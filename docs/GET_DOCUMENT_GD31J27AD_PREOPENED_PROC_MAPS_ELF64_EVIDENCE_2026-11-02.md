# GD-31J.27ad — read-only разбор реальных procfs maps и ELF64 по заранее открытым FD

**Дата:** 2 ноября 2026 г. **База:** Draft PR №142 / GD31J27ac. **Статус:** исходники, тесты и документация, **без выполнения** Python unittest, сборок, Astra Linux VM, systemd, root-сервисов, реальных IPC/SCM_RIGHTS, чтения корпоративных файлов и производства SaveAs.

## Для чего новый этап

GD31J27ac принимает внешний снимок `loadedElf`/`elfNeeded`/systemd/process и строго сверяет его с подписанным SBOM. Но снимок можно было составить вручную. GD31J27ad добавляет ограниченный разбор **фактических байтов** Linux `/proc/PID/maps` и ELF `PT_DYNAMIC/DT_NEEDED`, переданных через **уже открытые read-only файловые дескрипторы**. Это отдельный инструмент без `main()`, установки демона, CLI и вызовов `systemctl`, `ldd`, `readelf`, `subprocess`, `os.open` или `os.close`.

Заранее переданный FD не является доказательством происхождения! Изолированный супервизор и доверенный OS-оператор должны независимо подтвердить, что два `proc_map_fds` действительно открыты на `/proc/<broker_pid>/maps` и `/proc/<worker_pid>/maps`, а ELF FD привязан к конкретным root-owned файлам, проверенным по SHA-256 из SBOM. Эти действия **не реализованы** в данном этапе. Нет изменения UID, новых системных возможностей или обхода cross-UID procfs.

## Код

`native_saveas_proc_maps_elf_collector.py`:

- `parse_pinned_maps(data,executable,allowed_elf)`: проверяет ограниченный ASCII `/proc/<pid>/maps` (≤256 KiB, ≤2048 строк), корректность адресных диапазонов/permission/offset/device/inode, последовательный порядок, наличие закреплённого `/usr/bin/python3.X` и **точного** непустого набора ELF .so только в явно допустимых `/usr/lib/x86_64-linux-gnu` или `/usr/lib/aarch64-linux-gnu`. Файловое отображение вне allowlist, deleted/memfd, дополнительный JIT/анонимный VMA неизвестной формы, некорректный формат и отсутствие библиотеки → **отказ**. Из-за строгой политики реальные версии Astra Linux могут потребовать отдельного review новых типов mapping, но не неявного разрешения.
- `parse_elf64_needed(data,expected_machine)`: разбирает **не исполняя** байты ELF64 Little Endian для x86_64 (`EM_X86_64=62`) и AArch64 (`EM_AARCH64=183`), тип ET_EXEC/ET_DYN, не более 64 program headers, bounded `PT_LOAD` и `PT_DYNAMIC`, `DT_STRTAB`, `DT_STRSZ`, `DT_NEEDED`. По одному корректному virtual address mapping ищет ограниченную строковую таблицу и возвращает отсортированные SONAME. Неясные, отсутствующие, слишком большие или повторные таблицы, неверные SONAME, неподдерживаемая архитектура → отказ. **Не** разрешает `dlopen`, `RUNPATH` или подлинность loader.
- `_read_bounded_preopened_fd(fd,limit)`: читает **только** caller-owned FD ≥3, требует Linux `fstat` regular и `F_GETFL & O_ACCMODE == O_RDONLY`, ограничивает размер, не делает `open/seek/close`. Это ограничивает типы FD, но не удостоверяет, что FD действительно является procfs-картой, а не подложенным обычным файлом.
- `capture_preopened_once(..., approved_disposable_vm=True)`: требует ровно два разных pinned PID `broker/worker`, два разных дескриптора maps, отдельный дескриптор на каждую pinned .so, совпадающие фиксированные пути, раздельные входные read-only FD; разбирает обе карты и ELF без новых OS действий. Итог только `kernelMapFactsObserved=true` и `elfNeededFactsObserved=true`.

**Всё ещё false:** `mapsFdOriginAttested`, `processIdentityAttested`, `systemdUnitAttested`, `osEvidenceAttested`, `runtimeLibrariesAttested`, `dependencyClosureAttested`, `launchAuthorized`, `nativePublicationAuthorized`, `writePermissionGranted`, `releaseReady`, `approvedForProduction`. Проверка меток `approved_disposable_vm` — лишь блокировка сценария, не доказательство действительной VM.

## Граница относительно реального OS collector

Этот этап **не является завершённым независимым OS evidence provider**. В нём не реализованы:

- доверенное открытие procfs под нужными пространствами имён и pinned pidfd, проверка собственных UID/GID/capabilities перед чтением, root-only OS launcher или передача прав FD между отдельными процессами;
- проверка фактического inode каждого `.so` и независимого root-owned SHA до передачи;
- независимый снимок `systemctl show`/`D-Bus` со всеми drop-ins, generators, `ExecStart`, unit-file state и namespace/mount security;
- сопоставление интерпретатора, ld.so, импортируемых Python C extensions, wheels, stdlib, `dlopen`/relocations, полноценного ELF loader closure;
- фактическая политика Astra Linux МРД, эффективные systemd/cgroup ограничения и отсутствие корпоративных mount points.

Особенно важно, что **только сопоставление байтов, даже полученных из настоящих procfs FD, не является независимой аттестацией**. Внешний доверенный оператор должен зафиксировать origin, временную согласованность снимка и запрет повторного использования PID/FD. При невозможности получить cross-UID наблюдения следует отказать, а не выдавать `CAP_SYS_PTRACE` или запускать broker как root.

## Подготовленные, но НЕ исполненные тесты

- `test_native_saveas_proc_maps_elf_collector.py`: синтетические Linux maps (правильные и повреждённые), неизвестные/deleted mappings, missing ELF, встроенный ELF64 c `PT_DYNAMIC` и `DT_NEEDED`, неверная endian/class/machine, отсутствие строки или table, подмена pid/fd, reused FD, oversized input, socket/writable descriptor, сохранение владения FD.
- `test_native_saveas_proc_maps_elf_contract.py`: code-source запрет на `os.open`, `os.close`, `subprocess`, `setuid`, `ldd`, `readelf`, `systemctl`, подключения к worker/broker/CEF и release.
- `scripts/office_acceptance.py`: две pre-restore offline suite, проверка `scripts/tests/test_office_acceptance.py`. Приёмка теперь **38 fault cases / 35 обязательных manual E2E gates**: `preopened-proc-maps-elf-needed-fd-origin-failclosed` и `eurooffice-gd31j27ad-preopened-proc-maps-elf64-deny-e2e`.
- `CMakeLists.txt`: `GETDOC_GD31J27AD_ENABLE_PREOPENED_MAPS_ELF_PUBLICATION_LAB=OFF`, при ON принудительный **`GD31J27AD_BLOCKED` / FATAL_ERROR**. Все l–ac blockers сохранены.

## Следующий GD-31J.27ae

Независимый **read-only OS-level FD origin/provenance** для двух процессов и ELF files, работающий только в disposable offline Astra Linux VM, с проверкой конкретного process pidfd, inode, timestamp и kernel namespace. Отдельно потребуются systemd D-Bus effective-state evidence и операторский pinned trust anchor. Затем — реальные VM E2E и sandbox security review. До этих доказательств GD31J27q synthetic `O_TMPFILE` publisher остаётся отключённым.

**Не было:** Python/C++ тестов/сборки, OS shell/systemd/pidfd, настоящей передачи SCM_RIGHTS, публикации, GitHub Actions, merge. Основная `main`, пользовательские DOCX/XLSX/PPTX/PDF, WORM, GUP-2, Nextcloud и SMB не затронуты.
