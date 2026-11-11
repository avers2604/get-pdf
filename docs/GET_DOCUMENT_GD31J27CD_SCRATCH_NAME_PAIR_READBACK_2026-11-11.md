# GD-31J.27cd — сверка basename с удерживаемым файлом и диагностическая корреляция журнала/манифеста

**Дата:** 2026-11-11. **Базовая ветка:** GD-31J.27cc, Draft PR #194. **Статус:** stacked Draft, releaseReady=false. Запрещены любые automatic restore/delete/rename/cleanup или изменения WORM-оригиналов.

## Блок CD-1 — Astra Linux: read-only double openat2

RecoveryLinuxScratchNameIdentityProbe.ObserveExisting принимает два ранее открытых, **предоставленных вызывающим кодом** дескриптора:
- каталога (SafeFileHandle);
- временного файла-журнала (SafeFileHandle);
- строгое имя из RecoveryLinuxJournalScratchFactory.ValidateName.

С помощью DangerousAddRef оба descriptor wrapper удерживаются до завершения пробы. Проверяются UID, private mode 0700, parent statx, held scratch statx с owner 0600, nlink=1 и mount ID. Имя открывается **дважды независимо** относительно parent fd с Linux openat2 O_PATH | O_NOFOLLOW | O_CLOEXEC + RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV. Каждый вновь открытый handle читается через statx(AT_EMPTY_PATH), а исходный scratch и parent проверяются повторно.

RecoveryLinuxScratchNameIdentityProbe.Evaluate отличает:
- IncompleteEvidence: отсутствуют валидные ID, nlink, owner/mount;
- ParentChanged: изменился удерживаемый parent;
- CrossVolumeOrMount: том/mount/owner не совпадает;
- NameDoesNotMatchHeld: имя указывает на другой inode или происходит подмена между двумя открытиями;
- MatchedAtObservation: идентификаторы совпали **лишь в моменты наблюдения**, не навсегда.

Ни один результат не разрешает реальную запись, удаление или публикацию. Двойное открытие не устраняет гонку переименования **после последнего statx**. В Linux нет production integration.

## Блок CD-2 — Windows: read-only NtOpenFile по RootDirectory

RecoveryWindowsScratchNameIdentityProbe.ObserveExisting проверяет строгий basename через существующий CC ValidateName, parent/held File ID, owner SID и ACL evidence. В лабораторном режиме GD31J27CD_NATIVE_LAB=1 создаёт два **read-only name opens** через NtOpenFile с OBJECT_ATTRIBUTES.RootDirectory=heldParent, OBJECT_NAME=basename, OBJ_DONT_REPARSE и FILE_OPEN_REPARSE_POINT. Никакого path fallback, File.Move, File.Delete или rename.

Код дважды получает ID открытого по имени объекта, сопоставляет с удерживаемым файлом/томом и повторно проверяет parent owner/DACL/File ID. Reparse point, каталог, NULL DACL, broad/unknown/deny ACE или несогласованный identity означают отказ. NTSTATUS проверяется отдельно. Буферы UNICODE_STRING освобождаются, все возвращённые handle закрываются.

**Важный выявленный блокер:** GD-31J.27cc создал временный файл с ShareAccess=0, что по определению может блокировать его повторное открытие по имени, пока дескриптор созданного файла удерживается. STATUS_SHARING_VIOLATION (0xC0000043) в CD обозначает **отсутствие подтверждения**, а не совпадение. Требуется независимая квалификация безопасной share-политики с тестами конкурирующих процессов; самовольно менять её нельзя.

**Ещё один блокер:** RecoveryWindowsPinnedParentProbe.OpenForDiagnostics открывает родительский каталог с READ_CONTROL/FILE_READ_ATTRIBUTES. Для относительного открытия дочернего объекта требуется отдельно квалифицировать права на held parent, включая FILE_TRAVERSE/эффективный токен; существующий descriptor может оказаться недостаточным. Нельзя выводить из наличия owner SID/DACL разрешение на NT reopen.

Если имя открыто дважды и IDs совпали, это остаётся синтетически ограниченным событием наблюдения. После проверки возможна замена имени. Нет изоляции иных writer, независимого AccessCheck, directory durability NTFS/SMB или прав на будущий commit.

## Блок CD-3 — журнал и манифест

RecoveryScratchPairClaimReview — чистая функция, которая принимает **только переданные вызывающим кодом**:
- результат RecoveryCrashReconciler (статус воспроизведения/заявленный head);
- пару ID транзакций journal/manifest;
- заявленный SHA журнала из манифеста;
- заявленный SHA содержимого копии и SHA, указанный в манифесте;
- наблюдаемые (но пока не удостоверенные) copy/manifest metadata;
- два статуса проверки привязки имён.

Проверяет GUID, строгие 64 uppercase HEX SHA-256, статус journal claimed complete, совпадение имени каждого компонента и digest/transaction/head. Распознаёт:
- InvalidEvidence;
- UntrustedArtifacts;
- MissingComponent;
- JournalIncomplete;
- NameBindingUnverified;
- ManifestConflict;
- ClaimedConsistentUntrusted.

Это **не полноценная проверка манифеста**: ни байты JSON из файла, ни фактические хеши OOXML-копии, ни подлинность journal head, ни WORM-содержимое здесь не читаются. Все данные поданы извне; любой caller может их подделать. Даже ClaimedConsistentUntrusted никогда не разрешает restore, delete, publish.

## Блок CD-4 — тесты и приёмка

Подготовлено **37 C# MSTest**:
- 12 Linux ScratchNameIdentityTests (parent, mount, nlink, подмена в первом и втором lookup);
- 13 Windows ScratchNameIdentityTests (NTSTATUS share violation, reparse, DACL-границы, File ID и отсутствие разрешений);
- 12 ScratchPairClaimReviewTests (разрыв транзакции, журнал/манифест/копия, отсутствие компонентов, несходящиеся хеши).

Добавлены **13 Python source-only unittest** в scripts/tests/test_recovery_scratch_name_pair_contract.py и отдельный этап в scripts/office_acceptance.py **до** восстановления NuGet.

Добавлено **8 новых обязательных manual gate**, всего **203**, все новые — not-run:
- gd31j27cd-linux-name-replacement-between-opens-manual-e2e
- gd31j27cd-windows-nt-share-zero-reopen-manual-e2e
- gd31j27cd-windows-reparse-substitution-during-open-manual-e2e
- gd31j27cd-linux-held-parent-during-rename-manual-e2e
- gd31j27cd-smb-cross-host-name-replace-manual-e2e
- gd31j27cd-manifest-journal-digest-mismatch-manual-e2e
- gd31j27cd-restart-file-id-name-binding-manual-e2e
- gd31j27cd-worm-original-no-delete-manual-e2e

Для будущего одобренного офлайн-стенда:

    python3 -m unittest discover -s scripts/tests -p test_recovery_scratch_name_pair_contract.py -v
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryLinuxScratchNameIdentityTests
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryWindowsScratchNameIdentityTests
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryScratchPairClaimReviewTests

На этом этапе **нет** компиляции, действительного запуска MSTest/Python unittest, Windows/Astra/SMB E2E или fault-injection; проведена только исходная структурная проверка после записи на GitHub.

## P0 для будущих этапов

1. Windows: открыть производственно пригодный trusted directory handle с подтверждёнными ancestors/reparse/token/ACL и безопасной FILE_TRAVERSE/share-policy; NT name reopen с закрытым CC share mode 0 пока **не работоспособен** как система.
2. Linux/Windows: подтвердить устойчивую связь имени с held ID в течение commit, а не snapshot дважды; нужен явный OS-native протокол для атомарной публикации без replace.
3. Реально извлечь manifest/journal/copy из held FD и сверить bytes/SHA/OOXML, защищённый ключ HMAC, монотонный журнал, устойчивость файлового каталога и OS crash/power-loss E2E.
4. Не подключать новый код к рабочему OfficeRecoveryStore (path-based CaptureAsync/File.Move остаётся самостоятельным P0); Discard/Prune автоматически не удаляют.
5. Сохранить releaseReady=false, Draft PR и запрет включения GitHub Actions.

**Условие выпуска:** положительные результаты реальной Windows NTFS/ReFS, Astra ext4 и SMB квалификации, независимая аттестация прав доступа и отсутствие изменения WORM-оригиналов.
