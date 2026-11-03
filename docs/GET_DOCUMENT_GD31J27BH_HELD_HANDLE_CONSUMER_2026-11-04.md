# GD-31J.27bh — одноразовая выдача подтверждённой временной копии потребителю через удерживаемый дескриптор

**Репозиторий:** `avers2604/get-pdf`. **Ветка:** `feature/gd31j27bh-held-handle-consumer`, отдельный stacked Draft поверх GD-31J.27bg / PR #172.

**Ограничения:** ни сборка .NET, ни запуск MSTest/Python unittest, ни стенды Windows NTFS/Astra Linux/ext4/SMB не проводились. `main`, GitHub Actions, WORM, GUP-2, native EuroOffice SaveAs, рабочие документы и действующий `OfficeRecoveryStore` не менялись; `releaseReady=false`.

## Проблема

Предыдущий этап GD-31J.27bg сохранял удерживаемый `FileStream` опубликованной в личном временном каталоге копии, но наружу предоставлял только информационный `FilePath` и `Receipt`. Если потребитель повторно открывает путь, он может получить **другой файл** после конкурентного переименования. Кроме того, прежний `Dispose` мог закрыть удерживаемый дескриптор в момент будущего чтения.

Новый изолированный API **не открывает путь исходника повторно**; при передаче потребителю он работает с тем же `FileStream`, который был сохранён объектом `RecoveryPrivatePublication`.

## Реализовано

Изменён только изолированный модуль `src/GetDocument.Office/LibreOffice/RecoveryPrivateCopyPublisher.cs`:

- `RecoveryPrivatePublication.ConsumeVerifiedToAsync(destination, cancellationToken)` принимает только **новый пустой** читаемый, записываемый и seekable поток, принадлежащий потребителю. Непустые потоки отвергаются **без перезаписи и обнуления существующих байтов**.
- Перед первой записью в потребительский поток исходный удерживаемый дескриптор заново проверяется по **SHA-256 и всем ZIP/XML-ограничениям OOXML** с помощью `OfficeRecoveryStore.VerifyOpenedPackage(source,...)`. Сверяются размер и исходная квитанция.
- Затем копирование идёт по **64 КиБ** через удерживаемый поток, без открытия исходного пути. `IncrementalHash` вычисляет SHA-256 по **фактически отправленным байтам**, сверяется `Length` и `Receipt.Sha256`.
- После передачи заново проверяются длина, SHA-256 и ZIP/XML **уже в выходном потоке потребителя**. Только тогда возвращается новый `RecoveryStagedCopyReceipt`, без пути, прав публикации или полномочий восстановления/WORM.
- При ошибке/отмене receipt **не выдаётся**; только ранее подтверждённо пустой выходной поток обнуляется best-effort. Если очистка не удалась, вызывающий код обязан **отбросить весь результат**, а не использовать частично записанные байты. Метод не закрывает потребительский поток.
- Состояния `0 ready / 1 transferring / 2 consumed / 3 disposed`, `Interlocked.CompareExchange` и `_lifetimeCancellation` обеспечивают **одноразовость** и отказ от параллельных передач.
- `Dispose()` при активной передаче **запрашивает отмену**, но не закрывает дескриптор до выхода операции из `finally`. Завершившаяся передача сама закрывает/очищает исходный файл. Успех линеаризуется при переходе 1→2; вызов `Dispose` **после успешного перехода** не отменяет уже выданный результат. При конкурентном `Dispose` до него — ошибка, а не receipt.
- `CloseAndCleanup` однократно закрывает собственный дескриптор и очищает собственный временный файл/каталог по прежним правилам.

### Отдельная защита от подмены имени при очистке

В `TryDeleteOwnRegularFile(privateDirectory,filePath,expected=null)` добавлена **необязательная** сверка ожидаемых длины, SHA-256 и ZIP/XML по пути. `RecoveryPrivatePublication.CloseAndCleanup()` передаёт `Receipt`. Если опубликованный файл переименован, а под старым именем появился **другой файл**, несовпадение размера или SHA предотвращает его удаление. Прежняя проверка «дочерний путь только в своём каталоге», отказ от symlink/reparse/директории/устройства и нерекурсивное удаление пустого каталога сохранены. Пути для очистки незавершённого staging в `PublishAsync` по-прежнему используют старую консервативную схему владения.

**Критическое ограничение:** это **не атомарная идентификация файла по OS File ID**. Отдельное открытие пути в момент cleanup и `File.Delete(path)` всё ещё образуют TOCTOU. Злоумышленник, способный заменять файлы в личной временной области, может подменить файл между чтением SHA и удалением или поставить идентичную по содержимому копию. Для production требуется подтверждённая приватная ACL/0700, OS handle-bound identity/no-follow и отдельная проверка безопасного удаления.

## Написанные сценарии — НЕ запускались

В `tests/GetDocument.Office.Tests/OfficeRecoveryPrivatePublicationTests.cs` добавлены **8 MSTest** к прежним 7 (итого **15**) на синтетических DOCX и `FakeAdapter`:

1. `ConsumerCopiesVerifiedBytesFromHeldHandleAndReleasesPublication`: успешная передача подтверждённых байтов, освобождение опубликованной копии, неизменность пользовательского источника.
2. `SecondConsumerIsRejectedWhileFirstTransferIsInProgress`: вторая попытка чтения не может конкурировать с заблокированным первым потоком.
3. `DisposalDuringTransferCancelsWithoutEarlyCleanup`: закрытие владельца в момент копирования должно отменить операцию и оставить выход пустым.
4. `CancellationImmediatelyAfterWriteInvalidatesTarget`: отмена между записью и хэшированием не выдаёт receipt.
5. `NonemptyConsumerTargetIsNeverOverwrittenOrErased`: уже существующие байты потребителя остаются нетронутыми.
6. `CorruptedConsumerBytesFailIndependentShaZipAndClearTarget`: испорченный выход должен быть отвергнут после независимой проверки.
7. `VerifiedPublicationCannotBeConsumedTwice`: одноразовое владение.
8. `SwappedPathDoesNotChangeHeldSourceBytesOrDeleteForeignReplacement`: переименование опубликованного файла и появление чужого содержимого под старым именем **не меняет байты открытого дескриптора** и не разрешает очистке удалить отличающийся чужой файл.

Добавлен `scripts/tests/test_recovery_consumer_transfer_contract.py` — **8 source-only Python-контрактов** на отсутствие открытия по пути, двукратную проверку, отмену, cleanup и запрет регистрации в production. Ранее существующий `test_recovery_private_publication_contract.py` приведён к 15 C#-сценариям и новой форме освобождения `handle?.Dispose()`.

В `scripts/office_acceptance.py` добавлен шаг `recovery-consumer-transfer-source-tests` **до offline restore**, обновлены `test_recovery_chain_consistency.py` и `test_office_acceptance.py`. Ручные пункты остаются `not-run`:

- `gd31j27bh-consumer-windows-ntfs-manual-e2e`
- `gd31j27bh-consumer-astra-ext4-manual-e2e`
- `gd31j27bh-consumer-smb-race-manual-e2e`

`releaseReady=false`.

## Что не реализовано и не заявляется

- **Никакой продакшен-активации.** `OfficeRecoveryStore` не вызывает `RecoveryPrivatePublication` или `ConsumeVerifiedToAsync`; Windows/Linux native identity adapters всё ещё `SupportsVerifiedDirectoryRelativeOpen=false`.
- **Никакого восстановления оригинала**, WORM, редактирования или автоматической публикации на пользовательский путь.
- Нет доказанной защиты от враждебной одновременной записи в открытый исходный дескриптор: хэши и ZIP определяют пригодность фактически считанных байтов, но не дают атомарной транзакции над всеми участниками.
- Потребительский output остаётся у вызывающего кода. Его байты могут поменяться **после** возврата receipt; для реального применения нужен собственный lifecycle/commit-этап с удержанием дескриптора и независимой авторизацией.
- Не доказаны NTFS/ext4/SMB atomic rename, power-loss durability, Windows sharing, ACL каталога и своевременное освобождение ресурсов на всех ОС.

## Команды для будущего разрешённого стенда (НЕ запускались)

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_consumer_transfer_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_recovery_private_publication_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeRecoveryPrivatePublicationTests"
```

В будущей приёмке проверить также конкурентный `Dispose`, повторные потребители, чужие имена в каталоге, подмену path после открытия, отмену SMB во время записи и сохранность WORM. Запрет на реальное выполнение тестов пока не снят.

**Следующий этап GD-31J.27bi:** проект потребительского владельца конечного потока и безопасной приёмки без повторной проверки по имени файла. Настоящий commit и изменение пользовательских документов по-прежнему запрещены до OS-стенда и отдельного разрешения.
