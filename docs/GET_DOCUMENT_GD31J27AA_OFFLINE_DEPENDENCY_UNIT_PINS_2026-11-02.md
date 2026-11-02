# GD-31J.27aa — закреплённые Python/ELF зависимости и заблокированные systemd unit

**Дата:** 2 ноября 2026 г. **База:** Draft PR №139 / GD31J27z. **Режим:** source-only, без выполнения Python-тестов, Qt/CEF/EuroOffice/C++ сборок, настоящей Astra Linux VM, системных сервисов, real UID, SO_PEERPIDFD, SCM_RIGHTS и настоящих документов. Все права на SaveAs, WORM и релиз остаются false.

## Задача и граница доверия

Ранее GD31J27z сверял kernel `/proc/<pid>/exe`, один root-owned source SHA, cgroup и `cmdline`. Но root-owned source и интерпретатор могут импортировать другие Python-модули, загружать `.so`, обращаться к системным библиотекам и использовать произвольный launcher. Сравнение только двух SHA **не удостоверяет весь runtime**.

`native_saveas_dependency_unit_pins.py` — новый **отдельный, не подключённый к штатному запуску** read-only источник необходимых фактов, а не доверенный системный launcher и не осведомлённый обо всех динамических импортируемых модулях анализатор.

Модуль `parse_manifest` принимает ограниченный ASCII/JSON (≤8192 байт, одна строка, запрет повторных/лишних ключей, nonfinite), ровно:
- `schema=getdoc-gd31j27aa-root-owned-dependency-unit-pins-lab/1`;
- `mode=inspect_only`, `publishEnabled=false`, `laboratoryOnly=true`;
- точные `modules` SHA-256 четырёх фиксированных локальных файлов `native_saveas_broker_listener.py`, `native_saveas_worker_dialer.py`, `native_saveas_fd_worker.py`, `native_saveas_sandbox_observer.py`;
- `sharedObjects`: 1–16 закреплённых SHA-256 бинарных shared objects только в `/usr/lib/x86_64-linux-gnu` или `/usr/lib/aarch64-linux-gnu` с фиксированным допустимым basename `*.so[.N]`. **Это ручной allowlist, а не доказательство полноты системных libraries**;
- `units`: точные SHA-256 двух **заблокированных** policy specimens `getdoc-gd31j27aa-{broker,worker}.blocked.service`. Действительный OS systemd с подписанными unit-политиками, AppArmor/Astra МРД, seccomp BPF и согласованным cgroup **не создавался и не проверялся**.

Root-owned manifest имеет единственный разрешённый путь `/etc/getdoc-lab/gd31j27aa-dependencies.json` и **не предоставляется пользователем или через IPC**. В будущей disposable VM оператор должен независимо закрепить все настоящие SHA-256; текущий PR не содержит разрешающих реальных дигестов.

## Проверки исходников и файлов

`check_static_imports` читает Python source через AST, требует полный набор четырёх фиксированных локальных имён или разрешённых стандартных верхнеуровневых импортов и отказывает на неизвестные `import`, `from ... import`, `eval`, `exec`, `compile`, `__import__`. Никакие пользовательские путь/пакет/имя не превращаются в разрешённый local source. Это **синтаксическое наблюдение**, а не статически доказанное разрешение всех возможных динамических импортов: вложенный `getattr`, native extension, lazy imports, Python startup hooks и runtime `dlopen` требуют отдельно проверяемого OS evidence.

`_read_fixed_file` допускает только ТОЧНЫЕ четыре module paths, единый root manifest, два unit paths или строго ограниченные shared library locations. Требуется уже работающий под root операторский лабораторный процесс, но сам код **не создаёт** такого процесса; нет setuid, subprocess, сетевых вызовов, chmod/chown, mkdir/rename/delete. Все сегменты пути открываются через последовательные `O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC`, root-owned non-group/world-writable каталоги; regular inode owner root, nlink1, без group/world writable, ≤64MiB; SHA-256 и `fstat` identity проверяются до/после чтения; FD закрываются на любых ошибках. Root-owned JSON требует mode `0600`. На обычном ПК `/etc/getdoc-lab` и `/opt/getdoc-lab/bin` могут не существовать — это **нормальный отказ, а не повод устанавливать компоненты автоматически**.

`inspect_root_owned_files()` читает и сравнивает список, `inspect_pinned_digest_snapshot()` требует **точное** множество заявленных digests, `compare_observed_library_paths()` отвергает неизвестные, deleted, лишние и пропущенные `.so` из внешнего наблюдения. **Последняя функция не читает `/proc/pid/maps` сама**: snapshot должен быть получен оператором в изолированной Astra Linux VM, с документированной цепочкой доверия. Runtime-dynamic library proof не считается выполненным, пока не собран реальный полный maps/DT_NEEDED/dlopen набор.

## Заблокированная служба

Оба файла `getdoc-gd31j27aa-{broker,worker}.blocked.service` в PR являются **не установленными образцами**, содержат:
- `RefuseManualStart=yes` и `ConditionPathExists=/run/getdoc-gd31j27aa-disposable/APPROVAL_NEVER_AUTOCREATE`;
- `ExecStart=/usr/bin/false`, `NoNewPrivileges=yes`, `PrivateNetwork=yes`, `PrivateMounts=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `RestrictAddressFamilies=AF_UNIX`, `RestrictSUIDSGID=yes`;
- пустые `CapabilityBoundingSet` и `AmbientCapabilities`, разные `User=getdoc-lab-broker/worker`, общая IPC группа;
- **нет** `[Install]` и `WantedBy`. Ни один unit не является стартоспособным Python launcher. Переход от заблокированного specimen к реально запускаемому юниту потребует нового независимого security/OS review.

`verify_unit_bytes()` принимает только точный pinned hash и строгие наборы директив из двух секций Unit/Service, без дополнительных полей. Это не проверка effective systemd policy (drop-ins, manager, generators, cgroup delegation, environment, namespace setup), и не разрешение на включение unit.

## Подготовленные проверки, НЕ запускались

- `test_native_saveas_dependency_unit_pins.py`: exact manifests, unknown/duplicate keys, missing module/extra import, опасный dynamic import, hashes, неразрешённые пути `/etc`/`/tmp`, security-relaxed unit, unknown/missing `.so`, deny-only snapshots.
- `test_native_saveas_dependency_unit_pins_contract.py`: source invariants: root-only path traversal, no dynamic runtime wiring, noninstallable systemd units, CMake hard stop.
- `scripts/office_acceptance.py`: два pre-restore offline шага, `scripts/tests/test_office_acceptance.py`: их наличие, порядок и неизменный `releaseReady=false`.
- `native_saveas_acceptance_gate.py`: **35 fault cases / 32 mandatory manual E2E gates**, новые `pinned-python-elf-so-unit-import-closure-no-unknown-loads` и `eurooffice-gd31j27aa-dependency-elf-unit-pins-review-e2e`.
- CMake `GETDOC_GD31J27AA_ENABLE_DEPENDENCY_UNIT_PUBLICATION_LAB=OFF`; включение вызывает безусловный **`FATAL_ERROR GD31J27AA_BLOCKED`**. Все предыдущие l–z барьеры сохранены.

## Оставшиеся необходимые шаги

**GD-31J.27ab:** независимый источник подписанных каталогов/Python wheels + список transitive imports и бинарных расширений, эффективная systemd policy, Linux loader `DT_NEEDED` и реальные maps под отдельными UID и `pidfd`. Закрыть вопрос того, что root manifest и его SHA независимо закреплены доверенным оператором, а не сгенерированы тем же проверяемым процессом. Даже это не разрешит production SaveAs без настоящей Astra Linux VM, воспроизводимой локальной сборки и review.

**Не изменены:** `main`, CEF/worker/broker production CLI, WORM, GUP-2, Nextcloud, SMB, пользовательские OOXML/PDF, GitHub Actions. Отсутствуют merge, реальная публикация, проверка installed systemd и любые новые привилегии.
