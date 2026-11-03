# GD-31J.27bm — блокировка конкурентных снимков и предварительный резерв свободного места

Дата: 2026-11-04. База: GD-31J.27bl, Draft PR #177. Текущий этап — **stacked Draft**, не для развёртывания.

**Фактический статус:** исходники, C# MSTest и Python source-only проверки написаны; **.NET build, MSTest, Python unittest, GitHub Actions, запуск приложений, NTFS/Astra/SMB и crash/kill-тесты НЕ выполнялись**. `releaseReady=false`; все ручные gate — `not-run`. Не менять `main` и не подключать WORM, GUP-2, production native SaveAs или native deletion.

## BM-01 — одна кооперативная операция записи на recovery root

`RecoveryCaptureVolumeGate.AcquireAsync` используется в `OfficeRecoveryStore.CaptureAsync` **до** `InspectIncomplete`, `Directory.CreateDirectory(folder)` и передачи пути экспортёру. Освобождение происходит лишь при выходе из `CaptureAsync`: lock остаётся на протяжении чтения инвентаря, всех `await` экспортёра, SHA/ZIP/XML, переименований, манифеста и `Prune`.

- Для одного процесса используется `ConcurrentDictionary<string, SemaphoreSlim>` с ключом полного canonical recovery root. `WaitAsync(0, cancellationToken)` не даёт двум вызовам одного процесса одновременно пройти подготовку.
- Для разных процессов, согласованно использующих **один и тот же путь**, используется постоянный `.getdoc-capture.lock` в recovery root. `FileStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)` и `Lock(0,1)` удерживают файловую блокировку до `Dispose`, затем `Unlock` и закрытие handle. Lock-файл **никогда не удаляется**; это важно, чтобы не появлялись два разных inode с одним именем.
- Подозрительные корневые links/reparse points и lock-файл со ссылкой отклоняются, однако эти проверки **основаны на путях и подвержены TOCTOU**. Они не доказывают настоящую file-ID-bound изоляцию и не защищают от привилегированного конкурирующего переименования/подмены.
- Отсутствует ожидание занятого lock: второй запрос получает явную ошибку, без запуска экспортёра и без изменения чужих данных. При завершении процесса OS освобождает дескриптор; после restart повторно используется тот же постоянный lock-файл.

**Границы гарантий:** Unix file locks являются advisory и требуют сотрудничества всех участников. Возможность отключить locking настройкой .NET проверяется и ведёт к fail-closed. SMB/NFS/CIFS и неподтверждённые файловые системы блокируются; для нескольких машин общего серверного lock-протокола нет. Windows FileShare, Linux fcntl/flock, конкретная Astra Linux, NTFS, overlay/tmpfs и состояние после crash требуют отдельных реальных проверок. Это НЕ нативный защищённый sandbox и не полноценная транзакция.

## BM-02 — контроль свободного места всего тома до нового экспорта

Чтение выполняется через `DriveInfo.GetDrives()` с выбором самого длинного подходящего пути монтирования. `DriveInfo.AvailableFreeSpace` отражает пространство, доступное текущему пользователю. Два наблюдения требуются: **перед** созданием lock root и **внутри** уже удерживаемой блокировки.

Для допуска требуется не меньше **512 МиБ** свободного места, включающих запас относительно максимальной одной OOXML-копии **128 МиБ**. На Windows условно допускаются только локальные фиксированные NTFS/ReFS, на Linux — ext4/xfs/btrfs/overlay/tmpfs; это allowlist для разработки, **не подтверждение безопасности этих файловых систем**. Сетевые/неизвестные тома отвергаются `PlatformNotSupportedException`.

Производственный `OfficeRecoveryStore` использует только реальное наблюдение диска. Внутренняя `internal` перегрузка конструктора позволяет заменить его синтетическим наблюдением исключительно в unit-тестах. Нет публичного пользовательского параметра для обхода защиты.

**Точное ограничение:** два опроса свободного места не создают физического резерва. Между ними и самим `WriteAsync` любой чужой процесс может занять диск. Кроме того, запись через другой recovery root на том же устройстве не участвует в нашем lock. Новая схема даёт консервативный *предварительный* глобальный порог свободного места и root-wide сериализацию, **но не общесистемную квоту, не atomic disk reservation и не гарантию успешной записи**.

## BM-03 — ранее принятые ограничения сохраняются

- BL: per-source лимит **16** неполных артефактов и **64** всех записей, `Truncated` блокирует создание.
- BK: `CaptureAsync` не удаляет непроверенные `.part`, `.json.tmp` или финальные копии по pathname на rollback.
- BJ: источник и consumer сохраняют lifetime-handle до завершения async callbacks, не возвращают доверенный путь.
- Нативные Windows/Linux identity-delete адаптеры выключены. `FindLatest/Discard/Prune` остаются path/TOCTOU-sensitive; реального unlink нет.

## BM-04 — проверки написаны, не выполнены

В `OfficeRecoveryCaptureVolumeGateTests.cs` описаны **10** синтетических MSTest: одиночное владение и конфликт; освобождение с сохранением lock-файла; отмена до любых побочных действий; отказ при недостатке места и неизвестном томе; повторная проверка места под блокировкой; lock-symlink; освобождение при аварии; конкурирующие `OfficeRecoveryStore`; отказ экспортёру при низком свободном месте.

Новый текстовый контракт `test_recovery_capture_volume_contract.py` и регистрация в offline acceptance runner/stacked chain checks. **Ни один тест не был запущен**, включая Python.

Ручные gate (все `not-run`):

1. `gd31j27bm-capture-lock-windows-ntfs-manual-e2e`: два процесса, отмена, kill, locked-file rename, ACL, restart.
2. `gd31j27bm-capture-lock-astra-ext4-manual-e2e`: отдельные процессы и UID, advisory locking, SIGKILL, symlink swap, отключённые файловые блокировки.
3. `gd31j27bm-unsupported-smb-fail-closed-manual-e2e`: UNC/CIFS/NFS, повторное подключение сети, отсутствие ложного успешного lock.
4. `gd31j27bm-disk-pressure-cancel-crash-manual-e2e`: запас 512 МиБ, чужая запись в том же томе, ENOSPC, крайние объёмы OOXML, проверка целостности и сохранности оригинала.

Только синтетические данные на отдельном офлайн-стенде. **PR держать Draft; releaseReady=false**.

## Следующий P1

До релиза нужна доверенная системная граница владения root и lock-file inode (native no-follow/identity-bound root), проверка реального кросспроцессного locking NTFS/Astra, выработка механизма работы с SMB на нескольких ПК, управление общесистемным байтовым бюджетом и доказательство сохранности при power loss. Любые заявления, что эта реализация уже решает межмашинные гонки или гарантирует резервирование физического места, неверны.

## Справочные материалы .NET (не доказательство выполнения)

- [FileStream.Lock на Unix: advisory/shared semantics (.NET 6+)](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/filestream-file-locks-unix)
- [DriveInfo.DriveFormat на Linux в .NET 10](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/driveinfo-driveformat-linux)

Документация API не заменяет NTFS/Astra/SMB испытаний, и в данном этапе эти проверки **не проводились**.
