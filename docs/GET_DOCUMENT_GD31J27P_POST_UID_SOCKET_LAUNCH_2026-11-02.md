# GD-31J.27p — реальный post-UID broker/worker SOCK_SEQPACKET, пока без Save As

**Дата:** 2 ноября 2026 года. **Предшественник:** Draft PR №128 (GD-31J.27o).  
**Статус:** готов исходный код запускаемого диагностического соединения и проекты блокированных systemd units. **Не выполнялись** Python tests, C++ сборка, CEF/Qt/EuroOffice SDK, реальный systemd/Astra Linux запуск или VM fault injection. Это **не production**, не утверждённый launcher, не завершённая изоляция и не опубликованный OOXML.

## Что реально сделано на шаге p

Предыдущие l–o определили строгий `SO_PEERCRED` и `SCM_RIGHTS` протокол, но не содержали **реального соединяющегося с broker процесса после установки UID**. Это важно: согласно `unix(7)`, peer credentials в Unix domain sockets фиксируются при `connect(2)`, `listen(2)` либо `socketpair(2)`. Если socketpair создан до `setuid`, `SO_PEERCRED` может отражать старые права.

В p подготовлены **оба исполняемых непривилегированных конца** для будущей disposable офлайн-ВМ. Они не меняют UID, не создают системные учётные записи и не используют shell, а полагаются на отдельно проверенный запуск ОС:

1. **`native_saveas_broker_listener.py`** должен быть **уже запущен** внешним systemd/service supervisor под своим non-root broker UID/GID. Он получает проверенный статический root-owned policy, сверяет действительные `getuid/geteuid/getgid/getegid`, защищённый broker-owned каталог `/run/getdoc-gd31j27p-disposable` (0750) и отказывается, если `worker.sock` уже существует. Создаёт `AF_UNIX/SOCK_SEQPACKET` **после выбора UID** с socket mode0660, принимает ровно одно соединение за 5 секунд и сверяет реальные `SO_PEERCRED` worker UID/GID/PID. Отправляет только `state=denied`, **без SCM_RIGHTS**, закрывает соединение; не удаляет/не перезаписывает чужой socket path.
2. **`native_saveas_worker_dialer.py`** должен быть **уже запущен** под отдельным non-root worker UID/GID. Он читает строго один защищённый **root-owned** policy `/etc/getdoc-lab/gd31j27p-offline-worker.json` через `O_NOFOLLOW|O_CLOEXEC` и file-fd проверку; параметры не передаются через CLI, пользовательский документ, строку подключения или env. Фиксированный Unix socket path и его каталог сверяются `lstat` по owner/mode/group; worker должен иметь primary IPC GID. До подключения ожидаются только FD0–2. Он создаёт **свой собственный новый** connected SOCK_SEQPACKET на FD3 **после смены UID** внешней ОС и проверяет действительный broker UID/GID через `SO_PEERCRED`. Изменение inode pathname вокруг `connect` ⇒ отказ. При положительном preflight вызывает только существующий `native_saveas_fd_worker.serve_one(channel)`, который всегда завершает исполнение отказом (код2, `inspect_only`).
3. **Оба пути — диагностические.** Listener не передаёт ни одного файлового дескриптора, поэтому worker отклоняет zero-FD пакет и возвращает диагностический отказ. Никакого сохранения пользовательского DOCX/XLSX/PPTX, `O_TMPFILE/linkat/fsync` в worker, никаких WORM/SMB/GUP-2/Nextcloud. CEF native `NativeSaveAsPublish` остаётся прежним обособленным **синтетическим лабораторным** кодом и не подключается к новому пути.

При неизвестной поддержке `socket.SOCK_CLOEXEC`, невозможности видеть `/proc`, отсутствии root-owned конфига, ином UID/GID, лишнем FD, недоступном IPC-каталоге или неподдерживаемом sandbox — отказ, **никаких fallback**.

## Проекты systemd и политика безопасности

В репозитории находятся **только образцы**, не устанавливаемые из текущего PR:

- `getdoc-gd31j27p-broker.blocked.service` — под `User=getdoc-lab-saveas-broker`, `Group=getdoc-lab-ipc`; `RuntimeDirectory=getdoc-gd31j27p-disposable`, mode0750, фактический `ExecStart=/usr/bin/python3 -B /opt/getdoc-lab/cef_lab/native_saveas_broker_listener.py`. Именно этот процесс создаёт/listen под broker UID.
- `getdoc-gd31j27p-worker.blocked.service` — под `User=getdoc-lab-saveas-worker`, `Group=getdoc-lab-ipc`; `ExecStart=/usr/bin/python3 -B /opt/getdoc-lab/cef_lab/native_saveas_worker_dialer.py`. Только worker процесс вызывает `connect` после запуска под своей учётной записью.
- `getdoc-gd31j27p-worker-policy.example.json` — **пример**, содержащий UID40001/40002 и IPC GID40003; это **не реальные учётные данные** и не утверждение о наличии этих пользователей на Astra. Реальную UID/GID/корневой статический policy обязан назначать независимый администратор после проверки ОС, без публикации секретов.

Оба unit имеют `RefuseManualStart=yes`, `ConditionPathExists` с намеренно не создаваемым сигналом согласования, **без `[Install]` и `WantedBy`**. Оба явно запрещают сеть (`PrivateNetwork`), ограничивают mount namespace/системные пути, привилегии, capabilities, syscalls, SUID и адреса кроме AF_UNIX. При этом `SystemCallFilter=@system-service` не проверен по конкретным syscall Python/CEF/Astra; `PrivateNetwork` и `PrivateMounts` по отдельности не удостоверяют отсутствие корпоративных данных. Сервис не становится «одобренным» от наличия файла.

CMake: `GETDOC_GD31J27P_ENABLE_POST_UID_WORKER_PUBLICATION_LAB` по умолчанию OFF и **FATAL_ERROR `GD31J27P_BLOCKED`** при любом включении; все l–o блокировки сохраняются.

## Проверки исходников (только написаны)

1. `test_native_saveas_worker_dialer.py`: exact bound JSON, защита от дубликатов/лишних полей, запрещённое `publishEnabled`, поддельный broker UID и path, root-owned/one-link config, socket 0750/0660, worker UID/GID, never-authorize denial.
2. `test_native_saveas_worker_dialer_contract.py`: post-UID `connect`, FD3, `SO_PEERCRED`, отсутствие `socketpair`, child process запуска, setuid/mount/chmod/link/rename/unlink и выпуска пользовательского документа.
3. `test_native_saveas_broker_listener.py`: раздельные UID/GID, реальный peer UID/PID/GID и неизменные признаки deny-only.
4. `test_native_saveas_broker_listener_contract.py`: только broker UID process creates `bind/listen`, не создаёт socketpair до UID, не передаёт synthetic FDs и не включает unit.

Все четыре набора зарегистрированы **до** restore в `scripts/office_acceptance.py`, добавлен тест самого плана, жёсткий `native_saveas_acceptance_gate.py` расширен до **24 fault cases и 21 ручного E2E**, где новая проверка `post-uid-broker-bind-worker-connect-fd3-peercred`.

## Оставшиеся блокеры и следующий шаг

- **Нет запущенной/развёрнутой системной службы**, созданных и проверенных раздельных UID, реальной VM, а также фактически принятых `SO_PEERCRED`/FD3. Использование `RuntimeDirectory` и exact mode0644/root policy на Astra требует полноценной независимой проверки.
- **Нет передачи actual SCM_RIGHTS в новом broker listener**: это диагностический, ограниченный отказом handshake. Для передачи worker-owned `0700` FD необходим **отдельный доверенный supervisor**, который сможет выдать разрешённые preopened FD без расширения привилегий CEF GUI. Старый `native_saveas_fd_broker.handoff_after_kernel_review` пока применяется отдельно на уровне исходного API.
- **Нет публикации в worker**. До написания worker-side snapshot/no-replace/fsync и тестирования в disposable offline VM это нельзя называть завершённым Save As. Не переключать CEF на новый путь, не использовать с user Office-файлами.

**GD-31J.27q:** определить доверенный supervisor для передачи preopened FD и реализовать **непривилегированный** standalone worker-side *synthetic* snapshot publisher, исключительно с явным OFF и независимыми O_TMPFILE/FS-ошибками; устранить неоднозначность привилегий/владения FD перед полноценным запуском стенда. Если на Astra запрещено cross-UID чтение `/proc`, нельзя ослаблять безопасность: требуется отдельный аудитируемый OS механизм.

GitHub Actions, `main`, GUP-2, WORM, SMB, Nextcloud и production документация/файлы не изменяются.
