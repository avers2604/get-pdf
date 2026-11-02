# GD-31J.27ae — необходимые наблюдения происхождения FD, PID и namespace (без аттестации)

**Дата:** 2 ноября 2026 г. **База:** Draft PR №143 / GD31J27ad. **Режим:** только исходники и подготовленные тесты, без выполнения Python unittest, C++ сборок, real Astra Linux VM, root supervisor, systemd, IPC/SCM_RIGHTS и native SaveAs.

## Что закрывает этап

GD31J27ad принимает уже открытые read-only дескрипторы двух `/proc/PID/maps` и нескольких ELF-файлов. Проверка содержимого сама по себе не гарантирует, что FD действительно относится к нужному процессу или root-owned файлу.

Добавлен **не подключённый к рабочему приложению** `native_saveas_fd_origin_namespace.py` с явным `review_fd_origins_once(...)`. Только внутри заранее одобренной disposable offline VM и уже существующего UID 0 он наблюдает:

1. **Проверка входных pins до системных вызовов.** Требуются точные роли broker и worker, разные закреплённые PID и startTicks, разные FD≥3 без алиасов, фиксированные ELF paths и SHA-256, точные namespace inode для `mnt/net/pid` каждой роли. Произвольный PID, файл за пределами списка, дублирующий FD, bool вместо PID и отсутствующий VM gate ⇒ отказ.
2. **Удержание реальных pidfd.** Ранее добавленные `pin_broker_process` и `pin_worker_process` удерживают отдельные kernel references, проверяют независимо pinned время старта и живость. При любом завершении, повторном использовании PID или недоступном procfs — отказ. Оба pidfd закрываются даже после ошибки другого этапа.
3. **Наблюдение FD targets.** `os.readlink("/proc/self/fd/<n>")` должен в точности соответствовать `/proc/<brokerPid>/maps`, `/proc/<workerPid>/maps` и каждому фиксированному allowlisted ELF pathname. Любые deleted/memfd/отличающиеся пути отказывают. Этот путь — только kernel observation, не непрерывная доказанная привязка FD к trusted actor.
4. **Namespace snapshots.** `os.stat("/proc/<pid>/ns/{mnt,net,pid}").st_ino` обеих ролей сравнивается с внешне закреплёнными операторами инодами. Все наблюдения повторяются **после** однократного чтения. Kernel namespaces, cgroup/systemd и подлинность исходных pins всё ещё требуют независимого OS audit.
5. **Файл ELF в том же FD.** `fstat` подтверждает regular, root-owned, nlink=1, без group/world-write, `fcntl(F_GETFL)` — только O_RDONLY; SHA-256 читается через ограниченный `os.pread`, не меняя позицию FD для GD31J27ad parser. Проверяются стабильность `dev/ino/size/mtime/ctime` и совпадение с операторским SHA-256. После разбора `PT_DYNAMIC/DT_NEEDED` **SHA повторяется** и сверяется заново.
6. **Прежний разбор GD31J27ad.** Полученные FD передаются через `capture_preopened_once` только на read-only разбор maps и ELF. Возвращённый результат должен содержать `kernelMapFactsObserved=true`, `elfNeededFactsObserved=true`, но не иметь `releaseReady` или иного разрешения. Только после повторной проверки FD targets, namespace, SHA и pidfd-liveness возвращаются диагностические флаги.

Параллельно устранена логическая ошибка GD31J27ad `parse_pinned_maps`: возрастающие начальные адреса двух VMA не исключают **перекрытия**; теперь проверяется `previous_end<=start<end`, с отдельным регрессионным тестом.

## Что не было сделано и не доказано

- `os.geteuid()==0` **не доказывает доверенность supervisor**: root может находиться в ином namespace, FD-путь может быть изменён bind mount, а external pins — подложены.
- Код **не открывает** `/proc/<pid>/maps` сам, не создаёт root launcher, не вызывает `systemctl`, D-Bus, shell, pidfd_getfd, ptrace, setuid и не выдаёт новых capabilities.
- Эффективная политика systemd, signing chain, mount restrictions, Astra Linux МРД, seccomp BPF, root-of-trust и отсутствие корпоративных mount points **не были подтверждены**.
- Закреплённые FD и pidfd могут находиться в разных пространственных контекстах. Будущая VM проверка должна подтвердить как procfs mount, так и PID namespace применительно к **конкретному** процессу.
- Отсутствует независимая цепочка передачи доказательств от доверенного root supervisor к broker другого UID. Все успешные наблюдения — **necessary, not sufficient**; никакой SaveAs, worker-side `O_TMPFILE`, WORM, пользовательские файлы или release не разрешены.

Отчёт содержит `kernelFdPathObserved=true`, `kernelNamespaceFactsObserved=true`, `elfRootOwnedShaObserved=true` только при совпадениях, а `fdOriginAttested`, `osEvidenceAttested`, `launchAuthorized`, `nativePublicationAuthorized`, `writePermissionGranted`, `releaseReady`, `approvedForProduction` остаются **false** при любом результате.

## Изменения тестов и блокировок — написаны, не запускались

- `test_native_saveas_fd_origin_namespace.py`: точные роли/FD/pidfd/namespace, отказы при неизвестной FD-ссылке, смене namespace, несовпадении ELF SHA до и после чтения, non-root caller, root-owned inode violations, закрытие pidfd при всех ошибках.
- `test_native_saveas_fd_origin_namespace_contract.py`: исходниковая проверка неизменных false-claims, запрет subprocess/OS open/privilege changes/production import, порядок pin→read→recheck.
- Дополнен `test_native_saveas_proc_maps_elf_collector.py` новым тестом на пересекающиеся отображения; исправлен `native_saveas_proc_maps_elf_collector.py`.
- `scripts/office_acceptance.py`: две новые pre-restore suite, регистрационный тест в `scripts/tests/test_office_acceptance.py`.
- Матрица `native_saveas_acceptance_gate.py`: **39 fault cases / 36 manual gates**, новые `pidfd-fd-target-namespace-swap-elf-sha-no-launch-authority` и `eurooffice-gd31j27ae-pidfd-fd-origin-namespace-elf-hash-e2e`.
- CMake `GETDOC_GD31J27AE_ENABLE_FD_ORIGIN_PUBLICATION_LAB=OFF`; любое ON — **`FATAL_ERROR GD31J27AE_BLOCKED`**. Все прежние l–ad запреты сохранены.

## Следующий этап GD-31J.27af

Сначала независимый OS collector/test harness для **настоящей** disposable offline Astra Linux VM: закрепление доверенного supervisor executable/systemd unit, procfs mount namespace, origin FD через реальные openat2/pidfd/UID, проверка root-owned ELF vs signed SBOM и подтверждение отсутствия корпоративных mounts. Только после лабораторных тестов и security review можно рассматривать deny-only трёхстороннее испытание. Никакого перехода к production publication.

**Не выполнялись:** Python unit tests, C++ сборки, Astra Linux VM, systemd D-Bus, real kernel pidfd/SCM_RIGHTS, публикация, GitHub Actions, merge. Основная ветка main, WORM, GUP-2, SMB, Nextcloud и пользовательские DOCX/XLSX/PPTX/PDF не менялись.
