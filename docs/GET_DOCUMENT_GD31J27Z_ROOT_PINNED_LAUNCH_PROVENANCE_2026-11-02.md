# GD-31J.27z — независимые исходные pins, исполняемый образ и cgroup worker/broker

**Дата:** 2 ноября 2026 г. **База:** Draft PR №138 / GD31J27y.  
**Результат:** отдельная read-only проверка необходимых фактов запуска, source-only регрессионные тесты и blocked CMake. **Это НЕ заверенный процессный launcher, НЕ production native SaveAs, НЕ полная аттестация.** Реальные сборки, Python unittest, root services, systemd, Astra Linux VM, многопроцессный SCM_RIGHTS и fault injection НЕ выполнялись.

## Актуальная проблема

В GD31J27y broker и worker подтвердили только активное использование FD3, nonce и kernel inode в одноразовом диагностическом обмене. `SO_PEERPIDFD` закрепляет connected Unix peer process, но исполняемый файл этого процесса, его импортируемый Python-код, профиль systemd/cgroup и источник root-owned pins **не были проверены**. Процесс, запущенный из постороннего каталога, способен отвечать на nonce так же, как доверенный worker.

## Новый `native_saveas_launch_provenance.py`

Фиксированный операторский манифест **`/etc/getdoc-lab/gd31j27z-launch-pins.json`** (в рабочей системе отсутствует, не устанавливается этим PR) допускает только `schema`, `mode=inspect_only`, `publishEnabled=false`, `laboratoryOnly=true` и **ровно две** независимые роли `broker` и `worker`. Для каждой роли: точный `pid/startTicks/uid/gid`, ожидаемый путь `cgroup v2`, полный `argv`, kernel `exeTarget`, независимые SHA-256 **исполняемого интерпретатора** и **Python-файла**.

Разрешённые Python source basename зафиксированы непосредственно в коде:
- broker — `/opt/getdoc-lab/bin/native_saveas_broker_listener.py`;
- worker — `/opt/getdoc-lab/bin/native_saveas_fd_worker.py`.

Ожидаемый `argv` строго равен `/usr/bin/python3 -E -s -S <fixed-source>`; Python interpreter target в `/proc/pid/exe` отдельно привязан к `/usr/bin/python3.X`. Это только **декларативное требование к будущему OS launcher**, сам launcher и зависимости ещё не подготовлены. `-I` и `-S` не доказывают, что все импортируемые файлы/системные .so доверены.

### Root-owned источник pins

`load_root_owned_manifest()` использует лишь фиксированный путь. `/etc` и `/etc/getdoc-lab` открываются через `O_NOFOLLOW|O_DIRECTORY`, должны принадлежать root, не допускать записи group/world; манифест — отдельный regular inode, owner root, mode `0600`, nlink1, максимум 4096 байт, без дубликатов JSON-полей, произвольных флагов, каталогов пользователя или CLI-аргументов. Чтение проверяет стабильность inode до/после. Если broker с непривилегированным UID не имеет доступа к root-only файлу, алгоритм **отказывает** — нельзя обходить этот факт сменой прав на 0644, подменой pins из IPC/CEF или запуском broker как root.

### Наблюдение процессов

`inspect_live_process(profile)` сначала требует точную роль, открывает удерживаемый `pidfd` с внешне закреплённым `startTicks` и затем делает bounded-read:

1. `/proc/<pid>/status`: четыре фактических `Uid/Gid` (real/effective/saved/fs) должны совпадать с pinned nonroot UID/GID. Повторные/malformed поля — отказ.
2. `/proc/<pid>/cmdline`: точные NUL-separated `argv`, без `-c`, лишних файлов, модулей, параметров, пользовательских путей. Эти байты могут быть изменены процессом, поэтому **не считаются криптографическим доказательством запуска**.
3. `/proc/<pid>/cgroup`: одна точная запись `0::/system.slice/getdoc-gd31j27z-{broker|worker}.service\n`. Legacy/mixed/deleted cgroup и другие пути не подходят. Запись cgroup — наблюдение membership, **не доказательство, кто и с какой политикой создал unit**.
4. `/proc/<pid>/exe`: чтение kernel link и открытие **действительного исполняемого inode через procfs**, проверка regular root-owned nlink1, отсутствие group/world-write, bounded (≤64 MiB), SHA-256 и стабильность inode до/после чтения. Сравниваются **два независимых pin-а**: точный link target и digest.
5. Python source под `/opt/getdoc-lab/bin` открывается по заранее известному basename и безопасным root-owned ancestor-dirfd; `O_NOFOLLOW`, regular root-owned nlink1, SHA-256, стабильность inode.
6. После обоих digest повторно читаются `cmdline`, `cgroup`, `status`, kernel `exe` link, `startTicks` и проверяется живость исходного pidfd. Подмена `execve`, cgroup или идентификаторов между проверками приводит к отказу. Это уменьшает, но **не исключает** TOCTOU в недоверенном процессе.

`inspect_pair` проверяет обе роли по отдельности. Даже полное соответствие возвращает `state=requires-independent-review` и **все** `workerExecutableAttested/brokerExecutableAttested/trustedRootManifestAttested/cgroupAttested/launchAuthorized/nativePublicationAuthorized/writePermissionGranted/releaseReady/approvedForProduction=false`. Это не попытка создать криптографическую подпись одного только сравнения SHA.

## Критические ограничения Astra Linux

- [Linux proc_pid_exe(5)](https://man7.org/linux/man-pages/man5/proc_pid_exe.5.html): чтение чужого `/proc/pid/exe` проверяется через `PTRACE_MODE_READ_FSCREDS`; в закрытой среде с разными UID broker может не получить доступ. Нельзя автоматически выдавать `CAP_SYS_PTRACE`, обходить protect-proc или доверять только `cmdline`.
- [Linux cgroup v2 documentation](https://www.kernel.org/doc/html/latest/admin-guide/cgroup-v2.html): `/proc/PID/cgroup` показывает membership, у cgroup v2 формат `0::$PATH`. Точный путь не равен подписанной unit policy; также нельзя считать, что `/proc/pid/cgroup` во всех namespace будет содержать ожидаемый путь.
- Фиксированный SHA интерпретатора и **одного** source-файла не охватывает импортируемые Python-модули, C extensions, `ld.so`, библиотеки SDK, optional plugin entrypoint, окружение и путь загрузки. Полноценная supply-chain проверка должна охватить весь transitive closure + независимый package/signing provenance.
- `/proc/pid/cmdline` и membership cgroup могут изменяться; read-only snapshots и pidfd не удостоверяют file identity и systemd unit. Ни одна комбинация этих наблюдений не позволяет самостоятельно считать sandbox или worker OS-attested.
- GD31J27y FD3 nonce и GD31J27x SO_PEERPIDFD по-прежнему **explicit-only**. Новый module **не импортируется** broker listener, worker main, CEF или стандартным FD3 flow. Ни один сервис не устанавливается, root supervisor GD31J27t остаётся `ExecStart=/usr/bin/false`.
- Передача `SCM_RIGHTS`, synthetic `O_TMPFILE` publisher GD31J27q и in-process CEF SaveAs остаются выключенными для реальных документов.

## Отложенные тесты (НЕ выполнялись)

- `test_native_saveas_launch_provenance.py`: exact root manifest/duplicate-keys/unsafe pin, source hashes, неправильные UID/GID/argv/cgroup, deleted binary, missing `pidfd`/cross-UID procfs, PID reuse, изменение cmdline между первой и последней выборками; все разрешения false.
- `test_native_saveas_launch_provenance_contract.py`: source-only запрет любых `setuid/chmod/chown/ptrace`, чтения произвольных пользовательских путей и подключения проверки к production; root-owned fixed paths и CMake hard-stop.
- `scripts/office_acceptance.py`: обе suites добавлены до restore, `scripts/tests/test_office_acceptance.py` проверяет gate.
- `native_saveas_acceptance_gate.py` содержит **34 fault cases / 31 mandatory manual E2E gates**; новый случай `root-pinned-exe-cgroup-argv-uid-sha-no-launch-authority` и `eurooffice-gd31j27z-root-pinned-exe-cgroup-launch-review-e2e`.
- CMake `GETDOC_GD31J27Z_ENABLE_LAUNCH_PROVENANCE_PUBLICATION_LAB=OFF`, при ON всегда `GD31J27Z_BLOCKED` / FATAL_ERROR. Предыдущие l–y запреты не ослаблены.

## Следующий этап GD-31J.27aa

Подготовить **независимый, подписанный/проверяемый manifest для всего Python dependency closure и OS unit policy**; затем обсудить безопасную доставку результатов проверки от root supervisor к отдельному UID broker без `ptrace` и доверия произвольному IPC. Только после этого есть смысл проводить реальный трёхпроцессный synthetic E2E на disposable offline Astra Linux VM. До результата E2E не подключать публикацию worker или пользовательские документы.

**Отсутствовали:** реальные тесты, сборки, VM, системные службы, os.pidfd_open, SHA реальных binaries, прочие системные изменения, GitHub Actions и merge. Основная ветка `main`, WORM, GUP-2, Nextcloud, SMB и документы пользователя не менялись.
