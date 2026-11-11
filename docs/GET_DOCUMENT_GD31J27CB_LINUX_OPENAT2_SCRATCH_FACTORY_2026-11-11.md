# GD-31J.27cb — Linux openat2 scratch factory (лабораторный режим)

**Дата:** 2026-11-11. **Основа:** GD-31J.27ca, Draft PR #192. **Режим:** stacked Draft; производство выключено, releaseReady=false. Новые компоненты не подключены к OfficeRecoveryStore, GUP-2, EuroOffice SaveAs или WORM.

## Блок 1. Создание относительно удерживаемого Linux parent dirfd

Новый RecoveryLinuxJournalScratchFactory.CreateExistingParentLab **отказывается выполнять native create без явного лабораторного opt-in GD31J27CB_NATIVE_LAB=1**; флаг — только защита от случайного использования, не OS authorization. Метод получает:
- уже открытый и принадлежащий вызывающему коду SafeFileHandle каталога;
- строгое имя «32 строчных шестнадцатеричных символа GUID-N + .gdjournal.tmp»;
- CancellationToken.

Имя проверяется до syscall. Запрещены slash/backslash, NUL, traversal, произвольные расширения, заглавные hex-символы и пустой GUID. В модуле нет Path.Combine, File.Create, File.Open, File.Move или удаления.

На Linux x86_64/aarch64 (little-endian) вызывается **настоящий syscall openat2** относительно удерживаемого dirfd, c:
- O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC;
- файловым режимом 0600;
- RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV.

O_EXCL запрещает перезапись существующего имени, включая ссылки. Это НЕ полноценный транзакционный commit журнала/манифеста, защита от последующих rename или удостоверение доверенного parent. Указанный parent fd может быть в недоверенном mount/user namespace: происхождение factory не проверяет.

До openat2 проверяются ранее открытый directory handle, тип каталога, UID==geteuid, режим 0700, statx mount ID/File ID. DangerousAddRef удерживает parent fd на протяжении вызова и post-check. При EEXIST возвращается ошибка без перезаписи. ENOSYS/EINVAL трактуются как неподдерживаемый вариант. Любые ошибки не запускают очистку.

## Блок 2. Проверка именно нового файлового дескриптора

Непосредственно после создания проверяется **held fd** без открытия по имени:
- Linux statx(AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW);
- S_IFREG, права 0600, владелец UID, число жёстких ссылок 1, mount ID;
- полноценный RecoveryNativeFileId, отсутствие алиасинга с parent;
- сопоставление mount ID/device/UID с parent и повторная проверка parent;
- eUID не должен измениться во время операции.

FileStream передаётся через RecoveryLinuxJournalScratchLease; lease удерживает parent fd до Dispose и **никогда не удаляет directory entry**. InspectExistingHandles повторяет проверку held FD. Оба результата только диагностические: не подтверждена неизменность связи имени с inode на момент commit, trusted ancestry, POSIX ACL/LSM или writer isolation.

Если файл успели создать, а дальнейшая проверка, конструирование FileStream или отмена завершились ошибкой, файловые дескрипторы закрываются, **но созданное имя остаётся** для последующего разбирательства. Никаких попыток удалить его по имени.

## Блок 3. Windows — явный отказ

RecoveryWindowsJournalScratchGate сохраняет отказ:
- NativeRelativeCreateReady=false;
- CanCreateByPath=false;
- CanAuthorizeProductionCreate=false.

Небезопасного fallback на FileStream(path)/CreateFileW(path) нет. Для Windows отдельно потребуется проверенный нативный relative create от доверенного RootDirectory handle, полноценная проверка SID/ACL/effective token/reparse points, NTFS/SMB semantics и экспериментальные negative tests. Этот PR Windows создание не реализует.

## Блок 4. Тесты и ручные испытания

Подготовлено **16 C# MSTest**: формат имени, traversal, неверный UID/mode, подменённый File ID, mount/device, hardlink/alias, недействительный handle, Windows denial и один **opt-in** Linux-тест создания.

Linux-тест разрешается только при OperatingSystem.IsLinux() и GD31J27CB_NATIVE_LAB=1. Он создаёт уникальный тестовый каталог с режимом 0700, открывает его как dirfd, создаёт новый scratch, проверяет отказ повторного O_EXCL, оставляет файл после Dispose и удаляет тестовый каталог лишь в тестовом finally. Factory не выполняет test-fixture cleanup.

Подготовлено **13 Python source-only контрактов**, зарегистрировано 8 новых manual gates, всего **187**:

- gd31j27cb-astra-openat2-excl-no-follow-manual-e2e
- gd31j27cb-astra-existing-name-no-overwrite-manual-e2e
- gd31j27cb-astra-uid-mode-nlink-mount-manual-e2e
- gd31j27cb-astra-cancel-after-create-orphan-manual-e2e
- gd31j27cb-windows-relative-handle-create-pending-manual-e2e
- gd31j27cb-smb-crosshost-create-excl-manual-e2e
- gd31j27cb-astra-namespace-acl-lsm-ancestry-manual-e2e
- gd31j27cb-worm-and-path-delete-denial-manual-e2e

Команды для будущего **отдельно разрешённого** офлайн-стенда:

    python3 -m unittest discover -s scripts/tests -p test_recovery_linux_scratch_factory_contract.py -v
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryLinuxJournalScratchFactoryTests

Для явного запуска Linux fixture:

    GD31J27CB_NATIVE_LAB=1 dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ExplicitOptInLinuxScratchCreateIsExclusiveAndNeverDeletesOnDispose

Факт написания и регистрации тестов **НЕ означает** их выполнения.

## P0-блокеры до production

1. Не аттестовано получение доверенного parent fd от root, безопасность mount namespace, POSIX ACL/LSM и изоляция других писателей.
2. Нет atomic identity-bound commit имени journal/anchor/manifest или безопасной операции unlink/rename; openat2 с O_EXCL защищает только момент создания.
3. Windows relative file create не реализован и отсутствие fallback преднамеренно.
4. Нет защищённого монотонного поколения, независимого HMAC key и crash-safe anchor.
5. Новый factory пока не интегрирован даже с CA-потоком лаборатории; рабочий OfficeRecoveryStore.CaptureAsync продолжает использовать File.Move по имени (P0).
6. Автоудаление по пути остаётся выключенным, даже для «известных» остатков.

**Статус:** C# code + unit/source test files + docs подготовлены в ветке; .NET build/MSTest, Python unittest, Linux native tests, Windows/SMB, ENOSPC/SIGKILL/power-loss не запускались. ReleaseReady=false, PR Draft, main и Actions не менять.
