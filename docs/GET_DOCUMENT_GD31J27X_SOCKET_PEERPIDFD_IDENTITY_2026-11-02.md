# GD-31J.27x — Linux SO_PEERPIDFD привязывает kernel worker process к Unix IPC

**Дата:** 2 ноября 2026 г. **База:** PR №136 / GD-31J.27w.  
**Статус:** внутренние диагностические исходники и отложенные тесты, **без запуска Python unittest/C++/Astra Linux VM/systemd/CEF/EuroOffice и без реальной передачи SCM_RIGHTS**. Пользовательские документы и production SaveAs недоступны.

## Устранённая неопределённость

До GD31J27x broker сверял (1) kernel `SO_PEERCRED` с ожидаемыми PID/UID и (2) независимо открытый `os.pidfd_open(worker_pid,0)` с start ticks. Это улучшает жизненный цикл процесса, но само по себе не гарантирует, что **именно подключённый Unix-сокет** связан с полученным pidfd. Также `/proc/<pid>/fd/3 → socket:[inode]` удостоверяет лишь наличие сокета FD3, не тождество его second endpoint с broker-каналом.

Начиная с **Linux 6.5** `SO_PEERPIDFD` возвращает новый fd на процесс, являющийся **kernel peer именно этого Unix-соединения** (Linux UAPI `SO_PEERPIDFD=77` на поддерживаемых generic arch). Документация/код: Linux commit `7b26952a91cf65ff1cc867a2382a8964d8c0ee7d`, [Linux unix(7)](https://man7.org/linux/man-pages/man7/unix.7.html), [systemd getpeerpidfd](https://github.com/systemd/systemd/blob/main/src/basic/socket-util.c). Значение UAPI жёстко ограничено x86/x86_64/aarch64/riscv64; для неизвестной архитектуры **отказ**, а не догадка о числовом socket constant. На старом ядре (в том числе на неизвестной версии Astra Linux) `ENOPROTOOPT/EINVAL/EOPNOTSUPP` ⇒ **отказ**. Нельзя возвращаться к `SO_PEERCRED`-only или `pidfd_open`-only как к эквивалентной проверке.

## Код GD31J27x

`native_saveas_socket_peerpidfd.py` содержит `pin_socket_peer(sock, expected_worker_pid, expected_worker_uid, broker_uid, expected_worker_start_ticks)`. Он:

1. Требует Linux, явную архитектуру generic UAPI, `AF_UNIX/SOCK_SEQPACKET`, закреплённые корректные UID/PID/starttime и отличающийся UID broker.
2. Сначала получает **SO_PEERCRED** из самого socket и сверяет PID/UID/GID с внешним pin. Он учитывает известное свойство `SO_PEERCRED`: учётные данные фиксируются во время `connect/listen/socketpair`; поэтому upstream GD31J27p требует **подключение уже после смены UID внешним OS-supervisor**.
3. Только затем вызывает **`getsockopt(SOL_SOCKET, SO_PEERPIDFD, sizeof(int))`**. Возвращаемое ядром целое — **новый fd**, а не PID. Точно 4 байта, FD≥3, `FD_CLOEXEC`. Никакой `pidfd_getfd`, `ptrace` или проверки одного PID вместо требуемого системного вызова.
4. `PinnedSocketPeer.assert_live()`: независимо наблюдает `/proc/<worker pid>/stat:starttime` и делает `poll(0)` по полученному от peer socket pidfd. Любой exit/ready/error, mismatch birthtime, недоступный /proc, закрытый fd ⇒ отказ.
5. Закрывает kernel-created pidfd **во всех исходах**, в том числе при ошибке проверки. Даже положительное наблюдение возвращает `socketPeerAttested=false`, `nativePublicationAuthorized=false`, `releaseReady=false`. Нельзя назвать его полной аттестацией worker.

**`native_saveas_fd_broker.handoff_after_kernel_review`** теперь удерживает **две** независимые ссылки на процесс:
- socket-bound `peer_reference = pin_socket_peer(...)`;
- отдельную `pinned = pin_worker_process(pid,starttime)`.

Они остаются открытыми через существующую проверку `read_actual_handoff` (точный FD3, /proc status/mountinfo, namespaces, SID/UID), однократный `session.handoff(sendmsg/recvmsg)` и два `assert_live()`. Результат обязан быть строго `state=denied`, все release/nativeSave/write/production flags false. Все ссылки закрываются в `finally` даже при ошибке закрытия другой. Session consumed, нет повторной передачи и нет автоматической очистки.

**Оставшаяся граница:** socket-bound pidfd удостоверяет **kernel peer process**, но **не является доказательством, что на стороне worker именно FD3 — тот же конкретный endpoint**. Совпадение FD3 нужно установить отдельным двусторонним non-forgeable challenge и OS evidence до подключения реальной публикации. Подписанный/закреплённый executable, cgroup/systemd unit, Astra МРД, seccomp BPF, отсутствие корпоративных mounted directories, независимые allowlist SHA и actor authority также **не доказаны**.

## Тесты подготовлены, но НЕ запускались

- `test_native_saveas_socket_peerpidfd.py`: только моки socket/getpeerpidfd/FD_CLOEXEC/poll/startticks; unsupported kernel/architecture, плохие credentials, неправильная длина getsockopt, выход peer, PID reuse и обязательное закрытие fd.
- `test_native_saveas_socket_peerpidfd_broker.py`: socket-peer pidfd должен держаться через exact worker procfd preflight, single sendmsg/denial reply, затем закрыться. ENOPROTOOPT/недоступность до отправки и exit после отправки ⇒ **отказ без replay**.
- `test_native_saveas_socket_peerpidfd_contract.py`: запрет на fallback/ptrace/setuid/включение production SaveAs, проверка порядка `SO_PEERPIDFD → FD3 → single handoff → second check → close`.
- Ранее написанные `test_native_saveas_worker_pidfd_handoff.py` и `test_native_saveas_supervisor_broker_guard.py` адаптированы на socket-peer mock без доступа к реальному ядру.
- В `scripts/office_acceptance.py` три offline pre-restore тестовых шага и ручной `eurooffice-gd31j27x-socket-peerpidfd-identity-e2e`. Строгая матрица `native_saveas_acceptance_gate.py`: **32 fault cases / 29 обязательных manual E2E gates**.
- CMake `GETDOC_GD31J27X_ENABLE_SOCKET_PEER_PUBLICATION_LAB=OFF`; при ON принудительный `GD31J27X_BLOCKED` / `FATAL_ERROR`. Все прежние l–w блокировки сохраняются.

## Что дальше

**GD-31J.27y:** отдельно проверить двустороннюю привязку worker FD3 к socket endpoint и origin process/cgroup/exec через доверенный supervisor. Не включать новые capabilities ради чтения чужих `/proc`; на Astra Linux с kernel <6.5 поведение **fail-closed**, возможен отдельный согласованный вариант с независимым OS supervisor вместо тихого fallback. Затем выполнить реальные минимальные E2E и fault injection на **одноразовой офлайн-VM**, без реальных DOCX/XLSX/PPTX, PDF, WORM, GUP-2, SMB, Nextcloud и network. До системного аудита не включать worker-side synthetic `O_TMPFILE` publisher.

**Не выполнялись:** сборка, реальные тесты, установка root service, выделение UID, publication, merge, GitHub Actions.
