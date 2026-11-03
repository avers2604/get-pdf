# GD-31J.27bf — передача проверенного дескриптора во временную копию без повторного открытия пути

**Репозиторий:** `avers2604/get-pdf`. **Ветка:** `feature/gd31j27bf-verified-handle-staging`, отдельный stacked Draft поверх GD-31J.27be / PR #170.

**Ограничения пользователя:** без сборки, без запуска Python unittest / MSTest, без Windows NTFS/Astra ext4/SMB-стенда. Компиляция и тесты в рамках данного этапа не выполнялись. `main`, GitHub Actions, WORM-оригиналы, GUP-2, EuroOffice-native SaveAs и текущий `OfficeRecoveryStore` не изменяются.

## Обнаруженный разрыв между проверкой и восстановлением

Этапы GD-31J.27bb–be подготовили проверку SHA-256 и ZIP через один `FileStream`, проверки подмены пути и контракты Windows File ID / Linux inode. Но `OfficeRecoveryCopy` по-прежнему содержит **путь**. Если после проверки файловый путь будет повторно открыт потребителем, это снова создаст TOCTOU-риск: открытый объект может отличаться от проверенного.

На текущем этапе разработан **изолированный промежуточный слой staging**, который получает **уже открытый** дескриптор с предъявленной нативной идентичностью, переносит его байты в отдельный временный поток и проверяет именно перенесённое содержимое. Его задача — подготовить проект протокола без подключения к действующему восстановлению.

## Реализованный C# модуль

Новый `src/GetDocument.Office/LibreOffice/RecoveryVerifiedStreamStage.cs` содержит:

- `RecoveryVerifiedStreamStage.Open(adapter, key)`: единственный заводской метод получения исходного дескриптора. Вызывает `RecoveryNativeIdentityGate.OpenVerifiedRead`; не принимает непроверенный путь или `FileInfo`.
- `RecoveryNativeReadLease` остаётся у сессии до окончания единственной операции `StageToAsync`; затем закрывается и не может быть использован повторно.
- В `StageToAsync(staging, expectedSha256, extension, cancellationToken)` принимается **только новый пустой**, читаемый, записываемый и seekable временный поток. Не перезаписывает уже существующее содержимое и не открывает путь. Формат должен совпадать с basename в `RecoveryRelativeKey`, а ожидаемый SHA — состоять из 64 шестнадцатеричных символов.
- Копирование выполняется кусками по **64 КиБ** через `ReadAsync`/ `WriteAsync`; ограничение размера `128 МиБ`. SHA-256 вычисляется **инкрементально по фактически переданным байтам** (а не по ранее полученному пути или метаданным).
- После копирования SHA-256 должен совпасть с ожидаемым значением манифеста. Затем `OfficeRecoveryStore.VerifyOpenedPackage(staging, extension, digest)` заново проверяет **уже временный поток**: SHA, ZIP/XML и прежние ограничения OOXML. Это препятствует выдаче receipt по одному хэшу или по исходной ссылке.
- Только после успешной проверки возвращается `RecoveryStagedCopyReceipt(Length, Sha256, Extension)`. Это **не** операция сохранения, восстановления или публикации версии.
- Сессия **одноразовая**: повторное использование, в том числе после успеха, отклоняется. `Dispose` / `DisposeAsync` закрывают исходный дескриптор; при конкурентной отмене/закрытии операция не должна возвращать успешный receipt.
- На ошибке или отмене временный поток, который был **подтверждён пустым до начала записи**, best-effort обнуляется через `SetLength(0)`, а исходный lease закрывается. Чужие непустые потоки не очищаются. Временный поток остаётся принадлежащим вызывающему коду и **не закрывается** автоматически.
- Если очистка staging не удалась из-за ACL/IO, его состояние **не считается доверенным**: вызывающий код обязан отбросить такой staging. Участок передачи не выполняет автоматическое удаление пользовательских документов.

**Критически важно:** `RecoveryNativeIdentityGate` подтверждает лишь согласованность полей evidence, предъявленных адаптером, а не настоящую корректность нативного системного вызова. Windows/Linux production-адаптеры остаются **отключены** (`SupportsVerifiedDirectoryRelativeOpen=false`, `PlatformNotSupportedException`). Поэтому новый механизм можно проверить с синтетическим `FakeAdapter`, но **невозможно включить** как полноценный безопасный native restore без отдельной OS-реализации.

## Безопасность и границы решения

| Этап | Что подтверждается | Что ещё не гарантируется |
|---|---|---|
| Получение lease | Структурные свойства и предъявленная adapter identity | Реальность OS no-follow / root-relative open, пока адаптер отключён |
| Копирование | Байты из удерживаемого stream попали во временный output, без нового открытия исходного пути | Неизменность byte stream при malicious concurrent writer; при изменении байтов digest отсекает несоответствие |
| SHA и ZIP/XML | SHA от **фактически записанных** байтов и повторный SHA + OOXML validation staging | Абсолютная защита caller-owned staging от последующей конкурентной подмены |
| Успех | Возвращён receipt без пути и без публикации | Нельзя автоматически заменить исходник, WORM или создать версию документа |
| Ошибка/отмена | Receipt не выдаётся, lease закрыт, частичный staging очищается best-effort | Если staging внезапно стал недоступен, очистка может не выполниться — его нужно выбросить |
| Потребитель после этапа | Никаких production-вызовов нового класса | Нужен отдельный безопасный staging owner/commit протокол без повторного открытия имени |

**Ограничение публикации:** SHA-256 receipt относится к проверенному staging **на момент проверки**. При злонамеренном изменении caller-owned staging сразу после возврата он может устареть. Для следующего этапа потребуется непрерывное владение временным файлом/дескриптором до атомарного commit, запрет публикации до завершённой валидации, разграничение доступа и учёт WORM. Просто создать файл в общедоступном каталоге и переименовать его недостаточно.

## Написанные, но не исполнявшиеся регрессии

`tests/GetDocument.Office.Tests/OfficeRecoveryVerifiedStagingTests.cs`: **8 MSTest** с синтетическими DOCX/ZIP и `FakeAdapter`, который *имитирует*, но не доказывает нативное подтверждение:

1. `ValidCopyIsStagedFromVerifiedLeaseAndSourceHandleIsReleased` — перенос, digest, OOXML, закрытие исходного lease, staging остаётся открытым.
2. `WrongDigestCleansPrivateStagingAndClosesSourceHandle` — чужой SHA не выдаёт receipt и очищает новую staging.
3. `CorrectDigestOfInvalidZipIsNotARecoveryReceipt` — совпадение SHA без валидного OOXML не даёт результата.
4. `MidWriteCancellationReleasesLeaseAndInvalidatesStaging` — отмена после первой записи освобождает дескриптор и обнуляет partial staging.
5. `PreexistingStagingBytesAreNeverErasedOrOverwritten` — непустой поток не трогается даже при ошибке вызова.
6. `AVerifiedSessionIsSingleUseEvenAfterSuccessfulTransfer` — нельзя дважды использовать один lease.
7. `DisposingUnconsumedSessionClosesSourceWithoutTouchingStage` — `DisposeAsync` освобождает источник.
8. `DisabledRealNativeAdapterCannotCreateAStagingSession` — Windows/Linux адаптеры пока не могут создать сессию.

Новый `scripts/tests/test_recovery_verified_staging_contract.py` — **8 source-only Python методов** на требование gate, запрет path reopen и мутаций, порядок hash/ZIP, ограниченную передачу, отмену, state machine и статус ручной приёмки.

Пункты в `scripts/office_acceptance.py` и `scripts/tests/test_office_acceptance.py`:

- `gd31j27bf-verified-staging-windows-ntfs-manual-e2e`
- `gd31j27bf-verified-staging-astra-ext4-manual-e2e`
- `gd31j27bf-verified-staging-smb-cancellation-manual-e2e`

Все **`not-run`**, `releaseReady=false`. Новый source-only gate зарегистрирован перед `solution-restore`; `test_recovery_chain_consistency.py` обновлён.

## Реальная приёмка (отложена до разрешённого стенда)

```sh
# Не запускались. Только для будущего изолированного стенда.
python3 -m unittest discover -s scripts/tests -p test_recovery_verified_staging_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_recovery_chain_consistency.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeRecoveryVerifiedStagingTests"
```

Отдельно подтвердить: Windows FileShare.Read, Astra Linux/ext4/SMB ACL, попытки модификации источника во время копирования, блокировку записи staging после валидации, аварийную отмену/обрыв SMB и сохранность WORM. Не проводить испытания на действующих документах.

**Следующий этап GD-31J.27bg:** проект staging owner и атомарной/идемпотентной публикации подтверждённой копии, без внедрения в WORM или production restore до успешных реальных испытаний.
