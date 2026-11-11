# GD-31J.27bz — три блока протокола долговечного checkpoint, HMAC-вершины и рестарта

**Дата:** 2026-11-11. **Базовая ветка:** `feature/gd31j27by-crash-journal-reconciliation` (Draft PR #190). **Режим:** только диагностический C#/.NET 10 + тесты; никаких native mutations, записи журналов или подключения к рабочему восстановлению. `releaseReady=false`.

## Блок BZ-1 — RecoveryDurableCheckpointProtocol

**Цель:** отдельная fail-closed модель ПОРЯДКА фиксации состояния перед тем, как когда-нибудь появится OS-native хранилище. `Start(transactionId, priorGeneration, nextGeneration, journalHeadSha256, frameCount)` требует GUID, строго `next = prior + 1` без переполнения, 1–16 записей, SHA-256 в верхнем регистре.

Допустимая цепочка **синтетически заявленных** событий:

```text
Initialized
  -> JournalFileFlushClaimed
  -> JournalDirectorySyncClaimed
  -> AnchorFileFlushClaimed
  -> AnchorDirectorySyncClaimed
  -> RestartReadbackClaimed
```

Никакая фаза не выполняет `FlushFileBuffers`, `fsync`, `renameat2`, `unlinkat` или реальную запись. Все `Event` — строки фактов, заявленных вызывающим кодом. Дубли, пропущенная фаза, поддельный счётчик, ошибочная SHA и откат поколения отвергаются. Отмена/крах **до первого flush-claim** — `SuspendedWithoutWriteClaim`; после — `RequiresManualReview`. `LockLost` или `MediaError` всегда `RequiresManualReview`. Даже `ReadbackClaimed` не повышается до права изменения файла или доказанного durability.

**OS-долговечность пока не доказана.** Для Astra/Linux `fsync(file)` не обеспечивает надёжную фиксацию directory entry: нужен `fsync(dirfd)`, отдельный разбор ошибок `ENOSPC/EIO/EINTR` и реальные power-loss испытания. Справка: https://man7.org/linux/man-pages/man2/fsync.2.html . На Windows/Samba/SMB семантику сброса, контроль rename/replace и доверие к серверу нужно квалифицировать отдельно. Протокол **не предполагает** одинаковых гарантий для NTFS, ext4 и SMB.

## Блок BZ-2 — RecoveryProtectedAnchorProtocol

**Цель:** задать канонический формат контрольной вершины, защищённой HMAC-SHA-256 по **предоставленному извне** 32–64-байтовому ключу. Поля `Version=1`, `TransactionId`, `Generation`, `JournalHeadSha256`, `PreviousAnchorMacSha256`, `MacSha256`. Каноническая HMAC-нагрузка:

```text
GET-BZ1|transaction-guid-N|generation|journal-head-upperhex|previous-mac-upperhex
```

Используется `HMACSHA256.HashData`, постоянное по времени сравнение `CryptographicOperations.FixedTimeEquals`, обнуление временных MAC-буферов. Строгие заглавные hex, непустой ID, ненулевое поколение и точное равенство **ожидаемому** поколению, transaction ID, предыдущей вершине и текущему journal head.

**Ограничение доверия:** `CreateSynthetic` и `AssessCandidate` не создают/загружают/защищают ключ. Злоумышленник с ключом или доступом к старому snapshot способен сформировать валидную на вид вершину. Приложение **не подключено** к независимому KMS/TPM/Windows DPAPI/защищённой службе и не имеет проверенного аппаратного monotonic counter. Поэтому даже `MatchedButUntrusted`, когда HMAC сошлась, означает лишь совпадение с переданным ключом. `NativeTrustStoreConnected=false`; `HasIndependentlyTrustedKey=false`; все write/restore/delete права — `false`.

## Блок BZ-3 — RecoveryRestartCheckpointReview

**Цель:** read-only аналитическая сверка после условного перезапуска. `Inspect` последовательно проверяет:

1. Кандидат контрольной вершины по предоставленному ключу, ожидаемому поколению и предыдущему MAC (до интерпретации журнала).
2. Канонические JSON-байты (не более 8 КиБ) через `RecoveryCrashJournalCodec.DecodeCandidate`.
3. Всю цепочку до заявленной вершины с повтором событий через `RecoveryCrashJournalReconciler.Analyze`.
4. Отдельный **заявленный** снимок copy/manifest и (если оба видны как обычные файлы) шесть заявленных OS File ID родителя, копии и манифеста: open/recheck, один том, несовпадающие ID.

Выводы: `AnchorRejected`, `InvalidJournal`, `NeedsNativeReadback`, `ReopenedIdentityMismatch`, `JournalArtifactConflict`, `OrphanRequiresReview`, `NoJournalEvidence`, `IncompleteOperation`, `MatchedClaimsOnly`. Все классы исключительно диагностические. Даже `MatchedClaimsOnly` не доказывает, что имена объектов остались привязанными к открытым handle или что произошла действительно атомарная операция.

**Принцип безопасности:** нельзя превратить синтетический журнал, HMAC с недоверенным ключом и самозаявленные File ID в разрешение для `CaptureAsync`, `Discard`, `Prune`, Native EuroOffice SaveAs, СЭД, GUP-2 или WORM. `CanRestore=false`, `CanDelete=false`, `CanResumePublication=false`, `HasTrustedPersistentAnchor=false`, `HasIndependentlyAuthenticatedJournal=false`, `ReleaseReady=false`.

## Блок BZ-4 — тесты и приёмка

Подготовлены:

| Набор | C# MSTest |
|---|---:|
| `RecoveryDurableCheckpointProtocolTests.cs` | 12 |
| `RecoveryProtectedAnchorCandidateTests.cs` | 12 |
| `RecoveryRestartCheckpointReviewTests.cs` | 14 |
| **Итого** | **38** |

В `scripts/tests/test_recovery_durable_checkpoint_blocks_contract.py` написано **16 Python source-only unit tests**. `scripts/office_acceptance.py` запускает их **до** локального offline NuGet restore (при запуске самим оператором).

В `MANUAL_GATES` добавлены восемь **не выполненных** ручных проверок:

- `gd31j27bz-windows-journal-dir-sync-manual-e2e`
- `gd31j27bz-astra-ext4-journal-fsync-manual-e2e`
- `gd31j27bz-windows-trusted-key-rollback-manual-e2e`
- `gd31j27bz-astra-protected-key-monotonic-manual-e2e`
- `gd31j27bz-smb-crosshost-journal-consistency-manual-e2e`
- `gd31j27bz-restart-native-identity-reopen-manual-e2e`
- `gd31j27bz-powerloss-between-journal-anchor-manual-e2e`
- `gd31j27bz-original-worm-immutability-manual-e2e`

Всего **171 ручной gate** (ранее 163); все требуют реального выполнения, не засчитываются по факту написания тестов.

## Отсутствующие компоненты для настоящего долговечного journal

- Реальное независимое storage (служба с контролем OS SID/UID/LSM и запретом записи со стороны обычного пользователя), pinned trusted directories и native append/write/flush.
- Долговечный и отдельно защищённый monotonic generation + keyed anchor; восстановление от rollback, полномасштабного компрометации ключа и потери питания.
- Порядок `fsync` и rename/replace при `ENOSPC`, `EIO`, `EINTR`; отдельный manifest commit protocol. Linux `RENAME_NOREPLACE` зависит от файловой системы — https://man7.org/linux/man-pages/man2/rename.2.html .
- Windows NTFS/SMB `FlushFileBuffers`/directory semantics и server-side гарантии; глубокие отрицательные E2E.
- Сверка имён и File ID через реальные удерживаемые native handles после рестарта, контроль владельца/ACL/LSM и SHA/OOXML, а не предоставленные `RecoveryNativeFileId` структуры.
- Вызовы реального `File.Move` в `OfficeRecoveryStore.CaptureAsync` пока не менялись и остаются опасным ограничением P0; `Discard` и `Prune` сохраняются недеструктивными.

## Как проверять позже (только на одобренном офлайн-стенде)

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_durable_checkpoint_blocks_contract.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryDurableCheckpointProtocolTests
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryProtectedAnchorCandidateTests
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryRestartCheckpointReviewTests
```

**Статус подготовки в этой сессии:** написаны код и тесты трёх блоков, Python-контракты и runbook. GitHub read-back/структурная проверка будет выполнена до оформления PR. Реальные .NET build/MSTest и Astra/Windows/SMB platform E2E **не выполнялись**. CI/Actions не включать, `main` не менять, сохранять Draft и `releaseReady=false`.
