# GD-31J.27ce — Windows read-sharing и проверка двух удерживаемых потоков

**Дата:** 2026-11-12. **Основа:** GD-31J.27cd, Draft PR #195. Все новые функции только лабораторные, без включения в OfficeRecoveryStore, EuroOffice, GUP-2, СЭД или WORM. releaseReady=false.

## Блок CE-1. Windows NT: доступ для читающих наблюдателей

В RecoveryWindowsJournalScratchFactory добавлен RecoveryWindowsScratchShareMode:

- Exclusive — режим по умолчанию, ShareAccess=0. Контракт CC сохранён.
- ReadOnlyObservers — отдельная лабораторная опция, ShareAccess=FILE_SHARE_READ (0x1), без FILE_SHARE_WRITE (0x2) и FILE_SHARE_DELETE (0x4).

ReadOnlyObservers требует **оба** флага GD31J27CC_NATIVE_LAB=1 и GD31J27CE_READ_SHARE_LAB=1. Неизвестный режим отклоняется до вызова NtCreateFile. Наличие FILE_SHARE_READ может допустить повторное NtOpenFile только при совместимости ранее запрошенных DesiredAccess/ShareAccess. Успешные NTFS/SMB испытания ещё необходимы. Доступ для читающих не превращается в право восстановления, публикации или удаления.

Документация Microsoft: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntcreatefile

## Блок CE-2. Отдельный Windows parent handle с правом создания

RecoveryWindowsScratchParentLabOpener.OpenConfiguredLabDirectory — лабораторное открытие **уже существующего** каталога. Запрашивает:
- FILE_ADD_FILE = 0x2;
- FILE_TRAVERSE = 0x20;
- FILE_READ_ATTRIBUTES = 0x80;
- READ_CONTROL = 0x20000;
- SYNCHRONIZE = 0x100000.

Отказ Windows при запросе этих прав является отказом всего этапа. Не запрашиваются DELETE или WRITE_DAC. Требуется строго операторский локальный абсолютный каталог, совпадающий с GD31J27CE_PRIVATE_LAB_DIRECTORY, и флаг GD31J27CE_PARENT_LAB=1. Запрещён fallback к общему TEMP, относительным и UNC путям. Каталог открывается через CreateFileW только с OPEN_EXISTING и FILE_FLAG_BACKUP_SEMANTICS / FILE_FLAG_OPEN_REPARSE_POINT, затем проверяется held directory tag, owner SID, DACL и File ID.

Важное ограничение: открытие родительского каталога по заданному пути не подтверждает неизменность всех ancestors, отсутствие junction выше и полный effective Windows AccessCheck. Оно разрешено только для отдельной лаборатории, не для рабочей системы.

## Блок CE-3. Чтение фактических байтов журнала и лабораторной квитанции

RecoveryHeldJournalManifestReadback.ReadOnHeldStreamsAsync принимает **два разных** caller-owned, заранее открытых seekable/readable Stream. Не создаёт/не открывает по имени/не удаляет/не изменяет файлы.

Порядок double-read:
1. Журнал — до 8192 байт, канонический JSON, цепь SHA-256, native File ID на FileStream (через RecoveryHeldJournalScratchLab).
2. Лабораторная квитанция — до 1024 байт, точный канонический JSON, GUID транзакции, journal head, заявленный SHA-256 копии, native File ID до и после чтения.
3. Повторное чтение журнала и квитанции и сравнение контрольных сумм, кадров и содержимого.
4. Сопоставление GUID, вычисленного из прочитанного журнала head и **предоставленного вызывающим кодом** copy SHA.

**Нельзя смешивать форматы.** Рабочий OfficeRecoveryStore.RecoveryManifest содержит SourcePath, CapturedUtc и Sha256. Новый RecoveryHeldLabManifestCodec содержит Version, TransactionId, JournalHeadSha256, CopySha256 и является **только лабораторной квитанцией**. Он не распознаёт рабочий манифест и не изменяет его.

Двойное чтение ловит некоторые изменения во время проверки, но не доказывает атомарное состояние пары файлов, подлинность цепи SHA, наличие доверенного ключа, сохранность каталожной записи после аварии или реальную целостность OOXML-копии. Результат MatchedTwiceUntrusted не является разрешением. CanRestore=false, CanDelete=false, CanPublish=false, HasTrustedManifest=false, HasVerifiedCopyContents=false.

## Блок CE-4. Тесты и обязательные проверки

**28 C# MSTest написаны:** 7 проверок политики Windows sharing, 8 проверок каталожного opener, 13 проверок фактического held-journal/lab-manifest readback. Сценарии включают повреждённый JSON, неизвестные поля, чужой GUID, неправильный hash, обрыв, превышение лимитов, отмену, изменение файла между чтениями, изолированный test-owned FileStream fixture.

**18 Python source-only unit tests** написаны, зарегистрированы до восстановления офлайн NuGet. Контракты CC/CD, которые ожидали literal ShareAccess=0, обновлены в рамках CE так, чтобы проверять старый default и новый opt-in вариант.

Добавлены **8 новых ручных gate, всего 211**:
- gd31j27ce-windows-exclusive-default-no-change-manual-e2e
- gd31j27ce-windows-readonly-sharing-observer-manual-e2e
- gd31j27ce-windows-add-file-traverse-parent-manual-e2e
- gd31j27ce-windows-reparse-ancestor-denial-manual-e2e
- gd31j27ce-windows-ntfs-no-write-delete-concurrency-manual-e2e
- gd31j27ce-held-journal-lab-manifest-reopen-manual-e2e
- gd31j27ce-smb-cross-host-share-and-checkpoint-manual-e2e
- gd31j27ce-worm-original-unchanged-manual-e2e

Команды для будущего одобренного локального стенда:

    python -m unittest discover -s scripts/tests -p test_recovery_ce_windows_share_held_manifest_contract.py -v
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryWindowsScratchSharePolicyTests
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryWindowsScratchParentLabOpenerTests
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryHeldJournalManifestReadbackTests

Опциональный тестовый FileStream выполняется только при GD31J27CE_HELD_READ_LAB=1. Очистку в тесте может выполнять лишь его собственный fixture, не модуль восстановления.

## P0 до выпуска

1. Внешне подтверждённый Windows AccessCheck + token privileges/MIC, безопасные ancestors/reparse, утверждённый доверенный каталог, SMB server-side semantics и блокировки пока отсутствуют.
2. ShareRead-only вариант должен пройти реальные тесты на NTFS/ReFS/SMB с конкурентными процессами. Двойное открытие имени само по себе не устраняет TOCTOU после последнего чтения.
3. Рабочий манифест пока не читается из held fd в его существующем формате, а фактический SHA OOXML-копии по удерживаемому fd не подтверждается.
4. Нет защищённого ключа/anchor, rollback-resistant generation и ОС-аттестации долговечности каталога после power loss.
5. Рабочий OfficeRecoveryStore.CaptureAsync всё ещё вызывает File.Move по пути (P0), а автоматическое удаление Discard/Prune заблокировано. Новые CE-модули не подключены к production.

**Статус:** все перечисленные C# и Python тесты подготовлены, но .NET build/MSTest, Python unittest и Windows/Astra/SMB E2E не выполнялись. В изолированной среде прямой доступ к github.com оказался недоступен. PR должен оставаться Draft, releaseReady=false; main и Actions не менять.
