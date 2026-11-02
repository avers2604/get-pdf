# GD-31J.27y — rights-free FD3 nonce challenge: наблюдение связи broker ↔ worker

**Дата:** 2 ноября 2026 г. **База:** Draft PR №137 / GD31J27x.  
**Статус:** отдельный лабораторный API и регрессионные тесты добавлены в репозиторий, **не запускались**. Никаких реальных Astra Linux VM, root-сервисов, GUI/CEF, SDK, трёхпроцессной передачи FD, ослабления санкций, GitHub Actions или merge.

## Проблема

GD31J27x привязал `SO_PEERPIDFD` к kernel peer **конкретного Unix-сокета**, а GD31J27o проверял только, что в процессе worker FD3 выглядит как `socket:[inode]`. Даже совместно эти необходимые наблюдения **не удостоверяют**, что FD3 действительно является сокетом, на котором worker принимает запрос broker. Для проверки активного использования FD3 добавлен независимый ограниченный challenge/response **без SCM_RIGHTS**, до передачи любых файловых FD.

## Новый лабораторный протокол

`native_saveas_fd3_challenge.py` содержит два явных внутренних API:

**Worker `respond_fd3_once(sock,expected_broker_pid,expected_broker_uid)`**
- Требует Linux AF_UNIX SOCK_SEQPACKET, `sock.fileno()==3`, сверяет `SO_PEERCRED` broker PID/UID/GID (отдельный non-root UID) и `fstat(3)` с `fstat(sock.fileno())`; `st_mode` должен быть socket, inode положителен.
- Через **один** `recvmsg(size+1,ancbufsize=0,MSG_CMSG_CLOEXEC)` получает точно `CHALLENGE_PREFIX + 32-byte nonce`. Любой `MSG_TRUNC/MSG_CTRUNC`, сторонние ancillary/SCM_RIGHTS, неверный размер, нулевая строка nonce или чужая версия протокола ⇒ отказ. Проверяет неизменность FD3 непосредственно перед ответом.
- Отвечает единственным `ACK_PREFIX + same nonce + network-order uint64 fstat(3).st_ino`; `nativePublicationAuthorized=false`, `fd3PairedAttested=false`, `workerExecutableAttested=false`, `releaseReady=false`.

**Broker `challenge_worker_fd3_once(sock,expected_worker_pid,expected_worker_uid,broker_uid)`**
- Проверяет отдельный UID broker и `SO_PEERCRED` worker, читает `/proc/<worker_pid>/fd/3` и требует `socket:[inode]`; не открывает чужой FD.
- Создаёт одноразовый 32-байтный nonce через `os.urandom` и посылает **ровно один** challenge без передачи дескрипторов.
- Через ограниченный `recvmsg(size+1,0,MSG_CMSG_CLOEXEC)` получает единственный ACK, сверяет nonce, inode с kernel `readlink` **до** и **после** ACK. Все варианты недоступного cross-UID procfs, подмены FD3, лишних/усечённых сообщений и непрошеных прав ⇒ отказ; нет повторной отправки.
- По [Linux unix(7)](https://man7.org/linux/man-pages/man7/unix.7.html) при отсутствующем ancillary-буфере избыточные SCM_RIGHTS ядро **автоматически закрывает**, а `MSG_CTRUNC` сигнализирует об усечении. Нулевой `ancbufsize` — сознательная защита от случайного принятия прав в фазе подтверждения канала.

`native_saveas_fd3_challenge_flow.py` содержит **две дополнительные, не подключённые к штатному запуску функции**:
- `lab_broker_fd3_then_denied`: удерживает `SO_PEERPIDFD` worker на всю стадию challenge, требует `requires-independent-review` и неизменно ложные authority flags, затем передаёт в ранее подготовленный `broker.handoff_after_kernel_review`, который повторно проверяет kernel/proc/pidfd и допускает только `state=denied`. В любой ошибке session consumed и replay запрещён; socket-peer pidfd закрывается.
- `lab_worker_fd3_then_denied`: подтверждает на worker FD3, затем вызывает прежний `worker.serve_one`, который **всегда** завершает работу с кодом 2 и не публикует файлы.

## Граница доказательств

Nonce + проверка инода дают **наблюдение активного использования FD3** и защищают от тривиальной переподстановки FD3/повтора ответа в диагностическом окружении. Однако **worker сам сообщает содержимое ACK**, поэтому потенциально скомпрометированный процесс может солгать. Даже с успешным nonce, `SO_PEERPIDFD`, PID/starttime и kernel `/proc` **нельзя** автоматически утверждать:
- доверенность/подпись исполняемого Python/C++ кода, `/proc/pid/exe`, cgroup/systemd start metadata;
- криптографическое или независимое от worker подтверждение двусторонней kernel socket endpoint пары;
- корректную seccomp-BPF, Astra Linux МРД, mount restrictions, отсутствие корпоративных файлов и прав на конечный каталог;
- успешное сохранение, неизменяемость, WORM или production release.

Поэтому `fd3PairedAttested=false`, `workerExecutableAttested=false`, `workerLaunchAttested=false`, `nativePublicationAuthorized=false`, `releaseReady=false`, `approvedForProduction=false` **всегда**, в том числе при положительном echo.

`worker.main`, `worker_dialer.connect_and_inspect_once`, `broker_listener.listen_once` и CEF-приложение **не импортируют** FD3 challenge. Два явных API доступны только будущему отдельному лабораторному runner; **никакие системные unit не устанавливаются и не запускаются**. GD31J27q worker snapshot publisher по-прежнему не подключён и никогда не вызывается этим протоколом.

## Подготовленные проверки, НЕ выполненные

- `test_native_saveas_fd3_challenge.py`: mock-only точный FD3, неверный UID/PID, изменение kernel inode до/после, нулевой/неправильный nonce, усечение, unknown ancillary/SCM_RIGHTS, отсутствие повторной отправки и сохранение authority flags false.
- `test_native_saveas_fd3_challenge_flow.py`: explicit-only проход `socket pidfd → nonce → прежний deny-only handoff`, worker `nonce → serve_one`, early failure не вызывает SCM_RIGHTS handoff.
- `test_native_saveas_fd3_challenge_contract.py`: исходниковая блокировка обычного пути, запрет на опасные OS privileges, file publication и elevation.
- `scripts/office_acceptance.py` — все три suite зарегистрированы **до** restore, `scripts/tests/test_office_acceptance.py` проверяет регистрацию.
- `native_saveas_acceptance_gate.py`: **33 fault cases / 30 mandatory manual E2E gates**. Новые `fd3-nonce-inode-rights-free-no-proven-executable` и `eurooffice-gd31j27y-fd3-nonce-proc-inode-rights-free-e2e`.
- CMake `GETDOC_GD31J27Y_ENABLE_FD3_PROVEN_PUBLICATION_LAB=OFF` и **безусловный** `FATAL_ERROR GD31J27Y_BLOCKED` при ON. Все предыдущие l–x ограничения сохранены.

## Следующий GD-31J.27z

Подготовить независимый **происхождение/идентичность executable и cgroup/systemd launch** worker и broker (подлинные root-owned launch manifest, digest pinned code/runtime и без обхода разных UID). После этого, **на disposable offline Astra Linux VM**, проверить полную трёхпроцессную цепочку с настоящими отдельными UID, namespace, `SO_PEERPIDFD`, challenge без FD, `SCM_RIGHTS` только после проверки, и неизменный итог denial. Без успешных независимых E2E и OS review нет оснований переносить GD31J27q O_TMPFILE-публикацию из лаборатории в worker и включать SaveAs.

**Не выполнялись:** реальная сборка, Python unittest, OS service запуск, реальный SCM_RIGHTS, публикация, GitHub Actions, merge. `main`, пользовательские OOXML/PDF, WORM, GUP-2, SMB, Nextcloud не затронуты.
