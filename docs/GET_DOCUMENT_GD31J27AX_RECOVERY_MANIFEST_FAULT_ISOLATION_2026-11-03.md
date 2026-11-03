# GD-31J.27ax — изоляция ошибок манифестов и безопасная очистка временных файлов

**Репозиторий:** avers2604/get-pdf. **Этап:** stacked Draft поверх GD-31J.27aw / PR #162. **Режим:** без сборки, без запуска тестов, без GUI/Windows/Astra/SMB-стенда. Только точечные изменения C#, тестовые сценарии и статическая проверка. `main`, GitHub Actions, native EuroOffice, GUP-2, WORM и пользовательские исходники не изменены.

## Подтверждённые пробелы обработки ошибок

### 1. Исключение SecurityException при чтении одного recovery-кандидата

`FindLatest(sourcePath)` перечисляет `*.json` в каталоге данного документа. Для каждого имени вызывает `ReadValidCopy`, а тот `ReadManifest` и `EnsureValidPackage` / SHA-256. До GD-31J.27ax оба обработчика отдельных кандидатов перехватывали `IOException`, `UnauthorizedAccessException` и ошибки формата, но **не** `System.Security.SecurityException`. При файловой политике, запрещающей чтение одного JSON/OOXML, такое исключение могло оборвать общий обход и скрыть другие корректные recovery-копии.

**Исправление:** в локальные, строго ограниченные `catch ... when` для `ReadManifest` и `ReadValidCopy` добавлен `System.Security.SecurityException`. В случае проблемы возвращается `null` **для кандидата**, а цикл `FindLatest` может продолжить поиск других копий. Отклонённый объект **не удаляется и не восстанавливается**.

Уже существовавшая защита от malformed JSON, `JsonException`, слишком большого JSON (более 2048 байт), несоответствующей метаинформации, изменённого OOXML или неверного SHA-256 остаётся. Добавлены отдельные C# тестовые сценарии на смесь корректного и повреждённых манифестов, а также на возврат к старой исправной копии при порче новой.

**Граница:** ошибки уровня **перечисления самого каталога** (`Directory.EnumerateFiles`) или недоверенного корня (`RejectLinkedDirectories`) по-прежнему не маскируются под «нет резервной копии»: это невалидная среда, а не повреждённый отдельный объект.

### 2. Ошибка очистки перекрывает исходное исключение экспорта

`CaptureAsync` вызывает `TryDelete(temporaryCopy)` и `TryDelete(temporaryManifest)` в ветке `catch`. Ранее `TryDelete` игнорировал `IOException` и `UnauthorizedAccessException`, но не `SecurityException`. Из-за этого отказ в очистке временного файла по политике безопасности мог **заменить исходную ошибку экспорта или отмены** и усложнить диагностику.

**Исправление:** `TryDelete` обрабатывает `System.Security.SecurityException` как ожидаемый отказ best-effort cleanup. Добавлен только `internal` параметр `Action<string>? deleteFile = null` для детерминированных тестов, позволяющий имитировать ошибки без изменения реальной файловой системы. При обычном вызове без параметра по-прежнему используется `File.Delete`. Неожиданные исключения (например, `InvalidOperationException`) **не подавляются** — это могло бы скрыть ошибку программирования.

**Ограничение:** эту функцию оставляем только для существующей очистки после ошибок `CaptureAsync`; алгоритм удаления *проверенных* recovery-пар из GD-31J.27aw (`DeleteVerifiedPair`, копия → манифест, reparse checks) не заменён и не ослаблен. Внешний exporter всё ещё создаёт временный OOXML по пути; для полного доказательства владения против конкурентной подмены нужен OS-handle-bound механизм.

## Написанные регрессии

В `tests/GetDocument.Office.Tests/OfficeRecoveryStoreTests.cs` добавлены **2** сценария:

- `FindLatestSkipsMalformedAndOversizedJsonAndKeepsValidCopy`: валидный синтетический DOCX рядом с JSON неправильного синтаксиса, JSON более 2048 байт и JSON неверных типов. `FindLatest` должен обнаружить валидную копию, не меняя посторонние JSON и исходник.
- `FindLatestFallsBackToOlderValidCopyWhenNewerIsCorrupted`: два корректных синтетических XLSX снимка; после подмены байтов нового файла выбирается старый, а сомнительная копия не удаляется.

В новом `tests/GetDocument.Office.Tests/OfficeRecoveryCleanupFailureTests.cs` добавлены **4** сценария с тестовыми делегатами:

- `IoCleanupDenialDoesNotReplacePrimaryExportFailure`;
- `AccessDeniedCleanupDoesNotReplacePrimaryExportFailure`;
- `SecurityPolicyCleanupDenialIsBestEffort`;
- `UnexpectedProgrammingErrorsAreNotSilentlyIgnored`.

Это **не исполнение настоящего экспорта** и не доказательство работы с системными ACL. Тесты проверяют функцию очистки с искусственной ошибкой; её имя отражает желаемую политику, а не проверяет полный pipeline экспорта.

В `scripts/tests/test_recovery_chain_consistency.py` добавлены **3 source-only unittest-метода**; их общее количество стало 18. Тесты проверяют ограниченный per-candidate catch `SecurityException`, семантику default `File.Delete` и injectable callback, наличие C# сценариев. Старый статический split обновлён из `private static void TryDelete` на `internal static void TryDelete`; проверка уникальности платформенных manual gates расширена до суффикса `ax`.

## Приёмка и ограничения

В `scripts/office_acceptance.py` и `scripts/tests/test_office_acceptance.py` добавлены обязательные ручные пункты со значением `not-run`:

- `gd31j27ax-recovery-manifest-fault-windows-ntfs-manual-e2e`;
- `gd31j27ax-recovery-manifest-fault-astra-smb-manual-e2e`.

`releaseReady=false`; синтетические C# сценарии и Python source-only контракты **не запускались**. Также не выполнялись сборка .NET, MSTest, реальный Windows/Astra GUI, SMB отказ, проверка ACL, тесты производительности и анализ реальных документов.

Для будущей проверки после снятия запрета:

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_chain_consistency.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeRecovery"
```

В реальных Windows NTFS / Astra Linux / SMB тестировать доступность одного manifest при наличии второго валидного, ошибки SecurityException и UnauthorizedAccessException, ошибки чистки после отменённого экспорта, неизменность оригинала и WORM. Не использовать реальные служебные документы: только изолированные синтетические копии.

## Следующая безопасная работа без сборки

GD-31J.27ay: анализ границ `FindLatest` при исчезновении каталога во время перечисления и верификация, что недоступный recovery-root никогда не интерпретируется как «пустой набор». Изменение ошибочной семантики требует тестов на сбои каталога, не просто строковых утверждений.
