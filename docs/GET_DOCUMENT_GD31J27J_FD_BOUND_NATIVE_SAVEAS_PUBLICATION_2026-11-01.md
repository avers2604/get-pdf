# GD-31J.27j — привязка публикации Save As к проверенному файловому дескриптору

**Дата:** 2026-11-01. **База:** Draft PR #122 — GD-31J.27i. **Ограничения:** только Linux/Wayland, одноразовая офлайн-ВМ, три синтетических DOCX/XLSX/PPTX. Реальный пользовательский Save As, SMB/WORM/GUP-2, Office-файлы пользователей и рабочие приложения не затрагиваются.

**Статус:** исходники, Python-тесты/контракты и план VM-проверки подготовлены; компиляция C++, Python/MSTest и реальные CEF/SDK/VM испытания **не выполнялись**. Утверждать устранение всех конкурентных гонок и готовность к релизу нельзя.

## Обнаруженная проблема GD-31J.27g

До этого этапа `native_saveas_durable.h` открывал приватный файл через `openat(scratchDirFd, basename, O_NOFOLLOW...)`, сверял SHA, inode и метаданные, делал `fsync(fileFd)` и `fstatat` по имени. Затем вызывал **`linkat(scratchDirFd, basename, outputDirFd, basename, 0)`**.

Если процесс с доступом к приватному каталогу заменял именно *имя* между `fstatat` и `linkat`, сам `linkat` мог публиковать другой inode. Позднейшая проверка конечного inode не позволяла назвать результат успешным, однако нежелательная ссылка уже могла появиться. Это важно даже в тестовой реализации.

## Изменения GD-31J.27j

### Только descriptor-bound создание final имени

Новый `Gd31J27G::LinkVerifiedSourceFd(sourceFd, outputFd, basename)` принимает **открытый и уже независимо проверенный fd**, а не имя временного файла:

1. Сначала вызывается `linkat(sourceFd, "", outputFd, basename, AT_EMPTY_PATH)`. На ряде Linux это требует `CAP_DAC_READ_SEARCH`; лабораторному приложению **не следует выдавать привилегии** только ради теста.
2. Если `AT_EMPTY_PATH` недоступен из-за `EPERM/EINVAL/ENOENT/EOPNOTSUPP`, допускается ровно один kernel procfs-вариант. Открывается `/proc/self/fd` через `O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC`, и **обязательно** проверяется `fstatfs(...).f_type == PROC_SUPER_MAGIC`. Затем применяется `linkat(procFdDirectory, decimal(sourceFd), outputFd, basename, AT_SYMLINK_FOLLOW)`. Это следует за файловым дескриптором *текущего процесса*, а не за именем в writable scratch. Если procfs не настоящий/недоступен — отказ.
3. При `EEXIST` у `AT_EMPTY_PATH` возврат немедленный, никакого fallback/retry или перезаписи. Никаких `renameat`, `QFile::copy`, `unlink` и path-based `linkat(scratchDirFd, basename, ...)` в публикационном переходе.
4. После создания ссылки C++ повторно сравнивает **три идентичности**: верифицированный fd, старое имя в private scratch, конечное имя в approved output (device/inode/size). Проверяет, что ровно две ссылки, и снова вычисляет SHA-256 через тот же открытый fd. `fsync(outputDirFd)` выполняется только после сверки.
5. Если имя source успели заменить, создание ссылки **не может быть направлено на подменённый inode** через этот syscall. Но если source путь не соответствует исходному FD после link, проверка отказывает. Возможно оставшееся final имя считается результатом с неопределённым исходом: запрещён автоматический `unlink`; только будущая ручная послеаварийная инвентаризация.
6. Предыдущие ограничения сохраняются: только три exact basenames, маркеры владельца scratch/output, регулярный файл, 1–32 МБ, `nlink==1` до публикации, group/world write запрещён, source SHA, no-replace, same FS, file `fsync` до link и directory `fsync` после, без пользовательских документов.
7. Старый protocol `getdoc-native-saveas-candidate-lab/1` и pinned `getdoc-native-saveas-pinned-event-lab/2` не меняют поля JSON, не получают production rights. Новый fd-link helper расположен в общей **лабораторной** политике, поэтому отдельный регрессионный запуск legacy /1 обязателен.

### Что остаётся НЕ гарантированным

- Descriptor-bound link убирает **подмену inode именно между проверкой и вызовом linkat по имени**; он не может помешать другому процессу с разрешением записи в тот же inode менять данные после проверки/линковки. Потребуется исключительный доступ к private scratch, OS isolation и реальный fault-injection E2E с конкурентным writer. `fstat`/SHA после операции лишь обнаружат часть нарушений, не делают код полностью транзакционным.
- `/proc/self/fd` — Linux-специфический механизм и зависит от реального procfs, mount namespace, ядра/политик и наблюдаемого набора разрешений. Проверка superblock снижает риск подмены fake procfs, но не является полной аттестацией mount namespace или безопасности целой ВМ.
- `fsync` не доказывает сохранность при аппаратном отключении питания без конкретной модели файловой системы/носителя. После любой ошибки post-link состояние final **неизвестно**, никакого rollback.
- Без реальной Qt/CEF сборки нельзя утверждать, что nested V1/V2 callback state корректен, что C++ компилируется с конкретной версией EuroOffice SDK или что Save As вообще доступен пользователю.

## Регрессионные проверки (код подготовлен, НЕ запускался)

`test_native_fd_bound_link_contract.py` проверяет отсутствие старого path-based `linkat` и наличие только fd-bound helper, `AT_EMPTY_PATH`, настоящего procfs, `AT_SYMLINK_FOLLOW`, проверки inode/SHA и `fsync`.

`test_native_fd_bound_link_linux.py` — только на разрешённом Linux стенде; все операции строго с файлами в собственном `tempfile.TemporaryDirectory`. Сценарии:

- сохранение через открытый fd при подмене его исходного имени другим синтетическим файлом: final должен содержать **проверенные байты**, не подставленные;
- замена исходного имени символьной ссылкой на синтетический decoy: final не должен указывать на decoy;
- существующее final имя: `FileExistsError/EEXIST`, содержимое остаётся прежним;
- отсутствие настоящего `/proc/self/fd` на Linux: строгий fail, не implicit skip.

`scripts/office_acceptance.py` получил два offline pre-restore gate, а `scripts/tests/test_office_acceptance.py` — проверки их присутствия. Ручной E2E gate `eurooffice-gd31j27j-fd-bound-link-no-path-swap-e2e` добавлен как **15-й** обязательный к `native_saveas_acceptance_gate.py`; число fault-case сценариев осталось 18, один существующий сценарий `source-name-swap-during-linkat` теперь проверяет новый контракт.

В отдельной офлайн-ВМ необходима именно **C++/CEF** fault-injection проверка, а не только Python-модель Linux syscall. Запрещены операции с реальными корпоративными данными и процессами. Все GitHub Actions и remote builds остаются выключенными.

**Следующий этап GD-31J.27k:** сверка file descriptor lifetime, атак same-UID writer, устойчивости проверки post-link после crash и Linux/Astra compatibility на актуальном pinned SDK без включения production Save As. Фактическая сборка и тесты — только после ранее согласованного разрешения на финальный стенд.
