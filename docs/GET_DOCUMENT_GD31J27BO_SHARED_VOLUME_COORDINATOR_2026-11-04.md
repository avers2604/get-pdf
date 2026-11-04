# GD-31J.27bo — общий локальный координатор аварийных копий по тому

Дата: 2026-11-04. База: GD-31J.27bn / Draft PR #179. Это промежуточная stacked Draft-ветка, **не production release**.

**Ограничения разработки:** внесение исходников и тестовых спецификаций в GitHub; `dotnet build`, MSTest, Python unittest, GitHub Actions, локальные процессы и любые Windows/Astra Linux/SMB испытания **не выполнялись**. Все ручные gates — `not-run`, `releaseReady=false`, `main` не изменён.

## BO-01. Что реализовано в исходниках

Новый `RecoverySharedVolumeCoordinator` создаёт постоянный, пустой `.getdoc-volume-<SHA256(VolumeKey)>.lock` **в общем каталоге для приложения и локального пользователя**, по умолчанию `LocalApplicationData/GET-Document/recovery-volume-coordinator`.

Ключ `VolumeKey` получен от `DriveInfo` как полный путь точки монтирования/диска. Все recovery roots, сопоставившиеся с одним и тем же ключом **на одной машине и под одной учётной записью**, должны использовать **одно имя** координатора. На Windows ключ нормализуется по регистру перед SHA256; файл не содержит документов, путей, JSON или персональных данных.

Вызов `RecoveryCaptureVolumeGate.AcquireAsync` теперь выполняет следующую цепочку:

1. Отказ при отмене, отключённых файловых блокировках, неподтверждённом томе или недостатке свободного места (BL/BM, порог 512 МиБ).
2. Захват process-local `VolumeGates` по `VolumeKey`.
3. Захват **общего файлового координатора тома** через `RecoverySharedVolumeCoordinator.AcquireAsync`: `FileStream(FileMode.OpenOrCreate, FileShare.ReadWrite)` → `Lock(0,1)`. При конфликте блокировки — ошибка до запуска экспортёра, без ожидания.
4. Захват process-local `LocalGates` и **старого отдельного lock-файла recovery root**; повторная проверка свободного места и того, что имя точки монтирования не поменялось.
5. `OfficeRecoveryStore.CaptureAsync` удерживает все блокировки через экспорт, SHA/ZIP/XML, манифест и `Prune`. `RecoveryCaptureVolumeLease.Dispose` закрывает root-lock, освобождает локальный semaphore, затем shared volume lock и volume semaphore.
6. После исключения/отмены освобождение проходит через `finally`; **никакие lock-файлы и остатки аварийных снимков не удаляются по pathname**.

Если процесс аварийно завершён, kernel обычно освобождает открытые file locks вместе с дескрипторами. После рестарта постоянный lock-файл используется повторно, **но не удаляется и не «очищается» по возрасту или PID**. Реальные краевые случаи crash, SIGKILL, NTFS/Astra и зависших сетевых томов остаются без испытаний.

Для тестов с подменой `Func<string, RecoveryCaptureVolumeObservation>` синтетический каталог координатора расположен внутри тестового root, если не передан явный `coordinatorDirectory`. Это **внутренняя** тестовая настройка; публичный `OfficeRecoveryStore` всегда использует действительные сведения о томе и стандартный LocalApplicationData.

## BO-02. Проверки каталогов, объёмов и ограничение гарантии

- На первом шаге отказ при известной ссылке, junction/reparse в дереве каталога координатора или на lock-файле. После открытия выполняются повторные path-based проверки. Файл lock обязан быть обычным и пустым; он **никогда не перезаписывается и не удаляется**.
- На Linux выполняется проверка, что конечный каталог координатора не предоставляет group/other права на запись. Для уже существующих каталогов **не применяется chmod**, чтобы не изменять чужие права по пути.
- Default LocalApplicationData должен сам находиться на распознанном **локальном** томе. Если это redirected/network/unknown mount — отказ. SMB/NFS не становятся разрешёнными от появления координатора.
- Это **кооперативный lock одного хоста/одного пользователя**: процессы должны использовать одинаковый алгоритм, `VolumeKey` и каталог LocalApplicationData. Процессы разных пользователей, разные user profiles, процессы с другим mount namespace или bind-mount алиасом могут НЕ координироваться.
- **FileStream.Lock на Linux — advisory**; реальная семантика разных процессов и при SIGKILL не доказана. Имя coordinator lock и `FileInfo.LinkTarget` не являются OS handle identity. Враждебная подмена inode/rename после проверки остаётся **P1 TOCTOU** до нативного Windows File ID/Astra openat2/fstat и доверенной ACL-изоляции.
- Свободное место **не зарезервировано**: 512 МиБ остаются предварительным наблюдением, а не аппаратным/файловым резервом. Сторонние процессы могут вызвать ENOSPC в любой момент. Нет байтовой квоты по всему тому, распределённого брокера и гарантий на power loss/SMB.
- `FindLatest/Discard/Prune` и path-based экспорт ранее выявленные проблемы не решены; `releaseReady=false`.

## BO-03. Тестовые сценарии, которые только написаны

Новый `RecoverySharedVolumeCoordinatorTests.cs` содержит **8 MSTest** для одинакового хеш-имени у одного тома, различия томов, перезапуска после Dispose без удаления lock-файла, предварительной отмены, символических ссылок, непустого файла, недопустимого ключа и linked-корня.

В `OfficeRecoveryCaptureVolumeGateTests.cs` добавлены **2 теста** (13 → 15): последовательный захват двух различных recovery roots через один тестовый coordinator root и отказ при symlink координирующего файла **до** создания второго root. Сохранена проверка того, что реальная межпроцессная семантика этими тестами **не доказывается**.

Source-only контракт `test_recovery_shared_volume_coordinator_contract.py` зарегистрирован в `scripts/office_acceptance.py` и `test_recovery_chain_consistency.py`; предыдущие BN/BM текстовые контракты обновлены под новый `RecoveryCaptureVolumeLease`. **Ни один тест не запускался.**

### Обязательные ручные gates (все `not-run`)

| Проверка | Обязательный результат до выпуска |
|---|---|
| `gd31j27bo-windows-two-process-same-volume-manual-e2e` | Два реальных процесса под одним Windows-пользователем, разные recovery roots на NTFS: ровно один writer, второй fail-closed |
| `gd31j27bo-astra-two-process-same-volume-manual-e2e` | То же для Astra/ext4 с разными PID и контролем advisory lock, включая SIGKILL |
| `gd31j27bo-crash-restart-stale-lockfile-manual-e2e` | После kill повторный процесс открывает **тот же inode**, старый файл не удаляется, данные не исчезают |
| `gd31j27bo-cross-user-appdata-acl-manual-e2e` | Разные пользователи, права на каталог, mount alias, symlink/junction, доступ к lock имени, отклонение небезопасных ситуаций |
| `gd31j27bo-enospc-admission-race-manual-e2e` | Два recovery roots и посторонний процесс записывают на один диск; ENOSPC не приводит к порче исходников/WORM или опасной очистке |

Все реальные испытания разрешены только на синтетическом офлайн-стенде с созданными для тестов документами, без пользовательских SMB данных.

## BO-04. Решение о релизе и следующий P1

**Draft, `releaseReady=false`; никаких merge/deploy/auto-delete.** Нужны: native identity-bound root+coordinator lock на Windows/Linux, доказанный корректный file-lock между PID на NTFS/Astra, проверка специальных mount namespaces и ACL, общий байтовый бюджет/reservations (либо сервисный broker), контролируемая обработка ENOSPC и отказов диска, durability/fsync и read/write path-TOCTOU в `OfficeRecoveryStore`. Присутствие C# кода на GitHub не означает, что эти требования выполнены.
