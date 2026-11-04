# GD-31J.27bq — закрепление родительских дескрипторов и проверка UID / Windows Owner SID и DACL

Дата: 2026-11-04. База: GD-31J.27bp / Draft PR #181. Новый этап — **stacked Draft**, не для эксплуатации. Политика `releaseReady=false`.

**Статус доказательств:** исходники и синтетические тесты подготовлены; .NET build, MSTest, Python unittest, GitHub Actions, запуск native probes, реальное открытие NTFS/Astra/ext4, смена mount namespace, race-fault injection и SIGKILL **НЕ выполнялись**. Ни одна проверка на платформе не считается успешно пройденной. Пользовательские документы и WORM не затрагивались.

## BQ-01 — Linux / Astra Linux: directory-relative openat2 из удерживаемого родителя

Добавлен **`RecoveryPinnedLinuxParentProbe.cs`**, внутренний read-only диагностический слой. Он принимает **уже открытый `SafeFileHandle` родительского каталога**, а не произвольный путь, и:

1. Ограничивает дочернее имя точной грамматикой `recovery-volume-coordinator` либо 64-символьным шестнадцатеричным хешем без разделителей. Не принимает `..`, `/`, абсолютный путь, пути с `\\` или чужие имена.
2. Закрепляет parent fd через `DangerousAddRef` до освобождения выданного `RecoveryPinnedLinuxParentLease`. Никогда не закрывает caller-owned handle вместо его владельца.
3. Читает `statx(fd, "", AT_EMPTY_PATH|AT_SYMLINK_NOFOLLOW)` из закреплённого каталога; проверяет маски TYPE, MODE, UID, INO, MNT_ID, полный dev/inode, mount ID, тип `S_IFDIR`, `stx_uid == geteuid()` и режим **ровно 0700**.
4. Открывает дочернюю директорию **относительно этого fd** через `syscall(SYS_openat2=437, dirfd, basename, open_how)`, только на little-endian Linux x86-64/arm64. `open_how.flags = O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC`; `resolve = RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS|RESOLVE_NO_MAGICLINKS|RESOLVE_NO_XDEV`, без `O_CREAT`. Отказывает без fallback на `File.Open` / `openat` при старом ядре или неподдерживаемых флагах. Linux `openat2` предоставляет эти дополнительные ограничения на обход родительского каталога.
5. Читает UID/mode/mount ID дочернего fd и повторно проверяет parent fd. Изменения собственных сведений parent, межтомные переходы, ссылки, специальные типы, неполные данные или более широкие права приводят к отказу.
6. Возвращает read-only `RecoveryPinnedLinuxParentLease` с удерживаемым дочерним handle и ссылкой на caller-owned parent handle; освобождение через `Dispose`. Нет метода создания, переименования, удаления, `chmod` или записи.

**Граница доверия:** уже открытый родитель **ещё не подтверждён относительно надёжного системного anchor**. Каталог мог быть получен через атакуемый путь до вызова метода; удержание handle не защищает от переименования других компонентов и не запрещает посторонние операции из привилегированного процесса. Равенство UID/0700 и сохранение File ID не заменяют E2E-проверку ownership всей цепочки. Нет соединения с рабочим `CaptureAsync` и `RecoverySharedVolumeCoordinator`.

## BQ-02 — Windows: диагностическая фиксация каталога + Owner SID / DACL

Новые `RecoveryWindowsPinnedParentProbe.cs` и `RecoveryWindowsParentSecurityProbe.cs`.

- `OpenForDiagnostics(absolutePath)` открывает **существующий каталог** через `CreateFileW(FILE_READ_ATTRIBUTES|READ_CONTROL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT)` с удержанием `SafeFileHandle`. По `FileAttributeTagInfo` требует DIRECTORY и отсутствие конечного REPARSE_POINT. Это **не** NT relative open и не проверяет все предки.
- `ObserveExistingHandle(handle)` читает из этого же уже открытого объекта **полный `FILE_ID_INFO`**, `GetSecurityInfo(SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION)`, преобразует Owner SID в канонический текст через `ConvertSidToStringSidW`, освобождает возвращённые native-буферы через `LocalFree`. В записи есть `OwnerSid`, наличие ненулевого указателя DACL и `FileId`. **ACEs пока не разобраны**, token owner/effective groups, deny-only SIDs, SE privileges и integrity labels не оценены.
- `RecoveryWindowsParentSecurityGate.Evaluate` — **строго запретительный** контракт: отказ для неполного File ID, некорректного SID, несовпадающего владельца, NULL/неизвестного DACL; при совпадении владельца и ненулевом DACL возвращает только `RequiresExplicitAceAudit`. `CanAuthorizeCapture` всегда `false`. Ни одна проверка SID/DACL **не делает каталог доверенным** и не включает запись.
- Уже открытый Windows parent directory handle остаётся живым до `RecoveryWindowsPinnedParentLease.Dispose`. Повторное открытие имени и устойчивость ко всем reparse-подменам не реализованы; ограничение на **последний** компонент пути остаётся.

Старые адаптеры `WindowsRecoveryCaptureIdentityAdapter`/`LinuxRecoveryCaptureIdentityAdapter` по-прежнему `SupportsVerifiedRootAndLockIdentity=false` и не зарегистрированы в production. `RecoveryNativeLockNameProbe` остаётся отдельным read-only инструментом; его равные File IDs нельзя считать выдачей write/delete capability.

## BQ-03 — тесты только написаны, не запущены

- **7 MSTest** `RecoveryPinnedLinuxParentTests.cs`: синтетические statx UID/mode/mount и отказы, имена, закрытый handle, открытие каталога относительно удерживаемого fd, symlink отказ без изменения чужих данных.
- **9 MSTest** `RecoveryWindowsParentSecurityTests.cs`: SID/DACL/owner mismatch/NULL DACL/неполный File ID, строго запретительное решение, чтение owner SID через удерживаемый handle, Win32 open каталога и reparse отказ.
- **9 Python source-only проверок** `scripts/tests/test_recovery_pinned_parent_security_contract.py` зарегистрированы в `office_acceptance.py` и stacked `test_recovery_chain_consistency.py`; не запускались.

**Ручные gates (все `not-run`):**

| Gate | Обязательное будущее доказательство |
|---|---|
| `gd31j27bq-astra-openat2-pinned-parent-manual-e2e` | Kernel/arch поддержка `openat2`, fd retention, symlink, bind-mount, rename |
| `gd31j27bq-linux-uid-mode-mount-alias-manual-e2e` | UID/0700, ACL, расширенные атрибуты, mount namespaces, поведение root |
| `gd31j27bq-windows-held-parent-owner-dacl-manual-e2e` | File ID, Owner SID, DACL, закрытие handle, reparse в NTFS/ReFS |
| `gd31j27bq-windows-ace-privilege-denial-manual-e2e` | Действительный ACE effective rights, токены, владельцы, deny-only группы; **реализации пока нет** |
| `gd31j27bq-parent-rename-fault-injection-manual-e2e` | Подмена родителя и lock name во время захвата и после проверки; **сейчас это всё ещё риск** |

Только синтетические документы на изолированном стенде; без реального удаления пользовательских файлов.

## BQ-04 — статус P1, выпуск запрещён

**Не решено:** end-to-end доверие anchor; Windows security descriptor/ACE/owner SID effective-access evaluation; работа между учётными записями; race-free publication/deletion через OS-held handles; SMB/multi-host; реальная квота на диске, гарантия durabilty/fsync при сбоях; старые path-based `FindLatest/Discard/Prune`. Linux `openat2` на старом ядре должен отказать без небезопасного fallback. Существующий `RecoverySharedVolumeCoordinator` по-прежнему работает через path-based операции и **не использует** новые probes.

**Решение:** PR оставить Draft, `releaseReady=false`, никаких `main` merges, production native SaveAs, WORM/GUP-2 изменений, auto-delete, выпуска или включения непроверенных методов в рабочий путь восстановления.

Справочники: Linux [openat2(2)](https://man7.org/linux/man-pages/man2/openat2.2.html), Windows [GetSecurityInfo](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo). Они не заменяют платформенные испытания.
