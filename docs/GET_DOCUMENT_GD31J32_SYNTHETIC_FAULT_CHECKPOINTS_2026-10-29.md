# GD-31J.32 — матрица отказов синтетического Save As и защита фиксации перед hardlink

**Дата:** 29 октября 2026. **База:** GD-31J.31, Draft PR #103. **Статус:** исходники и тесты подготовлены, но **не собирались, не выполнялись и не испытывались в Qt/CEF/VM**. Тестовый fault hook — имитация исключения в .NET, а не реальное аварийное завершение процесса или физическое отключение питания. Пользовательское сохранение и хранилище WORM не затрагиваются.

## Причина изменений

GD-31J.29 создаёт отдельную `pending`-копию в `reviewed/`, делает `Flush(true)`, фиксирует конечное имя через `File.CreateHardLink`, после чего удаляет только свою временную ссылку и проверяет SHA исходника и результата. GD-31J.30 добавил журнал `intent → pending_flushed → linked → completed`; GD-31J.31 — строго ограниченный read-only отчёт. Ранее в этих компонентах **не было механизма проверять каждую точку сбоя одного и того же метода `Commit`** — только ручной план аварийных испытаний.

Помимо этого, между `Append("pending_flushed")` и `CreateHardLink` существовало окно, в котором файл `pending` или журнал могли быть изменены: повторный SHA/статус после фиксации мог обнаружить проблему, **но уже созданное финальное имя нельзя безопасно автоматически удалить**. GD-31J.32 добавляет повторную проверку непосредственно перед созданием конечного имени; это уменьшает вероятность такой ситуации, но **не устраняет** гонки при конкурентном доступе к каталогу.

## Новый внутренний fault-probe

Добавлено перечисление `EuroOfficeSyntheticSnapshotFaultCheckpoint` и внутренний метод `EuroOfficeSyntheticReviewedSnapshot.CommitWithFaultProbe(stagingRoot, review, callback, labApproved=false)`. Метод доступен только test assembly через уже существующий `InternalsVisibleTo`, по умолчанию не разрешён и использует тот же `CommitCore`, что обычный `Commit`. **Нет** пользовательского CLI, UI, внешнего API, автоматического вызова, выключения процесса, сохранения реальных файлов или обхода ограничений `labApproved`, синтетического манифеста, структурного OOXML и квитанции CEF v2.

Тестовая точка вызывает зарегистрированный callback **после** соответствующего действия с файловой системой. Тест может бросить собственное исключение, имитируя обрыв последовательности. **Тесты не выполнялись**, таблица ниже определяет ожидаемые инварианты:

| Контрольная точка | Журнал | Pending | Final | Ожидаемая read-only классификация |
|---|---|---|---|---|
| `AfterIntentPersisted` | intent | нет | нет | `NoOutputObserved` |
| `AfterPendingFileFlushed` | intent | да | нет | `PendingNeedsReview` |
| `AfterPendingJournalFlushed` | pending_flushed | да | нет | `PendingNeedsReview` |
| `AfterHardLinkCreated` | pending_flushed | да | да | `PendingAfterLink` |
| `AfterLinkedJournalFlushed` | linked | да | да | `PendingAfterLink` |
| `AfterPendingLinkRemoved` | linked | нет | да | `UncertainAfterLink` |
| `AfterFinalHashVerified` | linked | нет | да | `UncertainAfterLink` |
| `AfterCompletedJournalFlushed` | completed | нет | да | `CompleteObserved` |

**Неизменные требования при каждом отказе:** SHA исходного синтетического fixture и quarantine совпадают со значениями до операции; никакая стадия, даже `completed`, не возвращает `SaveCompleted=true`, `CleanupAuthorized=true`, `AuthorizedForPublication=true` либо `DurabilityProven=true`; оператор может только анализировать состояние, а не автоматически исправлять файлы. При отсутствии `labApproved` fault hook не исполняется и журнал не создаётся. При чужой CEF-квитанции v2 операция отказывает до записи.

## Защита окна перед созданием конечного имени

До `File.CreateHardLink(destination, pending)` теперь повторно выполняются:
- bounded SHA-256 и длина pending-копии (должны совпадать с ранее проверенным OOXML-кандидатом);
- SHA-256 quarantine и исходного синтетического эталона (должны оставаться неизменными);
- `EuroOfficeSyntheticSnapshotJournal.RequireLastStage(attempt, "pending_flushed")`: журнальный JSONL должен содержать ровно две последовательные записи, соответствующие текущим `attemptId/sessionId/format/sha256/length/pendingName/finalName`; любое внешнее добавление строки, подмена полей, неверный GUID или откат стадий — отказ **до** hardlink.

Это не криптографический механизм аутентификации журнала: внутри общей ФС атакующий может менять файл **после проверки**. Нужны эксклюзивные права, каталог под отдельной учетной записью, fsync директорий, независимые файловые дескрипторы и операционная sandbox-изоляция, прежде чем даже рассматривать production-пути.

## Тесты и регламент принятия

Добавлены **не выполненные** MSTest:
- `EverySyntheticCrashBoundaryRemainsReadOnlyAndCannotClaimCompletion`: один и тот же реальный `CommitCore` в восьми инъекционных точках; проверяются наличие pending/final, точная стадия `Inspect`, классификация `EuroOfficeSyntheticRecoveryReport`, отсутствие удаления, неизменность эталона и quarantine.
- `DisabledLabCannotReachFaultHookOrWriteAnyJournal` и `NullHookOrUnapprovedRevokedEvidenceCannotGainWriteAuthority`: отсутствие побочных файлов при отключённой лаборатории/невалидной квитанции.
- `MutatingPendingBetweenJournalFlushAndHardLinkMustNotCreateFinalName`: внешняя подмена bytes pending после записи стадии `pending_flushed` ведёт к отказу до публикации.
- `MutatingJournalBetweenPendingFlushAndLinkMustNotCreateFinalName`: внесение чужой строки в JSONL отказывает до hardlink, а наблюдатель классифицирует журнал как `MalformedJournal`.
- `SuccessfulCommitWithoutProbeStillWritesExactlyFourStages`: обычный синтетический `Commit` без callback сохраняет существующую семантику (только диагностическая завершённость).

`test_synthetic_fault_checkpoint_contract.py` — статическая проверка, что восемь probes идут в порядке реального алгоритма и остаются **internal-only**; после flushed присутствуют повторные SHA/journal проверки до hardlink, а отчёт/журнал не удаляют пользовательские файлы.

`scripts/office_acceptance.py` включает source gate `cef-synthetic-fault-checkpoint-source-tests` и два независимых **не выполненных** ручных этапа: `eurooffice-gd31j32-fault-boundary-exception-vm` (внутренние исключения) и `eurooffice-gd31j32-real-kill-powerloss-directory-fsync-vm` (реальный обрыв процесса, crash, drive flush, barrier и power loss на ext4/XFS/NTFS). Изменения в GitHub Actions не выполнялись.

## Обязательные физические проверки (будущий стенд, не выполнены)

| Группа | Провокация | Критерий |
|---|---|---|
| Native Save As / CEF | неполный/повторный CEF callback, отмена, timeout | финальный файл не должен появиться; отказ и квитанция v2 согласованы |
| Filesystem fault | ENOSPC/EDQUOT/EACCES, read-only volume, неподдерживаемый hardlink | нет перезаписи оригинала или существующего final; фиксируется неполная стадия |
| Process crash | `SIGKILL` между каждой записью и fsync | read-only Inspect никогда не удаляет и не переименовывает файлы |
| Power loss | VM snapshot / эмуляция отказа диска после flush и после hardlink | журнальные события не выдаются за доказательство целостности метаданных |
| Concurrent access | подмена pending, journal, final, symlink/reparse, bind mounts | операция отказывает, production остаётся закрытым |
| Reopen | независимо открыть итоговые синтетические DOCX/XLSX/PPTX в разных движках и сравнить содержимое | структурный ZIP/SHA не принимается за совместимость Microsoft Office |
| Authorization | отсутствие `labApproved` / CEF v2/manifest / попытка рабочего пути | не создаётся ни один новый лабораторный результат и не открывается WORM |
| Provenance | подпись/доверенная временная метка, independent journal durability | только после дополнительного проектирования, не утверждено |

## Ограничения и следующий этап

Инъекция managed exception **не равна** `kill -9`, flush контроллера, power loss или файловому TOCTOU в hostile environment. Все тесты и код нужно реально скомпилировать в финальной лаборатории; без неё нельзя заявлять ни 100% тестовое покрытие, ни исправленную crash durability, ни готовность API.

**GD-31J.33** — приоритетный ревью и стабилизация общего дерева PR с устранением исходных/тестовых несовместимостей перед отложенной сборкой; затем изолированная физическая лаборатория и проверка лицензий. Нельзя бесконечно добавлять тесты поверх неиспытанных PR и выдавать это за рабочий продукт.

**Только Draft поверх #103.** `main`, GitHub Actions, обычный GET launcher, GUP-2, Nextcloud и WORM не затрагивались.
