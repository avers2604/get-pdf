# GD-31J.27by — журнал аварийных операций и сверка после прерывания

**Дата:** 2026-11-10. **База:** GD-31J.27bx, Draft PR #189. **Статус:** stacked Draft, выпуск и реальное удаление/публикация запрещены (`releaseReady=false`).

## Назначение

В BX появился **только in-memory** план последовательности публикации и удаления аварийной пары «OOXML-копия + JSON-манифест». После SIGKILL/отключения питания текущая память процесса теряется. Новая часть BY готовит **схему будущего журнала** и строго **read-only сверку переданного снимка**, но намеренно **не создаёт и не читает журнал на диске**: нет согласованного доверенного каталога, фиксации `fsync` и OS-bound writer backend.

Файлы:

- `src/GetDocument.Office/LibreOffice/RecoveryCrashJournalReconciler.cs` — ограниченный формат кадров и проверка смысловой последовательности через существующий `RecoveryNativeMutationPlanner`.
- `src/GetDocument.Office/LibreOffice/RecoveryCrashJournalCodec.cs` — обмен **каноническими байтами** JSON в памяти.
- `tests/GetDocument.Office.Tests/RecoveryCrashJournalReconciliationTests.cs` — 23 синтетические MSTest.
- `scripts/tests/test_recovery_crash_journal_reconciliation_contract.py` — 10 source-only контрактов.
- `scripts/office_acceptance.py` — дополнительный шаг offline-контроля и ручные gate.

## Wire-format v1 — подготовлен, не записывается на диск

Каждый `RecoveryCrashJournalFrame`:

| Поле | Назначение |
|---|---|
| `Version` | строго `1` |
| `TransactionId` | непустой GUID одной операции |
| `Kind` | `PublishPair` или `DeletePair` |
| `Sequence` | целочисленный номер без пропусков, с 1 |
| `Event` | событие плана BX |
| `PreviousSha256` | 64 **заглавных** hex символа, у первого кадра 64 нуля |
| `FrameSha256` | SHA-256 от канонического ASCII-представления |

Канонический digest payload: `1|transaction-guid-in-N-format|kind-int|sequence|event-int|previous-hash` с инвариантным форматированием. Максимум **16 кадров** на операцию. Канонический JSON-envelope — максимум **8192 байта**, без лишних свойств, дубликатов, комментариев, завершающих запятых, другой последовательности полей или неоднозначного форматирования. `DecodeCandidate` декодирует данные только **в память**; `Analyze` проверяет цепочку хешей, transaction/kind, номер каждого кадра и законность переходов `RecoveryNativeMutationPlanner`.

**Критически важно:** unkeyed SHA-256 позволяет увидеть случайную порчу или несогласованное изменение отдельных записей, но злоумышленник, способный заменить весь журнал, может пересчитать все хеши. Даже `expectedJournalHeadSha256` без доказанно независимого, защищённого хранения не является аутентификацией. Поэтому `HasIndependentJournalAuthentication=false` без исключений и **ни один** результат не превращается в разрешение на file operations.

## Сверка с независимым обследованием файлов

В `Analyze` передаются **отдельно** журнальные записи и `RecoveryCrashArtifactSnapshot` для двух компонентов. Этот снимок — только заявленное наблюдение (свидетельства из `FileInfo`/потенциального native adapter пока не подключены); он **не подтверждает** ни фактическую принадлежность, ни содержимое OOXML, ни устойчивость File ID.

Возможные недеструктивные заключения:

| Класс | Значение |
|---|---|
| `NoJournalEvidence` | нет записей; отсутствие файлов не означает чистое завершение |
| `InterruptedBeforeMutationClaim` | есть только заявленные проверки, нет события изменения имени |
| `PartialMutationClaim` | заявлено одно или несколько изменений имени, но нет полного завершения |
| `ClaimedCompleteUnverified` | весь порядок событий заявлен, но файловый результат не аттестован |
| `CopyWithoutManifest` | видна копия без манифеста; не удалять |
| `ManifestWithoutCopy` | манифест без копии; не считать восстановимым |
| `JournalConflictsWithArtifactObservation` | заявленное завершение противоречит наблюдаемым именам |
| `UntrustedArtifact` | ссылка/каталог/необычный объект вместо обычного файла |
| `MetadataUnavailable` | получить достоверный снимок файлов не удалось |

Прерванная публикация **не возобновляется** на основании записей; незавершённое удаление также не продолжается. При неизвестном состоянии, подмене или несоответствии хешей выдаётся ошибка или диагностическая категория — **никогда не «готово к удалению»**.

Все `RecoveryCrashReconciliationReport` возвращают `CanAuthorizeRestoration=false`, `CanAuthorizeCleanup=false`, `CanAuthorizePublication=false`, `HasIndependentJournalAuthentication=false`. `RecoveryCrashJournalCodec.CanAuthorizeFileMutation=false`.

## Производственная интеграция сознательно не включена

- `OfficeRecoveryStore.CaptureAsync` по-прежнему использует `File.Move` по имени, с незакрытыми TOCTOU-рисками. Новый модуль его не вызывается и не меняет.
- `Discard` и `Prune` после BV остаются недеструктивными; лимит 32 опубликованных копий на один источник остаётся действующим.
- Источники, WORM-оригиналы, СЭД, GUP-2 и EuroOffice Native SaveAs **не подключены** к BY; никаких файлов или ACL модуль не пишет.
- Нет реального durable append-only journal, OS-held lock, проверенного `fsync(file) + fsync(directory)`, защищённой контрольной вершины, проверки подписи/HMAC, стойкого к rollback sequence или crash-safe восстановления после перезапуска.

## Что нужно для действительного persist/restart режима

1. Утвердить **разделённый доверенный сервис** сохранения журнала, с identity-bound DirectoryHandle, owner-only permissions, реальными политиками Windows/Astra, контролем namespace и исключением пользовательской записи.
2. Получить native receipts, связывающие в одной операции открытые File IDs и подтверждённую запись, rename/unlink/no-replace, блокировку, синхронизацию и смену каталога. Синтетические `Event` не являются квитанциями.
3. Обеспечить **append+durable commit** на независимом журнале, взаимную согласованность контрольной вершины и filesystem state после power loss, защищённый monotonic generation / signed or keyed anchor; определить допустимый rollback.
4. При рестарте **переоткрывать** компоненты через безопасные relative операции, проверять актуальный File ID, owner, ACL/LSM, SHA-256 и целостность OOXML; не доверять старым именам из JSON.
5. Выполнить реальные отрицательные тесты NTFS/ReFS/Astra ext4/SMB: rename races, symlink/hardlink, смена токена/UID, ENOSPC, SIGKILL, loss-of-lock, обрыв SMB и две конкурирующие машины. Деструктивные функции оставлять выключенными до принятия результатов.

## Проверки

Выписаны **23 MSTest** (включая границы JSON 8 КиБ/16 событий, каноническое кодирование, повторный запуск, отсутствие журнала, обрыв и реплей, orphan-артефакты, противоречия, недостоверность SHA без защищённого anchor) и **10 source-only Python-контрактов**.

Добавлены **5 обязательных ручных gates (`not-run`):**

- `gd31j27by-windows-crash-journal-truncation-manual-e2e`;
- `gd31j27by-astra-ext4-journal-fsync-powerloss-manual-e2e`;
- `gd31j27by-smb-cross-host-journal-replay-manual-e2e`;
- `gd31j27by-restart-artifact-identity-review-manual-e2e`;
- `gd31j27by-original-worm-preservation-manual-e2e`.

Итого в `MANUAL_GATES` **163** обязательных проверки (158 ранее + 5 новых).

Запуск **позже, на одобренном изолированном стенде**:

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_crash_journal_reconciliation_contract.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryCrashJournalReconciliationTests
python3 scripts/recovery_release_readiness.py --details
```

**В этой сессии:** добавлен C# код, C# тесты, Python-контракты, ручные gates и документация; перепроверка исходников проводится через GitHub. **Нет фактически запущенных .NET build/MSTest, Python unittest в репозитории, Windows/Astra/SMB или power-loss испытаний.** `releaseReady=false`, Draft PR, никаких изменений `main`.
