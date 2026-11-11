# GD-31J.27cc — Windows NtCreateFile relative scratch (лабораторный этап)

**Дата:** 2026-11-11. **База:** GD-31J.27cb, Draft PR #193. **Режим:** stacked Draft; строго изолирован от OfficeRecoveryStore/GUP-2/EuroOffice/WORM; releaseReady=false; основную ветку и GitHub Actions не менять.

## Блок CC-1 — создание относительно удерживаемого каталога

Создан экспериментальный адаптер RecoveryWindowsJournalScratchFactory.CreateOnHeldParentForLab. Он принимает заранее открытый SafeFileHandle каталога, lowercase имя вида GUID-N (32 hex) + .gdjournal.tmp, ожидаемый owner SID и CancellationToken.

До любых Windows native вызовов требуется GD31J27CC_NATIVE_LAB=1. Флаг предотвращает случайное включение; **не** является авторизацией, изоляцией пользовательских прав или гарантом безопасности. Поддерживаются только Windows x64/arm64.

Проверка имени полностью исключает slashes, UNC, NT device paths, NUL, alternate data streams, переходы каталогов, upper-case и неподходящие расширения. В методе отсутствует создание по абсолютному пути и fallback на CreateFileW(path).

Собственный P/Invoke вызывает нативный NtCreateFile с OBJECT_ATTRIBUTES.RootDirectory, указывающим на caller-held directory HANDLE. OBJECT_ATTRIBUTES.ObjectName содержит только канонический basename в явно выделенном UNICODE_STRING. Используются:

- FILE_CREATE (2) — «только новый файл», никакой FILE_OPEN_IF, FILE_SUPERSEDE или FILE_OVERWRITE;
- FILE_NON_DIRECTORY_FILE, FILE_OPEN_REPARSE_POINT, FILE_SYNCHRONOUS_IO_NONALERT;
- OBJ_CASE_INSENSITIVE + OBJ_DONT_REPARSE (требует Windows validation: у документации OBJECT_ATTRIBUTES и NtCreateFile есть различие в формулировках);
- нулевой ShareAccess для лабораторного дескриптора и FILE_ATTRIBUTE_NORMAL;
- READ_DATA/WRITE_DATA/READ_ATTRIBUTES/READ_CONTROL/SYNCHRONIZE, чтобы проверить owner, DACL и FILE_ID_INFO на возвращённом handle.

Если NTSTATUS или IO_STATUS_BLOCK.Information не подтверждает FILE_CREATED, операция отвергается. Даже при ошибке после успешного создания объект **не удаляется и не обнуляется**: закрываются только принадлежащие этому модулю дескрипторы и выделенная память. NTSTATUS не приравнивается к Win32 GetLastError.

Документация Microsoft: https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile ; OBJ_DONT_REPARSE: https://learn.microsoft.com/en-us/windows/win32/api/ntdef/ns-ntdef-_object_attributes

## Блок CC-2 — проверка security attributes, DACL и File IDs

Адаптер повторно использует уже существующие недеструктивные модули:
- RecoveryWindowsParentSecurityProbe.ObserveExistingHandle — owner SID, наличие DACL и Windows native FILE_ID_INFO.
- RecoveryWindowsAclRiskObserver.ObserveExistingHandle — анализ ACE, NULL DACL, широких ALLOW-write, неизвестных ACE и DENY без полноценной оценки effective access.
- RecoveryNativeHandleIdProbe.ReadFromExistingHandle — native File ID/volume без lookup имени.

До NT create проверяются тип каталога и отсутствие FILE_ATTRIBUTE_REPARSE_POINT через GetFileInformationByHandleEx(FileAttributeTagInfo), owner SID, present/non-null DACL и отсутствие широких/неразобранных ACE. Любая неполная информация отклоняется. Во время вызова родительский handle удерживается DangerousAddRef.

После создания (на НОВОМ handle) проверяются:
1. что это обычный файл, а не directory/reparse point;
2. полный FILE_ID_INFO нового файла, непустой ID и одинаковый volume с parent;
3. owner SID, наличие DACL, отсутствуют broad/unknown/deny ACE;
4. повторный File ID/owner/ACL parent и точное сравнение наблюдавшихся ACE;
5. повторный FileAttributeTagInfo parent, отсутствие reparse.

Метод EvaluateFileIdentity обнаруживает замену held parent File ID, alias child/parent и cross-volume. SameAclSnapshot сравнивает диагностические ACE-блоки до/после.

**Пределы:** совпадение File IDs и DACL snapshots ещё не гарантирует immutable ancestry, безопасность токена пользователя, conditional ACE, MIC/privileges, отсутствие параллельного writer, привязку имени к File ID при следующем открытии, отсутствие TOCTOU после проверок и долговечность имени после power loss. OBJ_DONT_REPARSE/FILE_OPEN_REPARSE_POINT нуждаются в NTFS/ReFS/SMB E2E на целевых ОС.

## Блок CC-3 — владение дескрипторами, отказ, очистка

RecoveryWindowsJournalScratchLease владеет созданным FileStream и одним DangerousAddRef родителя; Dispose закрывает FileStream и освобождает ссылку, **но никогда не вызывает удаление, rename, File.SetLength или publication**. Вызывающий код должен сохранять исходную оболочку SafeFileHandle открытой до Dispose lease.

При неуспехе после FILE_CREATED не предпринимаются ни восстановление, ни удаление по пути. Артефакт остаётся для ручной проверки.

Все итоговые permission flags false:
ProductionCreateEnabled, CanCreateByPath, CanAuthorizeCleanup, CanAuthorizePublication, HasVerifiedEffectiveAccess, HasTrustedAncestry, HasVerifiedReparseIsolation, HasVerifiedDirectoryDurability; а также CanAuthorizeRecovery/CanAuthorizeDelete/CanAuthorizePublish на lease.

## Блок CC-4 — тесты и ручные испытания

Написаны **18 C# MSTest** в RecoveryWindowsJournalScratchFactoryTests.cs: имя, UNC/ADS/traversal, неправильный SID, NULL DACL, широкие/неизвестные ACE, owner, совпадение File ID/volume, reparse tag, изменение DACL, opt-in, явный лабораторный fixture и deny-only результата.

Opt-in fixture запускается лишь на Windows при GD31J27CC_NATIVE_LAB=1 и **отдельной** переменной GD31J27CC_PRIVATE_LAB_DIRECTORY, указывающей на заранее созданный независимый private ACL-qualified тестовый каталог. Никакого fallback на обычный %TEMP%: он может иметь широкие ACL. Тестовый файл намеренно не удаляется в коде — оператор проверяет его после опыта. Даже этот тест не заменяет power-loss/ACL qualification.

Написаны **14 Python source-only контрактов** и зарегистрированы в scripts/office_acceptance.py до offline solution restore. Добавлены **8 ручных gate**, всего **195**:
- gd31j27cc-windows-nt-relative-create-exclusive-manual-e2e
- gd31j27cc-windows-reparse-junction-substitution-manual-e2e
- gd31j27cc-windows-ntfs-owner-dacl-token-privileges-manual-e2e
- gd31j27cc-windows-dacl-race-after-create-manual-e2e
- gd31j27cc-windows-ntstatus-handle-leak-manual-e2e
- gd31j27cc-windows-directory-fsync-powerloss-manual-e2e
- gd31j27cc-smb-crosshost-create-and-delete-race-manual-e2e
- gd31j27cc-worm-original-preservation-manual-e2e

Рекомендуемый запуск на одобренном офлайн Windows стенде:

    python -m unittest discover -s scripts/tests -p test_recovery_windows_nt_rooted_scratch_contract.py -v
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryWindowsJournalScratchFactoryTests

Нативный fixture требует явного предоставления GDPR31J27CC_PRIVATE_LAB_DIRECTORY (исправление: точное имя переменной **GD31J27CC_PRIVATE_LAB_DIRECTORY**) и флага GD31J27CC_NATIVE_LAB=1. Запрещено использовать production/shared recovery directory.

## Не закрытые P0

1. Доверенное открытие parent handle с проверкой всех ancestors/reparse, effective Windows token, AccessCheck/privileges/MIC/DACL для всех участников и SMB/server-side политики отсутствует.
2. Подтверждённый native commit journal/anchor/manifest, безопасный reconnect после рестарта, защищённая HMAC вершина, independent monotonic generation, честная Windows directory durability и power-loss тесты не выполнены.
3. Протокол журналирования ещё не подключён к рабочему OfficeRecoveryStore. CaptureAsync по-прежнему использует path-based File.Move (отдельный блокер). Discard/Prune не выполняют удаление по пути.
4. Лабораторный NT create не является разрешением на автоматический restore/cleanup/user files. WORM originals не изменяются.

**Фактическая проверка в этой сессии:** подготовка исходников и тестов, source read-back GitHub. **.NET build, C# MSTest, Python unittest, нативные NtCreateFile, Windows/SMB/E2E не запускались.** PR должен остаться Draft, releaseReady=false.
