# GD-31J.27w — удержание worker pidfd на всём протяжении выдачи SCM_RIGHTS

**Дата:** 2 ноября 2026 г. **База:** Draft PR №135 / GD31J27v. **Режим:** синтетические документы, внешне развёрнутые отдельные root/broker/worker UID только на disposable offline Astra/Linux VM. **Реальных тестов, сборки, VM, root-службы и передачи FD не выполнялось**; код, отложенные тесты и блокировки подготовлены в GitHub.

## Что изменено в проверке worker

До GD31J27w `native_saveas_fd_broker.handoff_after_kernel_review` выполнял `read_actual_handoff`: сверял kernel `SO_PEERCRED`, PID/UID/starttime, FD3, /proc status, capabilities, seccomp, mountinfo и namespaces до `BrokerSession.handoff`. Но **не удерживал ссылку на конкретный процесс** во время передачи `SCM_RIGHTS`, чтения ответа и после него. Worker мог завершиться, а числовой PID повторно использоваться. GD31J27v уже ввёл pidfd для root→broker, но не для broker→worker.

Новый этап реализует **дополнительный обязательный барьер**:

1. `native_saveas_pidfd_provenance.pin_worker_process(worker_pid,expected_worker_start_ticks)` переиспользует прежнюю строгую функцию `pin_broker_process`: перед `pidfd_open` проверяет independently pinned Linux `/proc/<pid>/stat` birthtime, получает kernel pidfd, проверяет `FD_CLOEXEC`, повторно проверяет starttime и `poll(0)` на exit/hup/error. Это не `pidfd_getfd`; worker FD не перехватываются из чужого процесса.
2. **После** получения pidfd, но **до** передачи прав выполняется прежний `supervisor_handoff.read_actual_handoff` и проверка исключительно `state=blocked`, `reviewState=requires-independent-review`, FD3 Unix socket и всех false-разрешений.
3. Непосредственно перед `BrokerSession.handoff(sendmsg/recvmsg)` вызывается `pinned.assert_live()`. При exit, PID reuse, отсутствии /proc-доступа или неподдерживаемом pidfd **ни одного FD не передаётся**, session consumed.
4. **Тот же открытый pidfd** удерживается во время единственного `sendmsg(SCM_RIGHTS)` и `recvmsg` с denied response; сразу после получения ответа выполняется второй `pinned.assert_live()`.
5. Ответ session.handoff принимается только при полном совпадении с ожидаемой **строгой denial-only схемой**: `state=denied`, `nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `writePermissionGranted=false`, `approvedForProduction=false`, `releaseReady=false`, `workerPreflightAttested=false`, `laboratoryOnly=true`. Любое дополнительное/поддельное approval-поле — отказ.
6. В `finally` pidfd закрывается. Любой исход остаётся one-shot; если worker умер **после** `sendmsg`, итог передачи **неизвестен**, повторять передачу или автоматически удалять результат категорически нельзя.

Новый механизм — только более узкая привязка процесса на период IPC. Он **не** подтверждает, что worker запущен доверенным executable, что `FD3` соответствует именно этой стороне `SOCK_SEQPACKET` без гонок, что `seccomp` использует проверенный BPF, что mount namespaces не содержат корпоративных файлов, и что Astra Linux МРД, DAC, systemd unit или исходные fixture pins прошли независимый security audit. `SO_PEERCRED` и `pidfd` сами по себе **не являются подписью приложения**.

Если broker под отличным от worker UID не может прочитать `/proc/<worker_pid>/status|mountinfo|fd|stat`, текущий алгоритм **отказывает**. Нельзя вводить `CAP_SYS_PTRACE`, `pidfd_getfd`, setuid, отключение `ProtectProc` или слабую проверку по одному PID только ради «зелёного» результата.

## Подготовленные проверки (НЕ запускались)

- `test_native_saveas_worker_pidfd_handoff.py`: mock-only точный порядок pidfd→kernel review→liveness→send→liveness→close; exit **до** передачи, exit **после** передачи, ENOSYS/недоступный pidfd, forged `releaseReady=true`, consumed session и отсутствие повторной отправки.
- `test_native_saveas_worker_pidfd_handoff_contract.py`: статический порядок исходников, отсутствие `pidfd_getfd`/`ptrace`/privilege change, доказуемость запретов в CMake и отсутствие импорта в обычный worker/GUI/CEF.
- `test_native_saveas_supervisor_broker_guard.py` обновлён: старые диагностические mock-tests теперь явно подменяют обязательный новый pidfd, не обращаясь к ОС.
- `scripts/office_acceptance.py`: два offline pre-restore шага; `scripts/tests/test_office_acceptance.py` проверяет регистрацию и ручную VM gate.
- `native_saveas_acceptance_gate.py`: **31 fault case / 28 mandatory manual E2E gates**; новый сценарий `worker-pidfd-death-before-after-scm-rights-no-replay`, новая обязательная проверка `eurooffice-gd31j27w-worker-pidfd-scm-rights-deny-e2e`.
- `CMakeLists.txt`: `GETDOC_GD31J27W_ENABLE_WORKER_PIDFD_PUBLICATION_LAB=OFF`; при ON **`GD31J27W_BLOCKED` / FATAL_ERROR**, прежние l–v блокировки сохраняются.

## Оставшиеся реальные блокеры

**Root supervisor и Astra Linux/systemd не развёрнуты**, worker UID/GID и отдельные namespaces не создавались на стенде, `pidfd_open`, `SO_PEERCRED`, передача трёх SCM_RIGHTS между тремя отдельными OS процессами и graceful/forced exit не испытывались. Происхождение и digests executable, допустимость worker `FD3` и независимый kernel peer evidence **не доказаны**. Прежний `native_saveas_broker_listener.main` остаётся zero-FD deny-only, а внутренний трёхсторонний bridge не подключён к CLI. Синтетический `native_saveas_worker_snapshot.publish_disposable_fixture` не вызывается из worker; CEF по-прежнему имеет собственный отдельно выключенный lab-only in-process SaveAs.

**Следующий GD-31J.27x:** уточнить OS-уровень launcher provenance и проверку actual socket endpoints, границы `SO_PEERCRED` / `pidfd` в разных namespace, согласовать независимую policy-evidence и затем провести offline VM E2E. Все пользовательские DOCX/XLSX/PPTX/PDF, GUP-2/WORM/SMB/Nextcloud и production остаются вне лабораторного пути.

**Не выполнялись:** Python unittest, C++/MSTest, настоящие root/worker процессы, real pidfd/FD transfer, VM, публикация, GitHub Actions, merge в main.
