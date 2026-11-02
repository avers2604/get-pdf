# GD-31J.27af — подготовка фиксированных read-only FD для disposable Astra Linux VM

**Дата:** 2 ноября 2026 г. **База:** Draft PR №144 / GD31J27ae. **Статус:** код и отложенные mock/source тесты подготовлены. **Реальные Python unittest, сборка, Astra Linux VM, systemd, отдельные UID, pidfd, procfs и SCM_RIGHTS не запускались.** Производственная публикация и пользовательские документы не используются.

## Что изменено

В GD31J27ad/ae подготовлены строгие анализаторы Linux `/proc/PID/maps`, ELF64 `DT_NEEDED`, pinned PID/startTicks/namespace и root-owned SHA, но FD должны были предоставляться извне. Новый **`native_saveas_disposable_vm_preopen.py`** добавляет **один явно вызываемый read-only способ открытия заранее разрешённых источников**:

- Ровно **две** роли `broker` и `worker`, отдельные закреплённые PID и ненулевые startTicks; `mnt/net/pid` namespace inode для каждой роли, фиксированные `/usr/bin/python3.X` и SHA-256 разрешённых ELF. Имена ELF берутся только из конечного allowlist GD31J27ae. Нет произвольного пути от документа, CEF, пользователя или сети.
- Проверка `approved_disposable_vm=True` и точного ручного подтверждения `OFFLINE_DISPOSABLE_VM_READONLY_DIAGNOSTIC_ONLY` происходит **до обращения к UID или открытию FD**. Подтверждение — **операторская отметка**, не независимая аттестация VM или сети.
- Уже запущенный Linux UID0 обязателен, но **не повышается программой**. Два процесса сначала закрепляются через `pidfd.pin_broker_process/pin_worker_process` со сверкой внешних startTicks. Только затем открываются их `/proc/<PID>/maps`.
- `_open_fixed_proc_map` использует `O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK` и последовательное открытие `/ → proc → <PID> → maps` через dirfd. Каталог PID может принадлежать самому непривилегированному процессу, но не должен быть writable group/world. Не используется `ptrace`, `pidfd_getfd`, `setuid`, shell, сеть или чтение `/proc/<PID>/mem`.
- `_open_fixed_elf` проходит `/ → usr → lib → <architecture> → <fixed SONAME>` по descriptor-relative компонентам с запретом symlink, group/world-write и ненадлежащего владельца. ELF-file должен быть root-owned, regular, с `nlink=1`. **Реальные дистрибутивные symlink на .so намеренно дают отказ** до утверждения безопасной альтернативы, никаких молчаливых fallback.
- Полученные FD передаются только в **`native_saveas_fd_origin_namespace.review_fd_origins_once`**, где выполняются повторная проверка отдельных pidfd, точных kernel FD links, inode пространств имён, read-only ELF, двойной SHA-256 и bounded maps/DT_NEEDED. Результат признаётся лишь если все observation flags истинны, а все authority flags ложны.
- Все root FD (открытые файлы и промежуточные каталоги) закрываются на любом пути, даже при исключении. Все pidfd после завершения тоже закрываются. Нет повторной попытки, перезапуска процесса, socket creation, передачи SCM_RIGHTS или сохранения данных на диск.

## Обязательная внешняя процедура перед *будущим* реальным вызовом

1. **Создать и документировать отдельную disposable offline Astra Linux VM** с отключённой корпоративной сетью и без SMB/Nextcloud/домашних директорий/других бизнес-mount. Независимый оператор проверяет mount namespace, сетевые интерфейсы и исходный VM image. Этот PR сам не проверяет/создаёт VM.
2. Установить только лабораторные synthetic fixtures и отдельно подтверждённые подписанные зависимости, не использовать реальные DOCX/XLSX/PPTX/PDF. Установить toolchain/runtime, проверить SHA всех пакетов. **В данном PR ни одна служба не устанавливается**.
3. Убедиться, что Astra Linux ядро предоставляет `pidfd_open`, необходимые `/proc/<PID>/maps` и `/proc/<PID>/ns/*`, а закреплённые UID/namespace/ELF соответствуют политике. Невозможность кросс-UID чтения означает **отказ**, а не автоматический `CAP_SYS_PTRACE` или понижение изоляции.
4. Независимо закрепить `expected_pids/startTicks/namespaces/elf_sha256/expected_needed` из утверждённого доверенного источника. Само поле `operator_ack` не является root-of-trust и не может служить решением о публикации.
5. **Отдельно разработать и проверить** изолированный OS launcher для synthetic supervisor/broker/worker, эффективную systemd policy, root-owned provenance, seccomp/Astra МРД, отсутствие бизнес-монтов, provenance самого `/proc` mount и гарантии невозможности смены mount namespace во время наблюдения.
6. Только после этого в одноразовой VM можно *вручную* выполнять тестовые вызовы. Ни позитивный результат, ни чистое завершение collector не должны делать SaveAs доступным.

## Что НЕ доказано

Даже если `fixedReadOnlyDescriptorsOpened=true`, остаются **false**:

`externalOperatorApprovalAttested`, `trustedVmIsolationAttested`, `fdOriginAttested`, `processIdentityAttested`, `osEvidenceAttested`, `effectiveSystemdAttested`, `runtimeLibrariesAttested`, `launchAuthorized`, `nativePublicationAuthorized`, `writePermissionGranted`, `releaseReady`, `approvedForProduction`.

Root UID + `O_NOFOLLOW` + readlink procfd + pinned pidfd не доказывают: что это доверенное ядро/правильный `procfs` mount, что не существует bind-mount подмены, что unit и binary origin проверены и что корпоративные файлы отсутствуют. Неизвестные библиотеки/JIT/memfd, symlink ELF, отсутствие cross-UID прав или нетипичные mappings приводят к **fail-closed**.

## Подготовленные тесты и блокировки — НЕ выполнялись

- `test_native_saveas_disposable_vm_preopen.py`: только mock fixtures. Проверяет отказ до OS доступа при неверных pins, double PID, bad UID, alias FD, symlink, неверном sha, отказном ответе reviewer, unwinding частично открытых FD и pidfd; отдельная проверка точного dirfd-пути и `O_NOFOLLOW`.
- `test_native_saveas_disposable_vm_preopen_contract.py`: source-only запрет subprocess, setuid, file mutations, production wiring, lifecycle order и неизменные denial flags.
- `scripts/office_acceptance.py`: две новые pre-restore suite, `scripts/tests/test_office_acceptance.py` проверяет их присутствие.
- Матрица `native_saveas_acceptance_gate.py`: **40 fault cases / 37 mandatory manual E2E gates**; новые `disposable-vm-proc-fixed-preopen-path-symlink-fd-cleanup-deny` и `eurooffice-gd31j27af-real-fixed-fd-proc-elf-readonly-e2e`.
- CMake `GETDOC_GD31J27AF_ENABLE_VM_PREOPEN_PUBLICATION_LAB=OFF`, при ON всегда **`GD31J27AF_BLOCKED` / FATAL_ERROR**. Предыдущие l–ae blockers оставлены.

**GD31J27ag следующий:** подготовить настоящий **непроизводственный offline launcher/staged manual VM harness**, независимое получение эффективной systemd/D-Bus и mount/MAC/namespace evidence, только synthetic inputs и неизменно denied worker. До независимых E2E/регрессий/безопасности никакой production SaveAs.

**Не изменены:** main, GitHub Actions, реальные пользовательские документы, WORM, GUP-2, Nextcloud, SMB. Нет запуска тестов, сборок, systemd, Astra Linux VM или реального PDF/OOXML SaveAs.
