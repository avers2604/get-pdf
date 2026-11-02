# GD-31J.27r — приём preopened FD от внешнего supervisor и однократная передача в worker

**Дата:** 2 ноября 2026 г. **База:** Draft PR №130 (GD31J27q). **Статус:** исходники и тестовые контракты добавлены, реальные проверки и сборки не выполнялись. Только Linux/Astra disposable VM и искусственные документы; исходные пользовательские DOCX/XLSX/PPTX, SMB, WORM, GUP-2, Nextcloud не затрагиваются.

## Фактически внесённые изменения

Новый `native_saveas_fd_provisioning.py` предоставляет два **внутренних** API для будущего непривилегированного broker (НЕ CLI, НЕ launcher, НЕ production handler):

- `receive_pinned_roles(supervisor_sock, trusted_supervisor_uid=0, expected_supervisor_pid, worker_uid, independent_fixture_hashes, approved_disposable_vm)` — принимает единственный bounded запрос от уже подключённого **root-owned OS-supervisor** через `AF_UNIX/SOCK_SEQPACKET`, проверяет **kernel SO_PEERCRED** с exact PID и root UID0; принимает ровно 3 FD в `recvmsg(MSG_CMSG_CLOEXEC)` и отказывает при `MSG_CTRUNC/MSG_TRUNC`, unknown ancillary, неверном числе, повторном/неоднозначном сообщении, неправильном запросе или SHA. Возвращённые FD принадлежат вызывающему коду и требуют закрытия.
- `relay_pinned_once(session, supervisor_sock, worker_sock, ...)` — принимает только один такой пакет, затем вызывает существующий `native_saveas_fd_broker.handoff_after_kernel_review`. Последний **сначала** проверяет `SO_PEERCRED`, UID worker, PID/startticks, защищённый `/proc/<pid>/fd`, затем единственным `sendmsg(SCM_RIGHTS)` передаёт FD уже подключённому worker. Сеанс в любом исходе помечается потреблённым и все полученные FD закрываются. Неустранимый сбой `sendmsg` не вызывает повторной отправки.

### Важная тонкость разных UID

Broker работает под отличным от worker UID и не должен иметь доступ к `0700` каталогам worker. Поэтому `verify_pinned_handles` **не** пытается делать `openat` внутри этих каталогов. Он проверяет полученные **already-open** FD через `fstat` и `fcntl(F_GETFL/F_GETFD)`: O_RDONLY, CLOEXEC, владелец **worker UID**, scratch/output directories 0700, source regular nlink1, размер ≤32 MiB, отсутствие group/world-write, один `st_dev` и непересекающиеся inode каталогов, SHA256 source через `pread`. Хеш обязан совпадать с **отдельно закреплённым в trusted OS supervisor каталоге** значением для одного из трёх synthetic fixtures; `sourceSha256` из самого сообщения не является авторизацией.

Marker bytes и exact source pathname должны проверяться **самим worker под своим UID**, когда он получит FD. Поэтому дополнительно усилен `native_saveas_fd_worker.inspect_fds`:

- source FD теперь обязан принадлежать worker UID (ранее проверялся тип/содержимое, но не владелец);
- `fstatat(scratchFd, fixtureName, follow_symlinks=False)` до и после digest подтверждает, что переданный открытый FD — **точно тот же regular inode** с заранее разрешённым именем **внутри приватного scratch**. Сторонний source FD с подходящим SHA или symlink под разрешённым именем — отказ;
- при несоответствии имени, inode, владельца, размера, mtime/ctime или marker никакое право на публикацию не выдаётся.

### Почему это ещё не полноценный запуск

**Ограничения сохраняются:**

1. Внешний привилегированный root-supervisor **отсутствует**: существующие systemd шаблоны GD31J27p остаются заблокированными, не создают работоспособного root→broker соединения и не формируют worker-owned 0700 directories. Нельзя подменять этот компонент GUI, запускать broker под root или временно ослаблять directory mode до 0770.
2. `native_saveas_broker_listener.listen_once` по умолчанию всё ещё проводит **нулевой FD**, диагностический handshake. `relay_pinned_once` — отдельный программный API, пока **не подключён к listener**, потому что systemd/UID/mount/FD4 policy и trusted root supervisor должны быть подтверждены на одноразовой Astra VM.
3. `native_saveas_fd_worker.serve_one` по-прежнему **inspect_only, deny-only**, никак не вызывает GD31J27q `publish_disposable_fixture`. Внешняя передача FD сама по себе **не** разрешает `O_TMPFILE`, `linkat`, файловую запись или выпуск документа.
4. `approved_disposable_vm=True` и словарь digest — параметры вызова, не аттестация. Root kernel credential подтверждает UID0 peer, но не конкретную доверенную программу или правильный набор системных политик. Требуются OS provisioning, pinned executable digest/manifest, seccomp BPF, AppArmor/МРД Astra, отсутствие корпоративных mounts, запрет ptrace и независимый аудит.
5. Отсутствие доступа broker к защищённому `/proc/<worker_pid>/fd` между UID приводит к fail-closed, а не отключению проверки. `SO_PEERCRED`/PID startticks и owner snapshot не доказывают длительную неизменяемость конечного файла.
6. Никаких `unlink`, `rename`, fallback к file paths или автоматической послеаварийной очистки. Ошибки после публикации из GD31J27q — только ручной review.

## Подготовленные, но НЕ выполненные тесты

- `test_native_saveas_fd_provisioning.py`: собственные synthetic tempfiles, проверка владельца worker UID, двух private каталогов 0700, source SHA, отказ при writable FD, mode 0750, занятом имени, root credential mismatch, `MSG_CTRUNC`, лишнем FD, однократность и закрытие FD. **No real root worker execution.**
- `test_native_saveas_fd_provisioning_contract.py`: source-only запрет публикации/privilege escalation, строгие три SCM_RIGHTS роли и соответствующие блокировки, отсутствие подключения через `native_saveas_broker_listener` и CEF.
- Обновлён `test_native_saveas_fd_worker.py`: исходные synthetic test fixtures помещены внутрь разрешённого scratch; добавлен отрицательный сценарий, где FD с правильным SHA расположен **вне scratch** или разрешённое имя — symlink.
- `scripts/office_acceptance.py` регистрирует два новых offline pre-restore тестовых шага, `scripts/tests/test_office_acceptance.py` проверяет их. `native_saveas_acceptance_gate.py` содержит **26 fault cases / 23 mandatory manual E2E gates** (новый `eurooffice-gd31j27r-root-supervisor-worker-owned-fd-relay-e2e`).
- Новый CMake feature flag `GETDOC_GD31J27R_ENABLE_TRUSTED_FD_RELAY_PUBLICATION_LAB` **OFF**, его включение принудительно вызывает `GD31J27R_BLOCKED: ...` / `FATAL_ERROR`. Все прежние l–q ограничения остаются.

**Тесты не запускались, C++/Python не компилировались, реальная VM не использовалась, merge в main не делался и GitHub Actions не включались.** Никакой green/production статус не утверждается.

## Что дальше

**GD-31J.27s:** подготовить проверяемое, но по умолчанию выключенное интеграционное соединение *root supervisor → broker listener → separate UID worker*, доказать kernel peer credentials/FD lifecycle на disposable Astra VM, проверить end-to-end `SCM_RIGHTS` и deny-only результат. Лишь после этого отдельно обсуждать подключение GD31J27q publication через worker IPC, обновление orphan recovery и выключение in-process CEF пути. Реальные пользовательские документы и WORM оставлять вне лаборатории.
