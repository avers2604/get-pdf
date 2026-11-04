# GD-31J.27bn — инварианты File ID корня и lock-файла, координация разных recovery roots на одном томе

Дата: 2026-11-04. Основание: GD-31J.27bm / Draft PR #178. **Stacked Draft**: без merge в `main`, без релиза и без подключения WORM/GUP-2/EuroOffice native SaveAs.

**Режим выполнения:** проанализированы и изменены файлы исходников GitHub; написаны синтетические C# MSTest и Python source-only контракты. **Ни одна сборка, MSTest, Python unittest, GitHub Actions, процесс, операция с реальным NTFS/Astra/SMB или crash simulation не выполнялись.** Все ручные gate — `not-run`, `releaseReady=false`.

## BN-01. Дополнительная локальная блокировка по смонтированному тому

В `RecoveryCaptureVolumeGate` добавлен **`ConcurrentDictionary<string, SemaphoreSlim> VolumeGates`**. Его ключ в рабочем коде — `Path.GetFullPath(drive.Name)` для найденного `DriveInfo`; в синтетических сценариях — явный `VolumeKey`, а при его отсутствии — собственный recovery root. Это дополнительная **внутрипроцессная** блокировка.

Порядок начала экспорта:

1. Отказать при отмене, ссылочном/недоверенном корне, отключённом файловом locking, неподтверждённом типе тома или свободном месте менее 512 МиБ.
2. Захватить semaphore по **тому** без ожидания (`WaitAsync(0)`) до `Directory.CreateDirectory(root)`; при конфликте — отказ, без создания нового каталога.
3. Под ним захватить прежний semaphore по **recovery root**.
4. Открыть прежний постоянный `.getdoc-capture.lock` и удерживать `FileStream.Lock(0,1)`. После lock перепроверить наличие/тип имени (managed-level sanity check, **не** File ID).
5. Повторно опросить том и свободное место уже под блокировкой; если `VolumeKey` изменился, отказать.
6. Передать оба semaphore и файловый handle в `RecoveryCaptureVolumeLease`; при завершении, исключении и отмене освободить в обратном порядке. Lock-файл по имени **никогда не удалять**.

Результат: разные **корни**, указывающие на один и тот же распознанный том, теперь *не могут одновременно начать экспорт в пределах одного процесса, если используют этот компонент*. Ранее контролировались только операции с одинаковым recovery root. Вспомогательные fake-наблюдения `VolumeKey` доступны исключительно внутреннему API тестирования, не через публичный конструктор.

**Это не распределённая координация.** Другой процесс с другим recovery root на том же диске НЕ разделяет `VolumeGates` и имеет другой lock-файл. На этот случай требуются отдельный доказанный нативный общий координатор, контролируемое хранилище или локальный брокер. Нет общей байтовой квоты, атомарного резерва, исключения записи сторонними приложениями и гарантии от ENOSPC. `DriveInfo` и путь монтирования не являются защищённым OS volume ID; подмена mount/lock-файла остаётся P1.

## BN-02. Обособленный контракт проверки OS File ID корня и lock-файла

Добавлен `RecoveryCaptureIdentityAttestation.cs` с `RecoveryCaptureIdentityRequest`, `RecoveryCaptureIdentityEvidence`, `IRecoveryCaptureIdentityAdapter` и `RecoveryCaptureIdentityGate.InspectForPlanning`.

Проверки, которые должен дать будущий реально работающий системный адаптер:

- Полный root path — только **запрос**, не доказательство доверия. Нужен root, открытый по проверенному родителю directory-relative и **no-follow**.
- `RootAtOpen == RootAtRecheck` по `RecoveryNativeFileId`, полученному из **открытых дескрипторов**.
- `LockAtOpen == LockAtRecheck == LockNameAtRecheck`: имя файла повторно должно соответствовать удерживаемому File ID. Одной проверки `File.Exists`, `FileInfo` или `FileStream.Length` недостаточно.
- Корень и lock на одном device/volume, все идентификаторы полные, платформы согласованы.
- Подтверждена невозможность сторонней записи/переименования в доверенном каталоге (`ParentWriterIsolationConfirmed`), OS no-follow, удержание handle, фактическая блокировка диапазона.

Метод возвращает только **структурное наблюдение**; он не создаёт системных прав, не возвращает дескриптор, не открывает документы и не предоставляет возможности удаления/изменения. Данные fake-адаптера могут утверждать что угодно и не являются доверенной аттестацией.

`WindowsRecoveryCaptureIdentityAdapter` и `LinuxRecoveryCaptureIdentityAdapter` имеют `SupportsVerifiedRootAndLockIdentity=false` и бросают `PlatformNotSupportedException`. **Они не подключены к `OfficeRecoveryStore` и `RecoveryCaptureVolumeGate`.** Нативный FileIdInfo/openat2/fstat/audited ACL код и реальные дескрипторы ещё предстоит реализовать и проверить отдельно. Защита от враждебной TOCTOU-подмены этим этапом НЕ доказана.

## BN-03. Проверки (написаны, НЕ запускались)

В `OfficeRecoveryCaptureVolumeGateTests.cs` добавлены **три** MSTest сценария (10 → 13): два разных каталога, но один `VolumeKey`; два разных `VolumeKey`; изменение идентификатора тома при повторном опросе, с проверкой освобождения.

В `RecoveryCaptureIdentityAttestationTests.cs` написаны **семь** синтетических MSTest: отказ системных адаптеров; неверный/относительный путь; подмена root ID; подмена link-name lock ID; отсутствие no-follow/изоляции/handle/lock; несогласованная платформа, устройство и нулевой File ID; положительные только **фальшивые** доказательства без реального действия.

Добавлен `test_recovery_volume_identity_contract.py` (source-only), обновлены `test_recovery_capture_volume_contract.py`, `test_recovery_chain_consistency.py`, `office_acceptance.py`. Исходники зарегистрированы в offline acceptance **до** restore, не исполнены. Никаких тестов было не запущено.

Новые ручные gates (все `not-run`):

| Gate | Что должно быть проверено в будущем |
|---|---|
| `gd31j27bn-volume-cross-root-windows-manual-e2e` | Два root на одном NTFS, конкуренция процессов и потоков, root rename/reparse, FileIdInfo |
| `gd31j27bn-volume-cross-root-astra-manual-e2e` | Astra/ext4, несколько процессов и UID, advisory lock, mount namespace, fstat/openat2 |
| `gd31j27bn-native-root-fileid-swap-manual-e2e` | Подмена имени lock при удержании descriptor, symlink и junction, denial без unlink |
| `gd31j27bn-volume-pressure-multi-process-manual-e2e` | Два разных recovery roots на одном разделе, одновременный экспорт, исчерпание места, SIGKILL, сохранность исходников |

Испытания только на синтетическом офлайн-стенде, без реальных пользовательских/SMB документов и WORM.

## BN-04. Нерешённые P1, запрет релиза

1. Межпроцессная **по всему тому** и межмашинная блокировка не существует. Текущий process-local VolumeGate не заменяет broker или нативный coordinator.
2. Нативная проверка root и lock File ID не внедрена, и использование managed path-check остаётся TOCTOU-sensitive.
3. Нет глобального байтового reservation; 512 МиБ — наблюдение, а не гарантия места. Порог BL 16/64 остаётся per-document.
4. Windows NTFS, Astra Linux/ext4 и SMB поведение lock/unlock, ACL, reparse, filename swap и SIGKILL не аттестованы.
5. Существующие `OfficeRecoveryStore.FindLatest/Discard/Prune` всё ещё имеют path-based риски и не должны расширять доступ к исходникам.

**Решение:** PR оставить Draft, `releaseReady=false`, `main` не трогать. Никакой автоматической очистки, удаления по pathname, истинного восстановления или WORM-коммита на основании этих наблюдений.
