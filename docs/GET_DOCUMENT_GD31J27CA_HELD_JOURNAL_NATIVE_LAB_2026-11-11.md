# GD-31J.27ca — журнал на удерживаемых дескрипторах: лабораторные блоки

**Дата:** 2026-11-11. **Основа:** GD-31J.27bz, Draft PR #191. **Режим:** stacked Draft; не подключать к рабочему аварийному восстановлению до аттестации Windows/Astra/SMB. Выпуск запрещён (releaseReady=false).

## Блок CA-1. Реальная запись в чужой заранее открытый пустой Stream

RecoveryHeldJournalScratchLab.StageOnHeldStreamAsync принимает только **переданный вызывающим кодом**, уже открытый, пустой, seekable, readable/writable Stream (Length=0, Position=0). Модуль сам **не создаёт и не выбирает путь**, не открывает имя файла, не переименовывает, не удаляет и не закрывает поток.

Прежде чем записать первый байт, он проверяет полный канонический JSON журнала BY (до 8192 байт), GUID транзакции, вид операции, хеш-цепь и порядок всех событий через RecoveryCrashJournalReconciler + RecoveryNativeMutationPlanner. Пустой журнал не допускается.

Для WriteAsync используются именно **приватные проверенные байты**, а не переданный вызывающим кодом изменяемый массив: регрессионный тест изменяет исходный буфер непосредственно перед WriteAsync и проверяет отсутствие TOCTOU. Затем выполняется WriteAsync и FlushAsync. Для переданного FileStream вызывается **реальная попытка сброса файлового содержимого через Flush(flushToDisk:true)**. После записи байты читаются через тот же held Stream, повторно проверяются размер и побайтовое совпадение. Native File ID открытого FileStream сравнивается до/после через уже существующий RecoveryNativeHandleIdProbe. При отмене или частичной записи **нет rollback, SetLength, unlink, File.Delete**: данные сохраняются на ответственности владельца потока для диагностики.

Успешная квитанция **не** доказывает OS durability, сохранность имени, trusted ownership, достаточность ACL или реальную безопасность: ContentDurabilityVerified=false, DirectoryEntryDurabilityVerified=false, CanAuthorizeRecovery=false.

**Ограничение доверия:** пустота потока не означает его безопасное происхождение. Любой вызывающий модуль может передать FileStream к пустому чужому файлу; до появления доверенного native factory этот лабораторный модуль нельзя включать в production.

## Блок CA-2. Ограниченное повторное чтение

RecoveryHeldJournalScratchLab.ReadOnHeldStreamAsync получает заранее открытый Stream (в том числе повторно открытый оператором тестовый файл), читает не более 8192 байт, заново проверяет канонический JSON, SHA chain, переходы событий и удерживаемый File ID до/после чтения. Никаких открытий по имени и прав на удаление/restore. Отчёт всегда CanRestore=false, CanDelete=false, CanPublish=false, HasTrustedPersistence=false.

## Блок CA-3. Linux: вызов fsync по удерживаемому каталогу

RecoveryHeldLinuxDirectorySyncLab.SyncExistingDirectoryForLab получает уже открытый SafeFileHandle каталога, проверяет Linux little-endian x64/arm64, UID владельца (текущий eUID), mode **0700**, statx identity/mount ID, делает DangerousAddRef, вызывает **настоящий libc fsync(dirfd)** и повторно читает statx с того же handle. На ошибку fsync, изменение UID/identity или отмену — отказ без удаления или fallback.

Здесь нужен каталог, предварительно открытый с O_RDONLY|O_DIRECTORY. O_PATH descriptor не подходит для fsync; ошибка ядра не скрывается. Возвращённая квитанция подтверждает лишь успешный вызов на данном fd, **не** долговечность изменений имени на носителе, политику монтирования, отсутствие гонок и безопасность доступа. Не доверять и не переиспользовать как production release gate.

На Windows **нет** эквивалентной подтверждённой directory-sync реализации в CA. FileStream.Flush(true) проверяет другой аспект. SMB/другой компьютер остаются отдельно неаттестованными.

## Блок CA-4. Тесты и приёмка

Подготовлены **27 C# MSTest** (16 held journal lab + 11 Linux held directory fsync) и **12 Python source-only unit tests**. Python-тесты зарегистрированы в scripts/office_acceptance.py до offline NuGet restore.

Добавлено 8 новых обязательных **ручных, не выполненных** gate, суммарно **179**:
- gd31j27ca-windows-held-file-content-flush-manual-e2e
- gd31j27ca-astra-linux-held-dirfd-fsync-manual-e2e
- gd31j27ca-windows-ntfs-fileid-reopen-manual-e2e
- gd31j27ca-astra-ext4-enospc-eio-interruption-manual-e2e
- gd31j27ca-smb-crosshost-name-swap-manual-e2e
- gd31j27ca-restart-truncated-journal-readback-manual-e2e
- gd31j27ca-windows-directory-durability-qualification-manual-e2e
- gd31j27ca-worm-original-untouched-manual-e2e

Тесты включают новый тестовый файл CreateNew с последующим повторным открытием и **только собственным test-fixture cleanup**, некорректный/усечённый JSON, пустой поток, чужую транзакцию, частичную запись с искусственной ошибкой, отмену до и после WriteAsync, повторное открытие, изменение File ID и ошибочный directory handle.

## Неустранённые P0-блокеры

1. Нет **доверенного OS-native factory** создания новых файлов под pinned parent FD / Windows trusted directory handle, O_EXCL/CREATE_NEW, no-follow/reparse, с проверенной writer isolation, ACL/LSM, безопасным lifetime и lock ownership.
2. Нет атомарного commit имени пары journal/anchor/manifest, directory fsync и доказанной защите от race между проверкой File ID и заменой directory entry. На Linux fsync файла сам по себе не доказывает durable directory entry: https://man7.org/linux/man-pages/man2/fsync.2.html
3. Нет защищённой серверной HMAC key/anchor monotonic storage и crash-safe generation после SIGKILL/power-loss; прежний BZ-план и CA stream-lab не соединены производственными проверяемыми квитанциями.
4. Нет реальных Windows/NTFS, Astra Linux/ext4, SMB cross-host, ACL/LSM, ENOSPC/EIO и power-loss испытаний, гарантий для выбранной файловой системы.
5. Рабочий OfficeRecoveryStore.CaptureAsync всё ещё содержит File.Move по пути (P0); Discard/Prune не удаляют файлы. ГЭТ Документ, GUP-2 и WORM не подключены к новым лабораторным модулям.

## Команды для будущего одобренного стенда

    python3 -m unittest discover -s scripts/tests -p test_recovery_held_journal_native_lab_contract.py -v
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryHeldJournalScratchLabTests
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryHeldLinuxDirectorySyncLabTests

**Статус:** исходники, тесты, ручные gate и документация подготовлены. Сборка .NET, запуск C#/Python тестов и реальные ОС-испытания **не проводились**. Main не менять, Actions не включать, PR оставить Draft, releaseReady=false.
