# GD-31J.27u — консервативный аудит root supervisor, DAC и PID lifetime

**Дата:** 2 ноября 2026 года. **База:** PR №133 / GD-31J.27t. **Статус:** исходники и отложенные тесты; реальные Python/C++ тесты, сборка, Astra Linux VM, systemd, доступ к /proc под UID0 и SCM_RIGHTS E2E **не выполнялись**.

## Устранённая проблема

На этапе GD-31J.27t root sender сверял `SO_PEERCRED(pid,uid,gid)` broker. Однако PID может использоваться повторно после завершения процесса. Это особенно опасно в лаборатории, где привилегированный отправитель имеет доступ к заранее открытым worker-owned FD. Kernel credentials сами по себе не содержат версию процесса и не подтверждают конкретный бинарный файл.

Теперь обязательное поле **`expectedBrokerStartTicks`** в root-owned манифесте должно быть независимо получено и закреплено оператором после запуска проверенного процесса broker. При реальной отправке `RootSendOnce.send` проверяет `/proc/<broker_pid>/stat`, поле 22 `starttime`, **до** открытия любых worker-owned FD, а затем **после** единственного `sendmsg`. Любая ошибка чтения, PID reuse, EOF, нестабильный process birth time или неверные credentials → отказ без повторной отправки. Если ошибка обнаружена после sendmsg, передача могла уже состояться: итог **неопределён**, и повторно отправлять FD нельзя.

## Новый `native_saveas_root_supervisor_audit.py`

Это отдельная **read-only** необходимая проверка реального ядра Linux, которая также включена в исходный маршрут `RootSendOnce.send` **до** `open_verified_handles`.

`inspect_current_root()` читает только:
- `/proc/self/status` — все четыре UID/GID = 0, `NoNewPrivs=1`, `Seccomp=2`, минимум один BPF filter, `TracerPid=0`;
- `CapInh/CapPrm/CapEff/CapBnd/CapAmb` — допускается максимум наблюдаемый **CAP_DAC_READ_SEARCH (бит 2)**. `CAP_DAC_OVERRIDE` и `CAP_SYS_ADMIN`, а также все прочие capabilities и privilege inconsistency вызывают отказ. Отсутствие CAP_DAC_READ_SEARCH не означает успешный доступ к worker-owned 0700 директориям;
- `/proc/self/mountinfo` — ограниченный разбор; известные внешние ФС (CIFS/NFS/FUSE и др.) ведут к отказу; локальные тома при этом **не удостоверяются** как безопасные;
- namespace inode mnt/net текущего процесса и `/proc/1/ns/mnt` / `ns/net`; совпадение → отказ. Различные inode — **не доказательство полноценной изоляции**.

**Положительный результат — только `state=requires-independent-review`.** `rootSandboxAttested=false`, `seccompAllowlistAttested=false`, `corporateFilesAbsentAttested=false`, `mountRestrictionAttested=false`, `brokerProcessPinnedAttested=false`, `nativePublicationAuthorized=false`, `launchAuthorized=false`, `releaseReady=false`, `writePermissionGranted=false`. Тот же root-процесс не вправе сам удостоверить свою OS-изоляцию.

**CAP_DAC_READ_SEARCH — широкая системная привилегия**, позволяющая обходить DAC при чтении, и её включение в будущем требует отдельной экспертизы: изолированный mount namespace без корпоративных каталогов/секретов, точный syscall allowlist, Astra Linux МРД и тестирование реального DAC-доступа. Текущий заблокированный `getdoc-gd31j27t-root-supervisor.blocked.service` имеет пустой `CapabilityBoundingSet` и намеренно не способен запускать Python; никаких capabilities в systemd этим PR не добавлено.

## Кросс-проверка root → broker → worker

`inspect_three_party_alignment(root_manifest,worker_dialer_policy,broker_bridge_policy)` сверяет **строгие схемы каждого уже реализованного компонента** и согласованность `workerUid/brokerUid/brokerGid/workerGid/ipcGid`, `publishEnabled=false`, `allowPublication=false`, root-supervisor UID0 и режим `inspect_only`. Он не открывает файлы, не вызывает systemd, не создаёт сокеты и **не принимает даже идеально согласованные настройки за идентичность реальных процессов**. Отчёт содержит только `configurationFieldsAligned=true`, `state=requires-independent-review`, все разрешения false.

## Реальные блокеры

- Supervisor как полноценная root-служба всё ещё **не реализован**; его `ExecStart=/usr/bin/false`, `RefuseManualStart=yes`, отсутствие `[Install]` сохраняются.
- `/proc/1/ns/*` и `/proc/<broker_pid>/stat` могут оказаться недоступны в sandbox: это **жёсткий отказ**, а не повод добавлять `CAP_SYS_PTRACE`/полный `CAP_SYS_ADMIN` или отключать проверку.
- Манифест с зафиксированным PID+startticks — только **operator-controlled входной pin**, не независимая аттестация цифровой подписи binary, pidfd или согласованного жизненного цикла systemd. PID между разными запусками нельзя подставлять из старого файла.
- `Seccomp=2` не доказывает семантику фильтра, `mountinfo` не доказывает отсутствие локальных корпоративных данных, проверка credentials не исключает всех TOCTOU. Требуется независимая сверка OS-level provisioning и временных меток на одноразовой Astra Linux VM.
- Существующий broker listener продолжает **zero-FD deny-only handshake**. Внутренний GD31J27s bridge и отправитель root в обычный CLI не подключены. GD31J27q snapshot publisher по-прежнему не вызывается из worker IPC; пользовательский SaveAs/WORM/GUP-2/Nextcloud не затронут.

## Написанные, но не выполнявшиеся проверки

- `test_native_saveas_root_supervisor_audit.py`: синтетические /proc status/mountinfo и отрицательные root UID, capabilities DAC_OVERRIDE/SYS_ADMIN, seccomp, tracer, namespace; правильные/неправильные pinned broker startticks, TOCTOU после sendmsg, несовпадающие политики root/worker/broker.
- `test_native_saveas_root_supervisor_audit_contract.py`: source invariants — root kernel check и PID starttime **до** открытия FD, повторная сверка **после** sendmsg, отсутствие разрешающих флагов и опасных OS-запусков.
- Обновлены старые `test_native_saveas_root_supervisor.py`: root manifesto включает `expectedBrokerStartTicks`, синтетические положительные mocks и неверные значения pin.
- Два offline pre-restore suite зарегистрированы в `scripts/office_acceptance.py`, матрица `native_saveas_acceptance_gate.py` содержит **29 fault cases / 26 mandatory manual gates**: `root-broker-pid-reuse-dac-caps-mountns-deny` и `eurooffice-gd31j27u-root-broker-pid-dac-namespace-audit-e2e`.
- Новая опция CMake `GETDOC_GD31J27U_ENABLE_ROOT_AUDITED_PUBLICATION_LAB=OFF`, при включении всегда `GD31J27U_BLOCKED` / `FATAL_ERROR`. Все прежние запреты сохраняются.

**Следующий GD-31J.27v:** подготовить независимый pidfd/процессный OS-proof и способ передачи broker worker без обязательного cross-UID чтения `/proc`, не ослабляя sandbox; реально принять отдельный Astra Linux VM policy и проверить работу только с синтетическими файлами. Публикация пользовательских документов остаётся выключенной.

**Не выполнялись:** сборки, тесты, root-сервисы, VM, реальные FD-передачи, merge, GitHub Actions. Производственная ветка `main`, пользовательские файлы, PDF, WORM и GUP-2 не менялись.
