# GD-31J.27v — Linux pidfd вместо одной числовой идентичности broker

**Дата:** 2 ноября 2026 года. **База:** PR №134 / GD-31J.27u. **Режим:** внутренний синтетический inspect-only / deny-only, без установленного supervisor и без production Save As. **Ни один тест, сборка, systemd unit, реальный root-процесс или Astra Linux VM не запускались.**

## Зачем требуется pidfd

Пара `SO_PEERCRED(PID, UID, GID)` и `/proc/<pid>/stat:starttime` необходима для проверки связанного Unix broker, но между чтениями PID может быть использован повторно. `pidfd_open(pid, 0)` возвращает файловый дескриптор, привязанный к конкретному процессу на уровне ядра; он не начинает запись, не копирует дескрипторы из чужого процесса и не даёт дополнительных privileges. Открытие pidfd без доверенного изначального launch-pin само по себе **не удостоверяет бинарный файл, его владельца или системную политику**. Поэтому одновременно сохраняются обе сверки времени создания процесса и уже существующие root kernel prerequisites.

Документация Linux: [pidfd_open(2)](https://man7.org/linux/man-pages/man2/pidfd_open.2.html), [pidfd_getfd(2)](https://man7.org/linux/man-pages/man2/pidfd_getfd.2.html). Второй системный вызов **не используется**: ему требуется отдельный ptrace access check, и он не нужен для добровольной передачи через `SCM_RIGHTS`.

## Новый `native_saveas_pidfd_provenance.py`

`pin_broker_process(pid, expected_start_ticks)` действует только на Linux и требует доступной функции Python `os.pidfd_open` и проверенного процесса:

1. Проверяется точный nonzero integer PID и отдельно закреплённый `expected_start_ticks`.
2. Читается `/proc/<pid>/stat` поле22 через `audit.read_broker_start_ticks` **до** pidfd acquisition. Если чтение невозможно или pin не совпадает — отказ.
3. Открывается **ровно один** kernel pidfd через `os.pidfd_open(pid, 0)`, проверяются его `FD_CLOEXEC` и отсутствие перезаписи стандартных FD0–2.
4. Проверяется `starttime` после открытия pidfd; `poll(0)` по `POLLIN/HUP/ERR/NVAL` обязан вернуть **пустой список**. Любое событие либо сбой чтения/ядра — отказ.
5. На время выполнения sender возвращается `PinnedBrokerPidfd`, которым владеет единственная операция. `assert_live()` повторяет kernel time/exit state; `close()` освобождает pidfd и не допускает reuse закрытого FD.
6. Если любая стадия открытия или проверки не прошла, pidfd закрывается; **нет** fallback к `/proc/<pid>/fd`, PID-only, `kill(pid,0)`, `pidfd_getfd`, `ptrace`, `CAP_SYS_PTRACE`, сигналам, пересозданию другого процесса или `subprocess`.

Этот guard **не содержит полномочий**: `processIdentityAttested=false`, `launchAuthorized=false`, `releaseReady=false`. Процесс может быть живым, но злонамеренным или неправильно изолированным. Его executable digest, systemd unit, mount tree, Astra МРД, seccomp BPF и отсутствие WORM/corporate mounts требуют **независимой** проверки.

## Подключение в отправитель GD31J27t

`native_saveas_root_supervisor.RootSendOnce.send` дополнительно к текущим ограничениям `SO_PEERCRED`, root capabilities, namespaces и `expectedBrokerStartTicks`:

- До `open_verified_handles` открывает `pin_broker_process(peer_pid, expectedBrokerStartTicks)`, удерживает ядром закреплённый pidfd и проверяет живость.
- Проводит одноразовый `sendmsg(SCM_RIGHTS)`, **не** вызывает `pidfd_getfd`, не меняет владельцев или UID.
- Сразу после `sendmsg` проверяет `pinned.assert_live()` и прежний независимый `audit.require_broker_lifetime` ещё раз.
- Во всех исходах закрывает 3 preopened FD и pidfd. При сбое до отправки FD передавать нельзя; при сбое **после** передачи результат считается **неопределённым**, повторная отправка/автоматическая очистка не допускаются.

Этот код находится **только** во внутреннем библиотечном sender. `native_saveas_broker_listener.main()` не вызывает 3-party bridge, системный root unit GD31J27t — `ExecStart=/usr/bin/false`, `RefuseManualStart=yes`, без [Install]. Никаких реальных IPC-соединений новой версией не создаётся.

## Написанные тесты и матрица (НЕ выполнялись)

- `test_native_saveas_pidfd_provenance.py`: синтетические kernel open/poll/close, `FD_CLOEXEC`, отказ при PID reuse перед/после открытия, `POLLIN/HUP/ERR/NVAL`, недоступном os.pidfd_open/EPERM/ENOSYS, невалидных числовых pin, закрытие pidfd на ошибке и отсутствие fallback.
- `test_native_saveas_root_sender_pidfd_guard.py`: mock-контракт sender — pin обязан открываться до source FD; broker exit до открытия не отправляет, exit после отправки оставляет неопределённый статус и запрещает повтор, pidfd в обоих случаях закрывается.
- `test_native_saveas_pidfd_source_contract.py`: независимая исходниковая проверка отсутствия `kill/ptrace/pidfd_getfd/setns`, порядков `pin→open→send→check→close`, hard-block CMake и запрета вызова из production listener.
- Адаптированы прежние `test_native_saveas_root_supervisor.py`, `test_native_saveas_root_supervisor_audit.py`, чтобы они **макетировали** kernel pidfd и не затрагивали реальные процессы.
- Три offline suite зарегистрированы в `scripts/office_acceptance.py`, добавлен тест самого плана. Прежняя строгая матрица `native_saveas_acceptance_gate.py` расширена до **30 fault cases / 27 mandatory manual E2E gates**, новые `broker-pidfd-exit-poll-unsupported-kernel-no-fallback` и `eurooffice-gd31j27v-pidfd-broker-liveness-identity-e2e`.

CMake: `GETDOC_GD31J27V_ENABLE_PIDFD_PUBLICATION_LAB=OFF`, **FATAL_ERROR GD31J27V_BLOCKED** при попытке включения. Все прежние l–u блокировки сохранены.

## Что остаётся

На disposable offline Astra Linux VM независимый оператор должен подтвердить `os.pidfd_open` версию Python/kernel, `procfs` visibility, actual `SO_PEERCRED` после UID switch, namespace/mount/ACL/MAC, seccomp, отсутствие корпоративных файлов и лишних FD, смерть broker во всех точках передачи, PIDs в разных namespace, `systemd` политики. Особенно важно, что **pidfd не отменяет ограничения cross-UID /proc**: если наблюдение worker недоступно в broker, текущий `handoff_after_kernel_review` должен отказаться, а не обходить проверку.

**Следующий GD-31J.27w:** независимый OS launch evidence и capability-boundary worker с pidfd, либо архитектура подтверждаемого supervisor-issued pidfd без расширения привилегий broker. До аудита такой механизм не должен открывать SavedAs. Реальная публикация в worker, CEF migration, Office-пользовательские документы, GUP-2, WORM, SMB, Nextcloud — вне данного PR.

Режим: только source/tests, нет выполнения реальных тестов, сборок, GitHub Actions, merge в main или фоновых процессов.
