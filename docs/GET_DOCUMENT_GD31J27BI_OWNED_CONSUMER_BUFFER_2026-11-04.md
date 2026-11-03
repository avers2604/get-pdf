# GD-31J.27bi — файловый владелец приёмного потока и ограниченный read-only callback

**Репозиторий:** `avers2604/get-pdf`. **Ветка:** `feature/gd31j27bi-owned-consumer-buffer`, отдельный stacked Draft поверх GD-31J.27bh / PR #173.

**Режим:** изменения исходников и записанные тесты, **без сборки .NET, запуска MSTest/Python unittest и Windows NTFS/Astra Linux/ext4/SMB-стенда**. GitHub Actions, `main`, `OfficeRecoveryStore`, пользовательские документы, WORM, GUP-2 и native EuroOffice SaveAs **не изменяются**. `releaseReady=false`.

## Почему потребителю недостаточно `Stream` + квитанции

GD-31J.27bh позволял одноразово перенести содержимое из удерживаемого recovery-дескриптора в **переданный пользователем пустой поток**, проверить SHA-256 и ZIP/XML и вернуть квитанцию. Однако после возврата метода этот поток **принадлежал потребителю** и мог быть изменён им или другим процессом; квитанция утрачивала смысл при конкурентной модификации. При выборе `MemoryStream` весь документ размером до 128 МиБ дополнительно удерживался в ОЗУ слабого рабочего ПК.

GD-31J.27bi вводит **отдельный внутренний владелец** результирующего потока, который не отдаёт потребителю файловый путь или сам `FileStream` и удерживает ресурс до конца одноразового обработчика.

## Реализованное решение

Новый `src/GetDocument.Office/LibreOffice/RecoveryOwnedConsumerOutput.cs`, **не подключённый к production**, содержит `RecoveryOwnedConsumerOutput` и вложенную `RevocableReadOnlyStream`.

### Создание и приёмка

`RecoveryOwnedConsumerOutput.CreateAsync(publication, cancellationToken)`:

1. Забирает владение ранее созданным `RecoveryPrivatePublication`, закрывая его и в успешном, и в аварийном сценарии.
2. Создаёт **новый случайный временный каталог** через `Directory.CreateTempSubdirectory("getdoc-consumer-owned-")`. Пользовательский путь не принимается.
3. Открывает **новый файл** `GUID.consumer` с `FileMode.CreateNew`, `FileAccess.ReadWrite`, `FileShare.None` и `FileOptions.Asynchronous | FileOptions.DeleteOnClose`. Ни один путь к новому файлу не возвращается потребителю. На NTFS режим `FileShare.None` блокирует обычные дополнительные открытия, но не заменяет native no-follow/ACL на Linux и SMB.
4. Вызывает `publication.ConsumeVerifiedToAsync(held, cancellationToken)` — передаёт байты **от уже удерживаемого источника** и использует прежние SHA-256/ZIP/XML проверки.
5. Выполняет `Flush(true)`, проверяет размер/seekability и **повторно проверяет SHA-256/ZIP/XML** самого приёмного `FileStream` после передачи. Возвращает объект-владелец только при полном соответствии квитанции.
6. Любая ошибка или отмена освобождает открытый дескриптор. `DeleteOnClose` должен удалять **открытый файл**, не требуя `File.Delete(path)`; затем предпринимается нерекурсивная очистка **только пустого** личного каталога. Если ОС/сетевая FS не поддерживает требуемый режим, **не вводится fallback к обычной очистке по пути**. Платформенное поведение ещё НЕ подтверждено.

### Единственный callback потребителя

`ReadVerifiedAsync<T>(Func<Stream, CancellationToken, Task<T>> reader, cancellationToken)`:

- Принимает одну асинхронную функцию-потребитель. Повторная либо параллельная попытка отклоняется.
- До первого вызова функции SHA-256 и весь ZIP/XML пакета **перепроверяются на том же удерживаемом дескрипторе**.
- Потребителю передаётся **не `FileStream`**, а `RevocableReadOnlyStream`: поддерживает чтение и позиционирование, но `CanWrite=false`, `Write/SetLength` выбрасывают `NotSupportedException`. Нет `FileStream.Name`, `SafeFileHandle`, пути назначения или права восстановить исходник.
- После возврата обработчика исходный дескриптор повторно проходит SHA-256/ZIP/XML проверку. Лишь затем возвращается результат пользовательской функции — **не разрешение на сохранение, перезапись или WORM commit**.
- После callback обёртка **отзывается** (`ObjectDisposedException` при новом чтении), буфер удаляется при закрытии удерживаемого дескриптора. Потребитель может вернуть полученные данные/метаданные, но должен считать их результатом **read-only анализа**. Передавать ссылку на обёртку дальше запрещено.
- `Dispose()` во время callback **сначала запрашивает отмену**, но не закрывает открытый дескриптор до выхода callback; cleanup выполняет активная операция после выхода. Если callback игнорирует токен и не возвращается, завершение cleanup также откладывается: принудительный `Dispose` не может безопасно забрать дескриптор посреди чтения.

### Граница безопасности

- На самом деле `FileOptions.DeleteOnClose` — семантика используемой файловой системы и .NET, а **не** универсальный механизм crash-safe уничтожения файла. Поведение при аварийном завершении, open-handle unlink, SMB reconnect, ACL, hardlink и отказе диска нужно проверить отдельно.
- `ReadVerifiedAsync` предоставляет read-only **обёртку**, но не изолирует исполнение делегата: вредоносный код с другими системными полномочиями способен совершать сторонние записи вне этого API. Также callback может успеть совершить побочный эффект **до** повторной SHA/ZIP-проверки. Поэтому этот API нельзя применять для commit и операций с WORM.
- Гарантия content consistency внутри read-only callback остаётся ограниченной: конкурентная запись нативным процессом или разрешённым OS-дескриптором возможна на некоторых FS. До настоящей проверки OS handle identity нельзя утверждать, что даже пред/пост-проверка делает любой промежуточный прочитанный фрагмент атомарным снимком.
- Неподтверждённые `WindowsRecoveryNativeIdentityAdapter` и `LinuxRecoveryNativeIdentityAdapter` **по-прежнему отключены**. Поэтому путь GD-31J.27bi доступен в настоящем состоянии только для синтетических тестов с `FakeAdapter`, но не для восстановления документов пользователей.

## Сценарии, написанные, но НЕ запускались

В `tests/GetDocument.Office.Tests/OfficeRecoveryOwnedConsumerOutputTests.cs` **9 C# MSTest**:

1. Успешный перенос в файловый owned-buffer, получение read-only потока, проверка байтов, отсутствие переданного пути, удаление после обработки.
2. Попытка сохранить обёртку `Stream` и прочитать после callback — отказ `ObjectDisposedException`.
3. Второе одновременное чтение и повторная попытка после завершения — отказ.
4. `Dispose` при заблокированном callback не удаляет файл преждевременно, после выхода отменяет результат и очищает.
5. Исключение из callback отзывает обёртку и закрывает файл.
6. Отменённое создание owner не создаёт новый каталог и сохраняет источник.
7. Квитанция с чужим SHA отвергается на синтетической публикации.
8. `Dispose` неиспользованного owner сразу очищает файл и блокирует чтение.
9. Оба реальных отключённых native identity adapters не могут создать исходную сессию.

Новый `scripts/tests/test_recovery_owned_consumer_contract.py`: **8 source-only Python методов** на `FileOptions.DeleteOnClose`, запрет внешнего пути, пред/пост SHA/ZIP, revocable view, отмену/cleanup, тестовые случаи и ручные гейты. Автоматический offline runner `scripts/office_acceptance.py` регистрирует их **перед `solution-restore`**, `test_recovery_chain_consistency.py` отслеживает уникальность, `test_office_acceptance.py` фиксирует:

- `gd31j27bi-owned-consumer-windows-ntfs-manual-e2e`
- `gd31j27bi-owned-consumer-astra-ext4-manual-e2e`
- `gd31j27bi-owned-consumer-smb-delete-on-close-manual-e2e`

Все три **`not-run`**, `releaseReady=false`.

### Будущие разрешённые проверки

```sh
# Не выполнялись.
python3 -m unittest discover -s scripts/tests -p test_recovery_owned_consumer_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_recovery_chain_consistency.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeRecoveryOwnedConsumerOutputTests"
```

На стенде: Windows 10/11 NTFS, Astra Linux/ext4, SMB. Проверить `FileOptions.DeleteOnClose`, `FileShare.None`, режим созданного каталога (0700/ACL), отсутствие утечки временных файлов, `Dispose` при блокирующих read, отмену, дополнительные открытия на SMB, поведение после crash и отсутствие повторного доверия к `FilePath`. При неподдерживаемом режиме — ошибка, а не переход к path-based удалению.

**Следующий этап GD-31J.27bj:** code-review данного контура от `RecoveryNativeIdentityGate` через временный файлообмен до read-only callback; аудит исключений/дефектов жизненного цикла и временного пространства, без подключения к реальному восстановлению. До реального OS-стенда и независимого разрешения никакого commit/WORM.
