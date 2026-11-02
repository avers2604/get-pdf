# GD-31J.27q — собственная синтетическая публикация в модуле worker (без включения IPC)

**Дата:** 2 ноября 2026 года. **База:** Draft PR №129 (GD-31J.27p).  
**Статус:** новая реализация функции и код регрессионных тестов подготовлены в репозитории; C++, Python unittest/MSTest, отдельная Linux/Astra VM, EuroOffice SDK, systemd, fault injection и фактическое сохранение **не запускались**. Этот код **не подключён** к CEF, broker listener и `native_saveas_fd_worker.serve_one`. Внутренний лабораторный вызов с явным опциональным флагом не означает OS авторизацию.

## Что добавлено

`probes/eurooffice-desktop/cef_lab/native_saveas_worker_snapshot.py` содержит самостоятельную Python-функцию `publish_disposable_fixture(handles, request, worker_uid, operator_vm_gate=None)`. Это отдельный от C++ CEF алгоритм **только для вручную разрешённой одноразовой офлайн-ВМ**, без public CLI, обработчика нового IPC `publish` или автоматического запуска. Чужие DOCX/XLSX/PPTX, SMB, WORM, Nextcloud и GUP-2 в неё не передаются.

Алгоритм предназначен для уже проверенных **трёх открытых дескрипторов**: source на одном из трёх заранее фиксированных синтетических OOXML имён; private scratch dirFd и output dirFd c владельцем процесса и правами 0700 на той же файловой системе. Предполагаемый доверенный внешний supervisor ещё **не реализован**. Сейчас `native_saveas_fd_broker.handoff_after_kernel_review` имеет отдельный диагностический API для `SCM_RIGHTS`, а `native_saveas_broker_listener` GD31J27p выполняет исключительно post-UID handshake без передачи FD; эти части автоматически не соединены.

### Последовательность копирования и публикации

1. Необходимо внутреннее ручное лабораторное включение `VM_TEST_ONLY_EXPLICIT_GATE`. Это **проверка намерения вызывающего кода**, а НЕ подлинное разрешение от ОС или криптографическая аттестация. Любой, кто может произвольно запускать Python в той же учётной записи, может задать маркер; поэтому module **не должен попадать в сетевой или пользовательский процесс**.
2. Проверка `worker.parse_request`: только `operation=inspect_only` и три exact fixture/final пары, ограничения JSON и SHA-256. `worker.inspect_fds` отдельно контролирует `O_RDONLY/CLOEXEC`, владельцев и режимы двух каталогов, exact owner markers, отсутствие уже занятого final имени, размер 1–32 МиБ, digest source. Дополнительно `os.stat(fixtureName, dir_fd=scratchFd, follow_symlinks=False)` обязан совпадать по device+inode с source FD, принадлежать worker UID и быть regular file с одной ссылкой. Это исключает попытку передать FD постороннего файла, даже с самосогласованным хешем. **Хеш, полученный из самого запроса, не является доверенной авторизацией; его должен закреплять независимый supervisor**.
3. Сначала `fsync(sourceFd)`; после этого создаётся **отдельный безымянный** inode через `openat(scratchFd, ".", O_TMPFILE|O_RDWR|O_CLOEXEC, 0600)`. При отсутствии O_TMPFILE или поддерживаемой FS — **отказ**, никакого `rename`, `QFile::copy`, записи именованного временного файла или hardlink исходного SDK inode.
4. Через `os.pread` с ограничением 32 МиБ и корректными частичными `os.write` в безымянный inode копируются байты, одновременно считается SHA-256. После повторной проверки source metadata `dev/ino/size/mtime/ctime` и полного snapshot размера выполняется `fsync(writer)`, `fchmod(writer,0400)`, повторный `fsync(writer)`.
5. Только после проверки **настоящего procfs** `fstatfs(/proc/self/fd).f_type == PROC_SUPER_MAGIC` открывается новый дескриптор **O_RDONLY** к тому же anonymous inode. Исходный записывающий FD закрывается **до** публикации final имени. Это отделяет опубликованную копию от дальнейших записей в файл SDK.
6. Выполняется Linux `linkat` через `os.link(str(readerFd), fixedFinalBasename, src_dir_fd=realProcFd, dst_dir_fd=outputDirFd, follow_symlinks=True)`. Источником ссылки является **уже открытый anonymous inode**, а не изменяемое имя scratch файла. `EEXIST` и ошибки процФС/capabilities — отказ без пути обхода. Не применяется автоматический retry или overwrite.
7. После создания ссылки проверяются inode открытого reader и final directory entry, ровно одна hardlink, права 0400, размер, SHA-256, затем `fsync(outputDirFd)`. После любого сбоя после `linkat` исход операции **неизвестен**: автоматического удаления final НЕТ, даже если `fsync` вернул ошибку. Только отдельная ручная инвентаризация и независимая проверка.
8. Даже при ожидаемом результате функция возвращает `state=synthetic-link-created-not-approved`, `reviewRequired=true`, `nativePublicationAuthorized=false`, `nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `approvedForProduction=false`, `releaseReady=false`, `writePermissionGranted=false`.

### Остаточные риски

- **Никакой IPC-публикации нет.** Worker `serve_one` по-прежнему умеет `inspect_only` и завершает обработку с отказом. Внешний системный supervisor пока не выдаёт в worker доверенные preopened FD, не закрепляет source SHA отдельно и не применяет проверенную файловую, namespace, seccomp или Astra МРД политику.
- **Snapshot не является WORM.** Тот же UID или процесс с доступом к `/proc/<pid>/fd` либо владельцу конечного файла может попытаться изменить или заменить его. Режим 0400 и закрытие исходного writable FD не гарантируют неизменяемость от любых локальных угроз. Нужны реальные OS-права и независимая atomicity/durability проверка.
- `ctypes`-вызов Linux `fstatfs` использует Linux ABI; `O_TMPFILE` и procfd-`linkat` зависят от версии ядра, выбранной FS, mount namespace и прав процесса. Проверить именно pinned Astra Linux/FS. Если недоступно — отказ, никаких дополнительных capabilities.
- `fsync` и совпадение SHA не доказывают аппаратную сохранность при внезапном выключении питания, гарантию неизменности содержимого в течение всей гонки или документную совместимость Microsoft Office. Требуются E2E и ручные проверки.
- `inspect_fds` и источник в scratch имеют свои требуемые markers/UID; текущие системные шаблоны GD31J27p не создают worker-owned каталоги и не обеспечивают FD roles. Не создавать их автоматически в CEF.

## Подготовленные проверки (НИЧЕГО НЕ ВЫПОЛНЕНО)

- `test_native_saveas_worker_snapshot.py`: только под **не-root Linux** и в `tempfile.TemporaryDirectory` — запрет без явного лабораторного маркера; happy-path synthetic separate inode + SHA + mode0400 + nlink1; последующая мутация SDK source не меняет published bytes; `EEXIST` сохраняет содержимое; wrong SHA/owner marker, подмена source именем-символической ссылкой и невозможность `O_TMPFILE` не создают final; симулированный отказ directory `fsync` **не удаляет** появившийся final.
- `test_native_saveas_worker_snapshot_contract.py`: source-only инварианты `O_TMPFILE`, `fstatfs(PROC_SUPER_MAGIC)`, `fsync`, closed writer до `linkat`, отсутствие автоматического unlink и публикации через SDK source inode. Гарантирует, что worker/CEF/broker **не импортируют** и **не вызывают** новую функцию.
- `scripts/office_acceptance.py` регистрирует эти два suite **до** restore; `scripts/tests/test_office_acceptance.py` проверяет их наличие. Строгий `native_saveas_acceptance_gate.py` дополнен 25-м случаем `worker-anonymous-snapshot-crash-fsync-linkat-unknown-outcome` и 22-м обязательным ручным E2E `eurooffice-gd31j27q-worker-anonymous-snapshot-fsync-linkat-crash-e2e`.

Отдельная опция CMake `GETDOC_GD31J27Q_ENABLE_ANONYMOUS_WORKER_PUBLICATION_LAB=OFF`; попытка включения всегда завершится `GD31J27Q_BLOCKED` / `FATAL_ERROR`. Все ограничения GD31J27l–p остаются.

## Что необходимо перед включением настоящей публикации

1. Доверенный внешний supervisor, который **на реальной disposable VM** создаёт раздельные непривилегированные UID и разрешённые scratch/output directories, выдаёт корректные preopened FD через проверенный post-UID `SO_PEERCRED` и подтверждает независимые fixture hashes.
2. Реальные Linux/Astra VM tests: `O_TMPFILE`, procfd-linkat, mount/UID isolation, события перезаписи, отсутствующая/меняющаяся directory entry, `EEXIST`, оба `fsync`, kill до/после link, fd close и orphan manual review.
3. Только после независимого аудита и отдельного решения — новый ограниченный IPC протокол `synthetic_publish`, перенос старого C++ CEF-триггера в worker, исключение старого in-process пути. До этого функция остаётся внутренней демонстрацией алгоритма, а **не работающим сохранением**.

**Следующий этап GD-31J.27r:** создание доверенного одноразового FD-provisioning/SCM_RIGHTS пути на disposable Linux/Astra VM и независимых evidence-рецептов, без подключения реальных документов и без автоматического разрешения production.

Никаких изменений main, GitHub Actions, WORM/SMB/GUP-2/Nextcloud или пользовательских файлов.
