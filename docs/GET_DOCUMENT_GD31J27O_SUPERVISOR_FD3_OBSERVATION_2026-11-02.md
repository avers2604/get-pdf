# GD-31J.27o — проверка FD3/PID/UID перед передачей данных в worker

**Дата:** 2 ноября 2026 года. **База:** Draft PR №127 / GD-31J.27n. **Режим:** только synthetic Linux/Astra/Wayland disposable offline VM. **Статус:** исходный код, отложенные тесты и блокирующие контракты подготовлены. Никакая сборка C++, Python/MSTest, EuroOffice/Qt/CEF SDK, VM или ручная fault injection НЕ выполнялись.

## Что было не доказано в GD-31J.27n

Проверки `/proc/self/status`, `NoNewPrivs=1`, `Seccomp=2`, capabilities и mount namespaces полезны, но не отвечают на вопрос: действительно ли перед нами тот самый процесс, который запустил проверенный системный supervisor, и не унаследовал ли он неожиданные файловые дескрипторы? Кроме того, `SO_PEERCRED` на сокете, созданном ещё **до** смены UID, может отражать старые credentials, а PID может быть переиспользован после завершения исходного worker.

Нельзя давать доступ к synthetic FD просто потому, что входной JSON заявляет статус «разные UID» или потому, что broker получил какой-либо PID без проверки процесса.

## Внесённые исходники

### read-only native_saveas_supervisor_handoff.py

Новый этап наблюдения `read_actual_handoff(sock, expected_worker_pid, expected_worker_uid, broker_uid, expected_worker_start_ticks)` вызывается после подготовки **чужим доверенным системным supervisor** нужного соединения и до передачи `SCM_RIGHTS`.

- `sock` должен быть `AF_UNIX/SOCK_SEQPACKET`, а `SO_PEERCRED` обязан совпадать с independently-pinned `expected_worker_pid/uid`. Broker под nonroot и **отличным UID**.
- Для защиты от простой ошибки PID reuse наблюдатель дважды читает `/proc/<pid>/stat` и сверяет kernel `starttime` (поле 22) с independently pinned `expected_worker_start_ticks` перед проверкой и после. Это не заменяет `pidfd` в будущем полноценном supervisor.
- Читаются `/proc/<pid>/status` и `mountinfo` (с ограничением размера), сравниваются inode `mnt/net` namespace worker с namespace broker. Используется готовый строгий GD31J27n parser статуса, capabilities, tracer, seccomp и запрета известных сетевых/FUSE filesystem.
- Проверяется **точный набор FD 0, 1, 2, 3** через `/proc/<pid>/fd`: 0–2 разрешены только `/dev/null` или `pipe:[n]`, FD3 только `socket:[n]`. FD4 и любые дополнительные FD, в том числе потенциально унаследованные корпоративные файлы или сокеты, — отказ. Другой режим stdout/stderr должен быть явным отдельным решением после OS-аудита.
- При отказе чтения защищённого `/proc/<pid>/fd` под другим UID функция **отказывает**, не запускает worker с ослабленными правами, не пытается `setuid`/`ptrace`/привилегированное чтение и не пропускает проверку.
- Даже при успешном снимке выдаётся **только** `reviewState=requires-independent-review`, `state=blocked`, `launchAuthorized=false`, `kernelIsolationAttested=false`, `brokerHandshakeAttested=false`, `nativePublicationAuthorized=false`, `releaseReady=false`, `approvedForProduction=false`, `writePermissionGranted=false`.

### Новое entrypoint для broker

`native_saveas_fd_broker.handoff_after_kernel_review(...)` сначала вызывает **реальную**, не сформированную отправителем из JSON проверку `read_actual_handoff`. Только после нужной формы **denial-only** результата допускает старый `BrokerSession.handoff`, который всё равно заканчивается исключительно `state=denied` при корректном диагностическом ответе. После отказа preflight broker помечает сессию израсходованной: не допускаются повторная передача или обход ранее неуспешного осмотра.

Это **не** «успешное сохранение»: ни broker, ни worker GD31J27l–o не выполняют `O_TMPFILE/linkat/fsync` публикацию, и никаких новых backend-разрешений не создано. Старый lab-only C++ путь `NativeSaveAsPublish` ещё существует **в CEF-процессе**. Его перенаправление пока нельзя считать реализованным.

### Заблокированное описание systemd

`getdoc-gd31j27o-supervisor.blocked.service` — только ревью-спецификация, не инсталлятор. `ExecStart=/usr/bin/false`, `RefuseManualStart=yes`, несуществующий без вмешательства администратора `ConditionPathExists`, отсутствует `WantedBy`/установка. Он **не создаёт** real `SOCK_SEQPACKET` и не обеспечивает FD3, не применяет к рабочему компьютеру никаких политик.

В CMake есть `GETDOC_GD31J27O_ENABLE_SUPERVISOR_PUBLICATION_LAB=OFF` и **FATAL_ERROR** при включении. Старые блокировки l/m/n не ослаблены.

## Подготовленные, но не выполненные проверки

- `test_native_saveas_supervisor_handoff.py`: точные PID/UID/GID и FD slots, неправильные FD3, случайный FD4, ошибки capabilities/seccomp/mountinfo, forged `SO_PEERCRED`, blocked flags даже при идеальном mock snapshot;
- `test_native_saveas_supervisor_broker_guard.py`: новый broker entrypoint не отправляет ни одного FD при невозможности kernel-observation; even accepted mocked observation не повышает права, повторная сессия отвергается;
- `test_native_saveas_supervisor_handoff_contract.py`: источник не запускает процесс/системные команды и не выдаёт права, CMake/unit служат жёсткой блокировкой;
- три offline pre-restore шагa в `scripts/office_acceptance.py` и проверка плана; 23-й fault case и 20-й mandatory manual E2E в `native_saveas_acceptance_gate.py`.

## Что обязан отдельно проверить оператор на будущей VM

1. Отдельные non-root UID broker и worker, доступ systemd/Astra к наименованиям namespace, времени рождения PID; невозможность чтения `/proc` является **блокером**, не поводом включать `CAP_SYS_PTRACE`.
2. Строго фактические fd3 SOCK_SEQPACKET и kernel `SO_PEERCRED` после переключения UID, запрет inherited file/network FD4+, неизменность ожидаемого PID+starttime. Проверить при exit/restart/reuse и повторном подключении.
3. Реальный syscall allowlist seccomp, отсутствие внешних/corporate mounts, отсутствие секретов, правила AppArmor/МРД Astra; сам статический профиль этого не доказывает.
4. Запрет доступа same-UID атакующих к open FD/ptrace, `O_TMPFILE`, `AT_EMPTY_PATH`, замена имени и содержимого, `EEXIST`, `fsync`, `SIGKILL` до/после link и закрытие дескрипторов.
5. Только после успешных independent тестов проектировать **worker-side synthetic publication** и удаление старого in-process публикующего перехода. Перенос CEF, реальное редактирование DOCX/XLSX/PPTX, GUP-2/WORM и production остаются за рамками этого PR.

**Следующий этап GD-31J.27p:** согласовать доказуемый OS launcher и процессное создание соединения после UID-switch, обсудить pidfd/systemd-политику, сформировать строго gated worker-side synthetic-publish prototype. До этого всё остаётся отключённым.

Проект закрытый, некоммерческий; никакие пользовательские документы, GUP-2, SMB/WORM, Nextcloud, GitHub Actions или main не изменяются.
