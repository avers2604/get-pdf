# GD-31J.27s — внутренний трёхсторонний канал FD и deny-only E2E-контракт

**Дата:** 2 ноября 2026 г. **База:** Draft PR №131 / GD31J27r. **Статус:** source-only изменения, тесты написаны, но **не запускались**; сборка C++/Qt/CEF/EuroOffice SDK, VM, смена UID, systemd, fault injection, реальный root supervisor и проверка защиты Astra Linux НЕ выполнялись. Производственный Save As запрещён.

## Внедрённая граница

Новое `native_saveas_three_party_bridge.py` содержит **внутренний** `bridge_inspect_only_once(policy, supervisor_socket, worker_socket, pinned_hashes, approved_disposable_vm, observed_broker_uid, observed_broker_gid)`. Это соединение на уровне исходного кода **двух уже установленных Unix SOCK_SEQPACKET соединений**, но не ОС-supervisor, не CLI и не systemd unit.

```text
ROOT OS supervisor [НЕ СОЗДАН; будущий отдельный системный сервис]
    │ already connected SOCK_SEQPACKET, SO_PEERCRED root UID0/PID
    │ inspect_only JSON + ровно 3 preopened FD через SCM_RIGHTS
    ▼
broker [UID≠worker, non-root]
    │ receive_pinned_roles: 3 read-only FD, owner worker UID,
    │ 0700 scratch/output, SHA256 по независимым pins, no path-based open
    │ one-shot relay_pinned_once; worker PID/starttime + procfs FD3 check
    │ sendmsg/SCM_RIGHTS ровно один раз
    ▼
worker [non-root отдельный UID, ОС-изоляция пока НЕ ДОКАЗАНА]
    │ существующий parse_request + inspect_fds
    │ только инспекция имени, inode, SHA, owner markers
    ▼
DENIED: нет SaveAs, файловой публикации, WORM или release rights
```

- `parse_static_policy` ограничивает точный набор параметров, три независимых процесса/UID, supervisor UID0, один `inspect_only`, `laboratoryOnly=true`, `allowPublication=false`. Никаких путей пользовательских документов, операций редактирования, команд оболочки или произвольного чтения файлов.
- `bridge_inspect_only_once` принимает только **уже подключённые** Unix seqpacket sockets. Вызывает старый `provisioning.relay_pinned_once`: сначала отдельный OS root-peer, приём предварительно открытых FD, pinned fixture SHA; после этого существующий `handoff_after_kernel_review` с worker SO_PEERCRED, PID/starttime, exact FD3 и kernel status. Отсутствие /proc-доступа, несовпадение UID/GID, ненадёжный upstream, тайм-аут → отказ. Сеанс одноразовый; после ошибки нельзя повторить передачу.
- Результат upstream должен **точно** соответствовать `expected_upstream_denial_for_contract()`: каждое поле равно значению из списка, `state=denied`, `laboratoryOnly=true`, никаких дополнительных keys, все флаги publication/save/release/isolation attestation строго false. Поддельный `releaseReady=true` или дополнительный `publishToWorm=true` отклоняется.
- `native_saveas_broker_listener.bridge_accepted_diagnostic_only` — **явный лабораторный адаптер**, принимающий уже подготовленные два канала. Он **не вызывается** штатными `listen_once()` и `main()`. Последние по-прежнему выполняют старый zero-FD denial handshake. `native_saveas_fd_worker.serve_one` также не импортирует bridge и **не вызывает** `native_saveas_worker_snapshot.publish_disposable_fixture`.

## Исправление управления FD

В `native_saveas_fd_provisioning._received_exact_three` устранён опасный ранний выход на неизвестном ancillary block. Теперь код сначала **перебирает весь полученный массив управления**, забирает каждый `SCM_RIGHTS` FD в локальный список и отмечает `bad_control`, а затем, при любом неожиданном типе/числе/флагах, закрывает **все** полученные дескрипторы. Это предотвращает утечку FD, когда чужой ancillary элемент идёт **перед** блоком `SCM_RIGHTS`. Дополнительный negative unit-case `test_unknown_ancillary_before_rights_closes_every_received_fd` использует synthetic `os.dup` и проверяет, что полученные FD больше не открыты.

## Заблокированное включение

CMake: `GETDOC_GD31J27S_ENABLE_THREE_PARTY_PUBLICATION_LAB=OFF` по умолчанию и обязательный `GD31J27S_BLOCKED` / `FATAL_ERROR` при попытке включить. Прежние l–r запреты не ослаблены. Нет подключения к CEF GUI или пользовательским документам. Никаких GitHub Actions, merge в main, изменения WORM, SMB, Nextcloud, GUP-2.

## Подготовленные тесты (не исполнялись)

- `test_native_saveas_three_party_bridge.py`: точная policy schema, UID/root/worker separation, режим inspect_only, запрет `allowPublication`, отказ при отсутствии VM approval или падении relay; одна отправка, denial-only status, отказ при дополнительных/положительных флагах; explicit listener adapter.
- `test_native_saveas_three_party_bridge_contract.py`: нет публикации или privileged OS launcher, отсутствие вызова в обычном CLI/CEF, наличие CMake hard-stop, точная downstream denial схема.
- Новая regression в `test_native_saveas_fd_provisioning.py`: неизвестный ancillary block перед `SCM_RIGHTS` должен привести к закрытию всех ранее переданных FD, без исключения.
- `scripts/office_acceptance.py`: оба suite зарегистрированы до restore; `scripts/tests/test_office_acceptance.py` проверяет их наличие, ручной E2E `eurooffice-gd31j27s-three-party-denial-bridge-fd-lifecycle-e2e`.
- Строгая матрица `native_saveas_acceptance_gate.py`: **27 fault cases / 24 mandatory manual gates**, включая `three-party-root-broker-worker-session-fd-close-denial`. Наличие флагов passed всё равно требует independent review и не разрешает Save As.

## Обязательные условия реального E2E на disposable Astra Linux VM

1. Отдельные OS users и pinned service PID/starttime, подтверждённый SOCK_SEQPACKET после установки UID/GID, namespace, seccomp, Astra МРД/AppArmor; отключить внешнюю/корпоративную сеть и исключить корпоративные mounts, secrets, WORM.
2. Доверенный OS root supervisor должен отдельно проверять **внешне закреплённые** SHA256 synthetic DOCX/XLSX/PPTX, worker-owned 0700 каталоги и source inode, открывать ровно три FD `O_RDONLY|O_CLOEXEC`, передавать только один bounded packet. Создание этого supervisor **ещё не реализовано**.
3. Broker должен принимать все fdCaps без дополнительных прав и без `openat` внутри private worker directories. Worker повторно проверяет marker bytes, источник по точному scratch basename, SHA и namespace. Не обходить защищённый procfs, если его нельзя проверить из broker.
4. E2E fault injection на разрыв между sendmsg/recvmsg, неправильные 1/2/4 FD, `MSG_CTRUNC`, нераспознанный ancillary перед/после `SCM_RIGHTS`, same UID, root, capability leak, лишние FD4+, повторную передачу, изменение имени/source byte SHA.
5. **После** этих тестов отдельно проектировать ограниченную worker-side публикацию из GD31J27q, уже с настоящими правами изоляции. Нельзя включать CEF `NativeSaveAsPublish` на основании только данного IPC-contract.

**Следующий GD-31J.27t:** оформить проверяемый root-supervisor fd-provisioning and service policy для одноразовой VM, зафиксировать миграцию текущего zero-FD listener на trusted sender без включения пользовательского SaveAs. До этого результат всей системы остаётся `denied`.
