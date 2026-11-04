# GD-31J.27bp — нативные File ID из открытых дескрипторов, no-follow диагностика и режим 0700

**Дата:** 2026-11-04. **База:** GD-31J.27bo / Draft PR #180. **Статус:** новый stacked Draft этап, без слияния с `main`, без выпуска.

Этот этап добавляет реальный исходный код системных вызовов для **чтения метаданных из уже открытых дескрипторов** Windows и Linux и диагностики соответствия имени lock-файла удерживаемому дескриптору. Это **не** реализация полноценной нативной доверенной файловой области и **не** доказательство того, что манипуляции с именами больше невозможны.

**Режим выполнения:** никаких сборок .NET, MSTest, Python unittest, GitHub Actions, запуска процессов, физического стенда, испытаний NTFS/Astra/ext4/SMB, SIGKILL и реальных вызовов нового кода в этом этапе. Все ручные проверки `not-run`; `releaseReady=false`.

## BP-01. Получение идентификатора из открытого OS handle

Файл `RecoveryNativeHandleIdProbe.cs` реализует внутренний **read-only диагностический** интерфейс `ReadFromExistingHandle(SafeFileHandle)`.

| ОС | Вход | Нативная операция | Возвращаемые сведения |
|---|---|---|---|
| Windows 10/11 | уже открытый `SafeFileHandle` | `GetFileInformationByHandleEx(..., FileIdInfo=0x12)` | `FILE_ID_INFO.VolumeSerialNumber` + **полный 128-битный** `FILE_ID_128` |
| Linux / Astra | уже открытый descriptor | `libc statx(fd, "", AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW)` | `stx_dev_major/minor` + `stx_ino`; дополнительно **требуется** `STATX_MNT_ID` |

Существующий тип `RecoveryNativeFileId` в Linux содержит составное значение `dev_major/dev_minor` и `inode`, **не включает `stx_mnt_id` в итоговую запись**: пока это лишь проверка присутствия mount ID. До аттестации нужно ввести расширенные evidence для mount namespace и смены монтирования.

Linux UAPI: буфер `statx` ровно 256 байт; обязательны возвращённые маски type/inode/mount ID, разрешён только тип regular file или directory, нулевые inode/device/mount ID отвергаются. Чтение поддерживается в прототипе только на **little-endian x64/arm64** с libc `statx`; старые ядра/libc и другие архитектуры должны отказать, а не подменять реальный inode значением `FileInfo`. Native fd закрепляется `SafeFileHandle.DangerousAddRef/DangerousRelease` на длительность вызова.

На Windows `FILE_ID_INFO` — структура 24 байта. Использование прежнего 64-битного `BY_HANDLE_FILE_INFORMATION` вместо 128-битного File ID **запрещено для ReFS**.

**Что это НЕ даёт:** файловый дескриптор может быть уже открыт по недоверенному пути, процесс может обладать чужим открытым handle, и равенство File ID не доказывает владение каталогом, проверенность ACL, отсутствие переименований или право записи/удаления.

## BP-02. Диагностика имени lock-файла без следования по последней ссылке

`RecoveryNativeLockNameProbe.VerifyCurrentName(heldLockHandle, absoluteLockPath)`:

1. Сначала получает реальный File ID **удерживаемого** дескриптора.
2. **Отдельно** открывает текущий путь только для метаданных:
   - Windows: `CreateFileW(FILE_READ_ATTRIBUTES, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT)`, затем `FileAttributeTagInfo` с запретом reparse и directory;
   - Linux: `open(O_PATH | O_NOFOLLOW | O_CLOEXEC)`, затем `statx(AT_EMPTY_PATH)` с проверкой типa.
3. Сравнивает обе пары volume/device + File ID. При несовпадении — `InvalidDataException`, при исчезновении имени/неподдерживаемом вызове — отказ.

Это позволяет обнаруживать **часть** подмен последнего компонента файла и не читать содержимое чужого документа. Однако проверка `absoluteLockPath` может пройти через подменённый родительский каталог. После повторного открытия имя снова может измениться. **Не реализованы** доверенный относительный open из защищённого родительского handle, его owner/ACL attestations, атомарность name-to-handle check с последующим действием, защита от mount namespace tricks и доказательство уникальности ID при inode reuse. Поэтому нативные прототипы намеренно **не подключены к** `OfficeRecoveryStore.CaptureAsync` и `RecoverySharedVolumeCoordinator`, а предыдущие `WindowsRecoveryCaptureIdentityAdapter` и `LinuxRecoveryCaptureIdentityAdapter` всё ещё имеют `SupportsVerifiedRootAndLockIdentity=false`.

## BP-03. Жёстче контролируем права каталога на Astra Linux

`RecoverySharedVolumeCoordinator` теперь создаёт **новые** каталоги координатора на Linux с `Directory.CreateDirectory(root, UnixFileMode.UserRead | UserWrite | UserExecute)`, то есть **0700**. После создания или при открытии уже существующего каталога проверяется ровно приватный режим (нет group/other read/write/execute, setuid/setgid/sticky). Чужой/старый каталог с режимом `0755` или `0775` отклоняется.

**Автоматического `chmod` существующего каталога нет.** Это осознанное fail-closed изменение: старые каталоги, созданные предыдущим этапом с менее строгими правами, перед эксплуатацией должны пройти безопасную ручную миграцию с проверкой фактического владельца/всех компонентов пути. Наличие 0700 само по себе **не доказывает** корректность UID владельца или безопасность при root/ACL-bypass. Реальная Windows DACL/Owner SID политика остаётся невнедрённой.

## BP-04. Тестовые спецификации — ТОЛЬКО НАПИСАНЫ

- 9 новых C# MSTest в `RecoveryNativeHandleIdProbeTests.cs`: Linux statx layout/fields/masks, нулевые ID, тип symlink, закрытый handle, стабильность двух наблюдений и независимых файлов, Windows 128-битный размер.
- 5 новых MSTest в `RecoveryNativeLockNameProbeTests.cs`: неверный путь и закрытый дескриптор, неизменённое имя, подмена содержимого по тому же имени, символическая ссылка и исчезновение имени. OS-gated случаи требуют реальных испытаний позднее.
- 3 дополнительных MSTest в `RecoverySharedVolumeCoordinatorTests.cs` (8 → 11): новый каталог 0700, отказ без chmod на режиме с групповым чтением и отказ при групповом доступе на запись.
- Новая исходная проверка `scripts/tests/test_recovery_native_handle_probe_contract.py` и обновлённый старый source-only контракт BO; runner/stacked consistency также обновлены. Ни один тест не выполнялся.

Новые ручные offline gates (все `not-run`):

1. `gd31j27bp-windows-full-fileid-ntfs-manual-e2e`: реальные `FILE_ID_INFO` 128 бит, NTFS/ReFS и семантика при rename/replace.
2. `gd31j27bp-astra-statx-ext4-manual-e2e`: `statx(AT_EMPTY_PATH)`, mask/mount ID, x64/arm64, старое Astra ядро/libc, умышленно неверные дескрипторы.
3. `gd31j27bp-nofollow-lock-rename-race-manual-e2e`: symlink/junction, смена последнего компонента/родителя, File ID mismatch, pinned handle, отсутствие записи в чужой файл.
4. `gd31j27bp-linux-coordinator-private-acl-manual-e2e`: 0700 новых каталогов, безопасный отказ для 0755/0775 старых каталогов и ручная миграция с владельцем UID.
5. `gd31j27bp-windows-dacl-owner-sid-manual-e2e`: проверка политики DACL, owner SID и неподконтрольных учётных записей. **Механизм Windows DACL ещё не реализован; gate заведомо не пройден.**

## BP-05. Сохранённые P1 и решение

Нужны нативный **directory-relative no-follow** из проверенной корневой директории; Windows ACL/Owner SID и Linux UID/namespace ownership; изоляция писателей, синхронизация между разными профилями пользователя/процессами, устойчивость к массовой подмене имён, подтверждённое поведение NTFS/Astra, реальное глобальное дисковое резервирование вместо `DriveInfo.AvailableFreeSpace`, проверка `FindLatest/Discard/Prune`, fsync/durability. Нативные возвращаемые File ID, даже если сравниваются равными, не дают разрешения на автоматическое удаление временных файлов.

**Итог:** оставить Draft и `releaseReady=false`; не включать production native identity gate, автоочистку, WORM-публикацию и deployment, пока platform gates не завершены.
