# GD-31J.27bj — аудит жизненного цикла аварийного восстановления

Дата: 2026-11-04. База: GD-31J.27bi, commit `210e48652b17989cecf516fc862c30c323f04c8e`, stacked Draft PR #174. Цель: проверить владение дескрипторами, конкурентные операции, отмену, временные артефакты и аварийное завершение процессов в цепочке GET Document/EuroOffice.

**Статус: аудит кода и исправления в изолированной ветке. Ни .NET build, ни MSTest, ни Python unittest, ни Windows/Astra/SMB/kill-тесты здесь не запускались.** Это не сертификат безопасности или выпуска. Не включать реальное восстановление, WORM commit, SaveAs и production native identity adapters. Ни `main`, ни пользовательские документы не изменены; GitHub Actions не запускать.

## Границы аудита и обязательная цепочка владения

1. `OfficeRecoveryStore`: существующее рабочее создание, выбор и удаление снимков — отдельный production-механизм с path-based контролями. Его не переписываем в этом изолированном этапе.
2. `RecoveryNativeIdentityGate`: разрешает только проверенный **уже открытый** read lease, основанный на OS handle identity. Фактические Windows/Linux adapters всё ещё `SupportsVerifiedDirectoryRelativeOpen = false`.
3. `RecoveryVerifiedStreamStage`: передаёт исходные байты в пустой **чужой** staging stream; владеет только исходным lease. Ни WORM, ни пользовательские пути не пишет.
4. `RecoveryPrivateCopyPublisher`: владеет приватной директорией, `.part.docx/.xlsx/.pptx`, переименовывает внутри неё без перезаписи, оставляет один FileStream на опубликованном объекте.
5. `RecoveryPrivatePublication`: владеет оставшимся source handle до завершения `ConsumeVerifiedToAsync`, не владеет потоком назначения. `FilePath` — справочный путь, не разрешение повторно открыть его как доверенный.
6. `RecoveryOwnedConsumerOutput`: владеет `CreateNew + DeleteOnClose` file-backed output и предоставляет единственный read-only scoped view; callback не получает путь или FileStream.

## Проверенные кодом дефекты и внесённые изменения

| ID | Приоритет | Наблюдение по исходникам | Действие в bj |
| --- | --- | --- | --- |
| BJ-01 | P1 | `RecoveryVerifiedStreamStage.Dispose()` до bj сразу закрывал `_lease` даже при незавершённом `StageToAsync`, поэтому pending `ReadAsync/WriteAsync` мог встретить закрытый дескриптор. | Исправлено в исходнике: собственный lifetime CTS, linked cancellation в I/O, state 0/1/2/3, единственный `CloseAndCleanup`, активный вызов закрывает lease в `finally`; `Dispose` только запрашивает отмену при state=1. |
| BJ-02 | P1 | `RevocableReadOnlyStream.Dispose()` отзывал wrapper, но не дожидался запущенного и забыто-неожидаемого `ReadAsync`. Final SHA/ZIP мог конкурировать с таким чтением по одному FileStream, а cleanup закрыть его под операцией. | Исправлено в исходнике: сериализация операций view; second concurrent read/seek отклоняется; отмена и `RevokeAndDrainAsync` ждут уже начатые операции **до** заключительной SHA/ZIP-проверки и закрытия owned handle. |
| BJ-03 | P1, блокер | `RecoveryPrivateCopyPublisher` после hard kill может оставить `.part` или приватный опубликованный файл; `Directory.CreateTempSubdirectory` в consumer может оставить пустую директорию, даже когда `DeleteOnClose` удалил файл. | **Не включали автоочистку:** невозможно безопасно решить владение по имени, возрасту или PID. Нужна отдельная согласованная операция осмотра, процессная/boot identity, native file-id, изоляция от живого процесса и журнал согласия на cleanup. |
| BJ-04 | P1, блокер | `RecoveryPrivatePublication.TryDeleteOwnRegularFile` сравнивает путь и SHA/ZIP, затем вызывает `File.Delete(path)`. Между проверкой и удалением возможна подмена; одинаковые байты не доказывают identity. | Оставлена консервативная path-based очистка только внутри случайного workspace; нельзя использовать в недоверенной production среде без нативной file-ID-bound delete. |
| BJ-05 | P1, production | `OfficeRecoveryStore.CaptureAsync`: в `catch` вызывается `TryDelete(temporaryCopy)` без флага фактического создания конкретного файла. Если export не создал путь, а другой актор поместил туда файл, имя само по себе не доказывает ownership. | Не изменяли production в stacked Draft. Перед развёртыванием нужна отдельная правка протокола exporter: `CreateNew`/владение дескриптором, контроль источника и отказ от удаления неподтверждённых файлов. Это отдельный release blocker. |
| BJ-06 | P1, production | `OfficeRecoveryStore.FindLatest/ReadValidCopy` проверяет held read handle, но возвращает путь после закрытия handle; последующий open может увидеть подменённый объект. `Discard/Prune` также имеют path-based deletion. | Без изменений в production; нельзя считать path receipt разрешением на реальный restore/delete без нативного identity-bound протокола. |
| BJ-07 | P1, проверка платформы | `FileOptions.DeleteOnClose`, `FileShare.None`, `Flush(flushToDisk:true)`, rename и файловые блокировки имеют отличающиеся NTFS, ext4 и SMB/серверные гарантии. | Оставлено fail-closed поведение, никаких утверждений о power-loss durability, no-follow, ACL, 0700 или гарантированном удалении без ручных платформенных подтверждений. |
| BJ-08 | P2 | Синхронная SHA/ZIP верификация до и после передачи может надолго задержать наблюдение `CancellationToken` на файлах до 128 МиБ. | Не вводили timeouts/Thread.Abort и не пропускали верификацию; требуется отдельная cancellable bounded-verify с неизменной целостностью и ZIP/XML лимитами. |

**Приоритеты:** P1 — препятствие к включению в производство или потенциальная порча/неверная идентичность объекта; P2 — контролируемый UX/latency риск. Исправление BJ-01/BJ-02 по исходникам требует подтверждения тестами и сборкой, не означает закрытие P1 в релизе.

## Матрица гонок, отмены и исключений

| Сценарий | Требуемый инвариант | Текущее покрытие |
| --- | --- | --- |
| `Dispose` перед первым `StageToAsync` | Нет чтения, lease закрывается ровно один раз | существующий MSTest + source contract |
| `Dispose` при незавершённом `staging.WriteAsync` | запрос отмены без раннего закрытия source; finally очищает stage/lease | новый `DisposeWhileStageWriteIsPendingKeepsSourceHandleUntilTransferStops` |
| Параллельные `StageToAsync` | второй отклонён, не закрывает handle/не трогает второй sink | новый `ParallelStageAttemptCannotStealOrCloseActiveSourceLease` |
| `ReadVerifiedAsync` callback запускает `ReadAsync` и возвращает, не await | revoke запрещает новые операции, дожидается начатого I/O до rehash | новый `RevocationDrainsUnawaitedPendingReadBeforeOwnerMayCloseHandle` |
| Два одновременных чтения/seek через один wrapper | второе отвергается, не меняя позицию в том же handle | новый `ConcurrentViewReadsAreRejectedWhileOneReadIsPending` |
| Callback бросает исключение/отмена во время callback | revoke + drain + handle close, никакой успешной квитанции | существующие synthetic tests; новые code paths требуют прогона |
| Rename/swap после предварительной проверки | не возобновлять trusted path opens; не удалять чужой объект | частично ограничено; нативный OS file ID gate пока отключён |
| Hard kill, SIGKILL, питание пропало, SMB disconnect | ни потеря user/WORM, ни неавторизованный orphan cleanup | **NOT RUN**, четыре новых ручных gates |
| Рестарт после crash; обнаружены private orphan folders | только read-only inventory; не трогать живые, чужие, linked объекты | **не реализован** private workspace reaper; отдельный этап |

## Категории временных объектов при смерти процесса

- Внутренняя `OfficeRecoveryStore` `.part`, `.json.tmp` и копия без манифеста: не считать завершённой. Уже есть bounded read-only `InspectIncomplete`. Не удалять автоматически.
- `getdoc-recovery-private-*`: если private `.part` или переименованный файл остался после kill, нет безопасного доказательства owner только по пути. Отложить cleanup до нативного протокола и офлайн-проверок.
- `getdoc-consumer-owned-*`: файл создан с `FileOptions.DeleteOnClose`. После terminate ожидается освобождение process handles, но фактическую семантику NTFS/ext4/SMB нужно проверить. Сам каталог может остаться пустым; не сканировать весь temp и не удалять рекурсивно.
- При network partition/SMB reconnect, антивирусной блокировке, изменении ACL и отказе `File.Delete`: ошибка/артефакт не свидетельствуют, что можно повторно открыть или удалить чужие данные.

## Обязательные последующие испытания (не выполнены)

1. `gd31j27bj-lifecycle-windows-ntfs-kill-manual-e2e`: kill до/после rename, при async read/write, удержание handle, ReadDirectoryChanges/reparse-point swap, DeleteOnClose и повторный запуск под ограниченным пользователем.
2. `gd31j27bj-lifecycle-astra-ext4-sigkill-manual-e2e`: SIGKILL, chmod/ACL/0700, nofollow/openat2, link swap, ext4 fsync(directory), остатки temp root.
3. `gd31j27bj-lifecycle-smb-disconnect-race-manual-e2e`: lost connection/reconnect, share locks, stale handles, rename/power-loss, replay/dispose; не выдавать успешную квитанцию при ошибке.
4. `gd31j27bj-orphan-cleanup-authorization-manual-e2e`: проверить read-only осмотр, false positive (живой процесс, одинаковый SHA, чужой файл, reparse point), no delete by age и отсутствие рекурсии.

В тестовой зоне использовать только синтетические файлы и выделенные временные каталоги. Обязательна дополнительная проверка .NET MSTest, Python source-only contracts, полного проекта Windows/Astra и SMB до любого merge/release.

## Gate и решение

- Изменения bj — только изолированная ветка/stacked Draft; не публиковать как production.
- `releaseReady = false`, manual acceptance `not-run`.
- WORM-оригиналы, действующие документы, `OfficeRecoveryStore` production и GUP-2 не меняются.
- Продолжить после одобренных offline tests и отдельного устранения BJ-03/BJ-04/BJ-05/BJ-06/BJ-07.
