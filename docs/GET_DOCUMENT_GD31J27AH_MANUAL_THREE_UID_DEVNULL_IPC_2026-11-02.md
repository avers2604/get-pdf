# GD-31J.27ah — ручное трёхпроцессное IPC-испытание на disposable Astra Linux VM

**Статус:** подготовлена реализация, **НЕ запускалась**. **База:** Draft PR №146 / GD31J27ag. Только offline/disposable VM, без пользовательских документов, без подключения к работающему GET-system, без WORM/SMB/Nextcloud и без production SaveAs.

## Что нового и зачем

До этого GD31J27ag умел только проверять **синтетическую запись** событий supervisor → broker → worker. Реальные отдельные UID, Unix `SO_PEERCRED`, socket peer process и `SCM_RIGHTS` там не задействовались. Сейчас добавлен **`native_saveas_manual_triparty_devnull.py`** — код, который при отдельном ручном запуске трёх его ролей может провести **настоящую передачу IPC-дескрипторов**, не открывая ни одного документа.

**Это именно транспортный smoke test, а НЕ end-to-end штатных `RootSendOnce`, `bridge_inspect_only_once`, `native_saveas_fd_worker.serve_one` и не доказательство безопасности sandbox.** Он не создаёт нужные отдельные процессы автоматически, не управляет systemd, не делает setuid/setgid, не устанавливает зависимости, не создаёт каталоги, не выполняет файловую публикацию и не выдаёт никакой аттестации.

## Неизменяемые условия запуска

1. **Только самостоятельно подготовленная оператором одноразовая offline Astra Linux VM**; независимо доказаны отсутствие корпоративных mount points, сетей, путей к документам и доступа к рабочим данным. `GETDOC_GD31J27AH_VM=OFFLINE_DISPOSABLE_NO_DOCS` — ручная отметка, **не криптографическое доказательство** изоляции.
2. Три **разных реально запущенных процесса** в отдельных терминалах/сессиях, а не subprocess одного launcher. Фиксированные учётные данные: supervisor `UID=0/GID=0`, broker `UID=44001/GID=44003`, worker `UID=44002/GID=44003`; real/effective UID и GID должны полностью совпадать. Утилита **не создаёт** пользователей, группу и не переключает привилегии.
3. Оператор отдельно создаёт и утверждает точные каталоги на **disposable /run**, без символических ссылок:
   - `/run/getdoc-gd31j27ah-disposable`: root:root, mode `0755`;
   - `.../broker`: owner 44001, group 44003, mode `0710`;
   - `.../worker`: owner 44002, group 44003, mode `0710`.
4. Не должно существовать socket paths `broker/root.sock` и `worker/worker.sock` до попытки. Код **никогда не удаляет** socket path после исполнения: если путь сохранился, следующая попытка отказывается стартовать; оператор удаляет всю VM, а не запускает автоматический cleanup.
5. Оператор независимо размещает сверенную копию исходного файла `native_saveas_manual_triparty_devnull.py` в закрытой VM по пути `/opt/getdoc-lab/bin`; данный PR **не устанавливает** файл и не настраивает систему. Весь Python runtime и OS image должны быть проверены внешней процедурой. Только stdlib, Linux AF_UNIX SOCK_SEQPACKET/SCM_RIGHTS/SO_PEERCRED/MSG_CMSG_CLOEXEC.

### Ручные команды после отдельного одобрения VM

Запускать последовательно по роли, но **не ожидать завершения каждой команды прежде запуска следующей**: каждая роль ждёт соединения не больше 30 секунд. Ни один из примеров **не создаёт аккаунты и не меняет UID**. Оператор должен заранее открыть три сессии с указанными реальными учётными данными.

**Терминал 1, worker (UID44002, GID44003):**
```bash
GETDOC_GD31J27AH_VM=OFFLINE_DISPOSABLE_NO_DOCS /usr/bin/python3 -E -s -S /opt/getdoc-lab/bin/native_saveas_manual_triparty_devnull.py worker I_APPROVE_DISPOSABLE_VM_NO_DOCUMENTS_DENIAL_ONLY
```

**Терминал 2, broker (UID44001, GID44003):**
```bash
GETDOC_GD31J27AH_VM=OFFLINE_DISPOSABLE_NO_DOCS /usr/bin/python3 -E -s -S /opt/getdoc-lab/bin/native_saveas_manual_triparty_devnull.py broker I_APPROVE_DISPOSABLE_VM_NO_DOCUMENTS_DENIAL_ONLY
```

**Терминал 3, supervisor (UID0, GID0):**
```bash
GETDOC_GD31J27AH_VM=OFFLINE_DISPOSABLE_NO_DOCS /usr/bin/python3 -E -s -S /opt/getdoc-lab/bin/native_saveas_manual_triparty_devnull.py supervisor I_APPROVE_DISPOSABLE_VM_NO_DOCUMENTS_DENIAL_ONLY
```

Ожидается **только** `GD31J27AH_DENIAL_OBSERVED_NOT_ATTESTED` у supervisor, если он действительно получил точный байтовый `DENIED`. **Все три процесса выходят с кодом 2**, даже если наблюдён обмен. Ошибка/timeout/нарушение политики также даёт 2, но **без** этой отметки на supervisor. Эта отметка является только наблюдением, **НЕ аттестацией безопасности и НЕ разрешением на SaveAs**.

## Что действительно передаётся

- Supervisor подключается к `broker/root.sock`, проверяет `SO_PEERCRED` broker: UID44001/GID44003, открывает **ровно три** `/dev/null` с `O_RDONLY|O_NOFOLLOW|O_CLOEXEC`, проверяет character device + `st_rdev` и `F_GETFL`.
- Один `sendmsg(SCM_RIGHTS)` отправляет строго `GETDOC_GD31J27AH_THREE_DEVNULL_FDS_INSPECT_ONLY\n` и три дескриптора. Частичный send => без повторов, отказ. Нет доступа к document paths, их нельзя передать в качестве аргумента.
- Broker получает **ровно три** FD через `recvmsg(MSG_CMSG_CLOEXEC)` и проверяет, что все они read-only `/dev/null`. Затем подключается только к `worker/worker.sock`, проверяет `SO_PEERCRED` worker UID44002/GID44003, делает такой же одноразовый `sendmsg`.
- Worker проверяет `SO_PEERCRED` broker, payload, тип, права и количество FD, а затем возвращает **только** `{"state":"denied",...,"nativePublicationAuthorized":false,"releaseReady":false,"writePermissionGranted":false}`.
- Broker возвращает этот же **побайтово** подтверждённый отказ supervisor. Все принятые и открытые FD закрываются при нормальном ходе и отказах; не передаются ни обычные файловые дескрипторы, ни каталоги пользователя, ни синтетические OOXML/PDF.

## Что это НЕ проверяет

- Реальный `RootSendOnce`, перенос worker-owned синтетических fixture FD через GD31J27s bridge, nonce FD3, отдельный GUP-2, подписи EuroOffice, CEF, реальную запись `O_TMPFILE`, WORM или OOXML.
- Отсутствие корпоративных mount points, защищённость самой VM/ядра, подлинность ручных флагов, systemd effective policy, seccomp BPF, Astra MAC, полноту подписанного SBOM. Каждый пункт требует независимого лабораторного review.
- Долговременную защиту от перезапуска/повтора при новом disposable окружении; endpoint sockets остаются на месте, **не удаляются** приложением.
- Надёжное различение всех сбоев и успешного denial через exit status; оба варианта дают код `2` и никакой положительной authority.

## Изменённые тесты и правила приёмки

`test_native_saveas_manual_triparty_devnull.py`: mock-only тесты на обязательные идентичности, ручной gate до запуска, `SO_PEERCRED`, `SCM_RIGHTS` ровно на 3 `/dev/null`, `O_RDONLY`, отказ при unsupported `MSG_CMSG_CLOEXEC`, полном закрытии FD при неверном пакете, отсутствии retry после short send, запрет stale/unowned runtime dirs и неизменную denial-response.

`test_native_saveas_manual_triparty_contract.py`: source-only запрет любых subprocess/fork/setuid, создания пользователей, network sockets, произвольных document paths и production wiring. Оба набора в `scripts/office_acceptance.py` до restore, registration в `scripts/tests/test_office_acceptance.py`.

Новая матрица **42 fault cases / 39 mandatory manual E2E gates**:
- `manual-triparty-devnull-uid-peercred-scm-rights-failure-deny`;
- `eurooffice-gd31j27ah-distinct-uid-devnull-scm-rights-manual-e2e`.

CMake `GETDOC_GD31J27AH_ENABLE_MANUAL_IPC_PUBLICATION_LAB=OFF`; при ON всегда **`FATAL_ERROR GD31J27AH_BLOCKED`**. Предыдущие l–ag блокировки не ослаблены.

**Никаких реальных запусков, тестов или сборок в этой работе не было.** Следующий шаг — фактически подготовить одноразовую VM (вне рабочего контура), выполнить написанные unit tests, проверить реальный smoke test под разными UID, исправить выявленные проблемы. Только потом — отдельно рассмотренный реальный E2E существующих supervisor/bridge/worker. Main, GitHub Actions, пользовательские файлы, WORM, GUP-2 и Nextcloud не изменялись.
