# GD-31J.27bx — протокол безопасного порядка нативной публикации и удаления (только модель)

**Дата:** 2026-11-09. **База:** GD-31J.27bw / Draft PR #188. **Режим:** отдельная stacked Draft-ветка; `releaseReady=false`. Реальный native backend не подключён.

## Контекст и границы

В `OfficeRecoveryStore` публикация аварийной копии и манифеста пока выполняется двумя `File.Move` по пути, без атомарной проверенной привязки имени к открытому файловому объекту. `Discard` и `Prune` после GD-31J.27bv преднамеренно недеструктивны. В GD-31J.27bw появились read-only пробы `openat2/statx`, сопоставляющие held File ID и имя, но полученный снимок **не удерживает имя от замены сразу после проверки**.

**GD-31J.27bx НЕ реализует файловые операции.** Он добавляет отдельный чистый C# планировщик `RecoveryNativeMutationPlanner`, который проверяет **порядок заявленных событий**. Сами события не подтверждены ядром ОС: любой вызывающий код способен их сформировать. Ни один тип не выдаёт права записи, публикации, unlink или restore. Планировщик не подключён к `OfficeRecoveryStore`, `RecoveryPairMutationGate` или EuroOffice Native SaveAs.

## Функциональный протокол — только синтетическая модель

### Общие предусловия

- Каждый план имеет непустой `TransactionId`, явный `PublishPair` или `DeletePair`.
- Входные File ID должны быть согласованными по внутреннему `RecoveryPairMutationGate.Evaluate` (`NeedsNativeBoundOperation`). Это всего лишь **планировочное** свидетельство; в нём используются caller-provided поля, которым не нужно доверять.
- Дальнейший порядок: `ObserveParentAnchor` → `ObserveExclusiveLock` → `ObservePairNameAndIdentity`. Заявление об эксклюзивной блокировке **не доказывает** настоящую OS-held блокировку.

### Публикация

1. `FlushCopyContent`: заявлена синхронизация содержимого копии (для будущего backend понадобится проверенный file handle, SHA/OOXML, данные flush на уровне ОС).
2. `RevalidateCopyName`: непосредственно перед изменением имени требуется свежая проверка типа, File ID и владельца под закреплённым parent handle.
3. `PublishCopyNoReplace`: заявлена публикация копии **без перезаписи** существующего имени. На Linux требуются внимательно проверенные dirfd-based механизмы, на Windows — соответствующая handle-based операция и корректная файловая семантика.
4. `SyncParentDirectory`: заявлена фиксация изменения имени каталога.
5. `FlushManifestContent`, `RevalidateManifestName`, `PublishManifestNoReplace`, `SyncParentDirectory`: такая же последовательность для манифеста. Манифест — **последний** маркер завершения публикации.
6. Итог `SequenceObserved` означает **только синтетически правильный порядок**, а не подтверждение durability, безопасности или права публикации.

### Удаление

1. Свежая проверка имени и held File ID копии (`RevalidateCopyName`).
2. `UnlinkCopyEntry` и **синхронизация родительского каталога**.
3. Свежая проверка манифеста (`RevalidateManifestName`).
4. `UnlinkManifestEntry` и **синхронизация родительского каталога**.
5. Итог `SequenceObserved` **не открывает реальный delete**. После удаления копии, но до удаления манифеста может остаться невалидная пара; это не разрешает автоматически удалять что-либо ещё.

### Отмена, гонки и рестарт

- `CancelRequested` или `ProcessInterrupted` **до первого заявленного изменения имени** переводят план в `SuspendedWithoutMutation`; это не право чистить `.part`, `.tmp` или иные остатки.
- После заявленного первого изменения имени любой cancel/crash переводит план в `RequiresManualReview`. Никаких автоматических rollbacks по имени.
- `ExclusiveLockLost` всегда `RequiresManualReview`.
- Нарушенный порядок, дублированное событие, попытка продолжения после терминального состояния или подставленный некорректный тип вызывают `InvalidDataException` / явную ошибку; план не возобновляется автоматически.
- **Нет постоянного журнала:** in-memory план нельзя использовать для определения состояния после реального SIGKILL/отключения питания. Для будущего backend потребуется отдельный журнал/квитанции, независимая сверка файловой системы и критерии допуска к повторному запуску.

## Что ещё нужно до внедрения native backend

- Доказанное доверие к parent fd/Windows directory handle, к локальному lock и к *эффективным* правам/политикам ОС, а не к самодекларируемым флагам.
- Примитив изменения имени, защищённый от post-check TOCTOU. **Повторное чтение File ID перед обычным unlink/rename по имени не даёт такой гарантии.**
- Windows: NTFS/ReFS/SMB, reparse points, owner SID/DACL, Authz/effective-token, delete sharing, журналирование и корректная directory flush semantics.
- Astra Linux: `renameat2`, `unlinkat`, dirfd, fstat/statx, POSIX ACL, LSM/мандатная безопасность, mount/user namespaces и `fsync` file/directory — с фактическими испытаниями семантики и права.
- Обрыв сети SMB, конкурирующий другой host, ENOSPC, сигнал процесса и потеря питания — отдельные negative fault-injection сценарии.
- Работающий и аттестованный протокол EuroOffice SaveAs, без изменения WORM-оригиналов, без автоматического включения GUP-2.

## Подготовленные проверки

- **14 новых C# MSTest**: строгая последовательность publish/delete, no-replace, directory sync, отсутствие reorder/replay, отмена до и после первой мутации, crash после unlink, разрыв lock, недостоверные ID, невозможность повысить результат до production.
- **8 новых Python source-only контрактов**, добавленных в `scripts/office_acceptance.py` перед offline NuGet restore.
- **5 новых обязательных manual gates (`not-run`):**
  1. `gd31j27bx-windows-ntfs-no-replace-directory-fsync-manual-e2e`;
  2. `gd31j27bx-astra-renameat2-unlinkat-dirfsync-manual-e2e`;
  3. `gd31j27bx-smb-multi-host-lock-crash-manual-e2e`;
  4. `gd31j27bx-transaction-restart-reconcile-manual-e2e`;
  5. `gd31j27bx-eurooffice-native-saveas-integration-manual-e2e`.

Итого **158 обязательных ручных проверок** (153 прежде + 5 новых). Наличие записи в списке не означает выполнение.

Запуск позже на **одобренном изолированном** стенде:

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_native_mutation_sequence_contract.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryNativeMutationSequenceTests
python3 scripts/recovery_release_readiness.py --details
```

**Текущий статус:** исходные файлы и тесты подготовлены; `.NET build`, MSTest, Python unittest репозитория, фактические системные вызовы Windows/Astra/SMB и fault injection не запускались. Система выпускает только после обязательной технической приёмки, не по модели порядка событий. `NativeBackendEnabled=false`, `CanAuthorizeNativeMutation=false`, `CanAuthorizeCleanup=false`, `ReleaseReady=false`; исходный `main` не изменять.
