# GD-31J.27t — фиксированный synthetic FD sender в root-supervisor (не включён)

**Дата:** 2 ноября 2026 г. **База:** GD-31J.27s / Draft PR №132.  
**Состояние:** исходники и регрессионные контракты подготовлены, **ни один Python/C++ тест, сборка, systemd, root-UID процесс, Astra Linux VM, реальный SCM_RIGHTS и fault-injection не запускались**. Нельзя считать готовой службу supervisor, изоляцию Linux или Save As. Изменения исключительно для disposable offline VM с синтетическими DOCX/XLSX/PPTX без реальных корпоративных данных.

## Что добавлено

`native_saveas_root_supervisor.py` — внутренний Python-модуль, не имеющий `main()`, CLI или установки системной службы. Он предоставляет три логических операции:

1. `read_pinned_manifest()`: **фиксированный**, независимый от пользовательского JSON, CLI и среды путь `/etc/getdoc-lab/gd31j27t-root-supervisor.json`. Все сегменты открываются по `dir_fd` с `O_NOFOLLOW|O_CLOEXEC`; оба родительских каталога должны принадлежать root, не быть group/world writable, manifest — root-owned `0600`, ровно одна ссылка и максимальный размер 2048 байт. Строгий JSON с запретом дубликатов и неизвестных полей, только `operation=inspect_only`, `publishEnabled=false`. Все три synthetic fixture SHA должны быть независимо закреплены в manifest.
2. `open_verified_handles(config)`: открывает **только** `/run/getdoc-gd31j27t-disposable/` — предварительно созданную operator-controlled root-owned 0700 директорию. Внутри должны **уже существовать** два worker-owned каталога с точными basename `gd31j26-private-ABCDEF` и `gd31j25-output`, оба mode0700, содержащие ожидаемые worker-owned marker-файлы. Открывается только allowlisted fixture, например `gd31j6_writer.docx`, а не присланный произвольный путь. Все три FD — `O_RDONLY|O_CLOEXEC|O_NOFOLLOW`; контролируются через ранее добавленный `worker.inspect_fds` (source↔scratch inode binding, owner UID, 32 МиБ, single-link regular, SHA256, markers, конечного имени ещё нет). Нет изменения прав, каталогов, файлов или очистки.
3. `RootSendOnce.send(connected_sock, config, gate=VM_TEST_ONLY_GATE)`: предназначен **только для уже запущенного под real/effective UID0 доверенного OS-супервизора**, имеющего уже connected AF_UNIX SOCK_SEQPACKET канал к broker. Сверяет `SO_PEERCRED` с `brokerUid/brokerGid/expectedBrokerPid` из отдельного root-owned manifest, затем **одним** `sendmsg([inspect_only JSON], [SCM_RIGHTS с ровно 3 FD])` отправляет данные. При partial send, EOF, EPIPE, SIGKILL или timeout **нет повторной передачи**. Session consumed до первой попытки, все source/scratch/output FD закрываются даже при отказе. Никаких `os.setuid`, `subprocess`, `mkdir`, `unlink`, `chown`, `chmod`, автоматической файловой записи или публикации.

`VM_TEST_ONLY_GATE` — явный тестовый флаг, **не секрет, не криптографическая подпись и не OS аттестация**. Он не должен заменять независимые проверки VM и разрешения оператора. `RootSendOnce` возвращает лишь `state=sent-unattested`, все `nativePublicationAuthorized/nativeSaveProven/documentVerified/saveVerified/releaseReady/approvedForProduction/writePermissionGranted/trustedSupervisorAttested=false`. Отправка FD не является подтверждением даже их получения.

## Root-DAC и необходимость отдельной VM

Важное ограничение: **UID0 без соответствующей DAC capability может не иметь права прохода через worker-owned 0700 директории**. Простой системный unit с `CapabilityBoundingSet=` и без capabilities намеренно не способен выполнить actual `open_verified_handles`. Для будущего работающего supervisor необходимо совместно доказать минимальный доступ к worker-owned private directories **и** изоляцию mount namespace. Рассмотрение `CAP_DAC_READ_SEARCH` требует отдельного security review: способность обходить DAC может открыть **всю** файловую систему. Нельзя автоматически включать или рекомендовать такую capability на рабочем ПК без неизменяемого mount allowlist, ограничений Astra Linux и тестов.

`getdoc-gd31j27t-root-supervisor.blocked.service` — **неработоспособный образец требований**, не готовый сервис: `ExecStart=/usr/bin/false`, `RefuseManualStart=yes`, обязательный никогда автоматически не создаваемый approval marker, отсутствует секция `[Install]`/`WantedBy`. Он не запускает Python, не создаёт учётные записи, FD или соединение.

## Точное положение в архитектуре

- GD31J27s `native_saveas_three_party_bridge` остаётся **неподключённым** к штатному broker CLI. Он ожидает уже подключённый канал root→broker и отдельный worker→broker с pinned PID/startticks и реальными UID.
- GD31J27t создаёт **только вызываемую явно функцию отправителя** на root стороне. Не создаёт broker listening socket, не меняет UID и не выполняет exec. Реальный системный supervisor с проверяемой ОС политикой и достаточным контролем FD всё ещё НЕ развёрнут.
- GD31J27q `native_saveas_worker_snapshot.publish_disposable_fixture` — по-прежнему отдельный *unwired* синтетический алгоритм. Worker `serve_one` продолжает `inspect_only` и **всегда** отвечает denial. CEF NativeSaveAsPublish остаётся отдельным экспериментальным in-process путём, не соединённым с новым worker.
- Даже в успешном FD-handoff `root peer SO_PEERCRED` подтверждает UID, но не подпись бинарного кода, seccomp BPF, доступы Astra МРД, отсутствие корпоративных mounts/секретов, filesystem durability и MS Office совместимость.

## Тестовые контракты и ручные блокировки

Добавлены `test_native_saveas_root_supervisor.py` и `test_native_saveas_root_supervisor_contract.py`: точный manifest/JSON + duplicate keys, запрет произвольных путей/unknown keys, root-only UID, post-connect `SO_PEERCRED`, отказ неверным broker PID/GID/UID до открытия FD, synthetic fixture + marker/sha и проверка single sendmsg + закрытия FDs, partial send без повторной отправки, блокировка systemd specimen и отсутствие runtime импорта во всех CEF/worker/broker маршрутах. Фикстуры не выходят за пределы disposable temporary directory; реальные UID0/системные службы не запускаются.

В `scripts/office_acceptance.py` добавлены два pre-restore offline шага и ручной `eurooffice-gd31j27t-root-supervisor-fixed-fd-sender-e2e`, матрица `native_saveas_acceptance_gate.py` теперь содержит **28 fault cases / 25 mandatory manual gates**. CMake опция `GETDOC_GD31J27T_ENABLE_ROOT_SUPERVISOR_PUBLICATION_LAB=OFF`, при включении обязательный `GD31J27T_BLOCKED` / `FATAL_ERROR` и сохранение всех l–s ограничений.

## Что необходимо сделать следующим (GD-31J.27u)

1. Независимый аудит безопасности кода и контрактов root-owned manifests, rootDAC/syscall allowlist, отдельной worker-owned FS, доступов Astra МРД, групп IPC и монтирования; **только на disposable VM**.
2. Решение, как ОС сможет создать настоящие connected post-UID socket channels и exact worker/source directory FDs без ослабления `0700`. До этого реальная E2E пересылка не заявляется.
3. Отдельный staged OS runner с операторским контролем без автоматического включения; доказательство `SO_PEERCRED`, независимого pinned executable digest, времени жизни PID и отсутствия дополнительных inherited FDs.
4. Только после завершения предыдущих независимых gate — точечное подключение worker-side synthetic publication и повторные `O_TMPFILE/linkat/fsync` аварийные испытания. Производственный SaveAs, пользовательские документы, WORM, GUP-2, Nextcloud и release scope остаются заблокированы.

**GitHub Actions, main, production configuration, пользовательские файлы и корпоративные пути не изменялись.**
