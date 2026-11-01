# GD-31J.27n — наблюдение Linux sandbox, заблокированный systemd-профиль и граница публикации

**Дата:** 1 ноября 2026 года. **База:** Draft PR №126 (GD-31J.27m). **Режим:** offline, disposable Linux/Astra/Wayland VM, только синтетические DOCX/XLSX/PPTX.

**Фактический статус:** подготовлены изменения исходников и отложенные тесты; настоящие сборки, Python unit/MSTest, CEF/Qt/EuroOffice SDK, systemd, Astra Linux VM и fault-injection **не выполнялись**. Worker остаётся **deny-only**, broker — **no-authority handoff**, а штатная лабораторная публикация всё ещё находится в CEF-процессе. Никакого production Save As.

## Что добавлено

### 1. Наблюдатель фактов ядра

`probes/eurooffice-desktop/cef_lab/native_saveas_sandbox_observer.py` выполняет только чтение Linux `/proc`. `inspect_current(peer_pid,peer_uid)` предназначен для вызова **из уже запущенного внешним OS-supervisor worker**, только после получения peer PID/UID через `SO_PEERCRED`.

Сведения не принимаются из JSON-запроса рабочего документа. Проверяются:

- Четыре поля `Uid`: real/effective/saved/fs UID строго равны ожидаемому non-root worker UID; `Gid` — единый непривилегированный GID.
- `NoNewPrivs=1`, `Seccomp=2`, `Seccomp_filters>=1`, `TracerPid=0`; `CapInh`, `CapPrm`, `CapEff`, `CapBnd`, `CapAmb` должны быть обнулены.
- Различные inode `mnt` и `net` namespace между worker и connected peer из `SO_PEERCRED`. Отсутствие доступа к `/proc/<peer_pid>/ns` → отказ.
- Ограниченный разбор `/proc/self/mountinfo` со строгим форматом и лимитом 512 записей / 256 КиБ; известные сетевые ФС `cifs`, `smb3`, `nfs`, `nfs4`, `9p`, `davfs`, `ceph`, `glusterfs`, `lustre`, `afs`, `coda` и весь `fuse.*` → отказ.
- Неизвестные, повторные, неполные или чрезмерно большие поля/ответы приводят к fail-closed, **без печати секретных путей**.

Даже при успешной проверке результат — `reviewState=requires-manual-review`. Поля `kernelIsolationAttested`, `corporateMountsAbsentAttested`, `seccompFilterSemanticsAttested`, `inheritedFdIsolationAttested`, `brokerLaunchAuthorized`, `nativePublicationAuthorized`, `releaseReady`, `approvedForProduction`, `writePermissionGranted` всегда **false**.

**Причина:** `Seccomp=2` не раскрывает фильтр BPF и список разрешённых системных вызовов, mountinfo не доказывает отсутствие корпоративных данных на локальном диске или иных путях, namespace inode не удостоверяет все правила изоляции; другие файлы `/proc` или унаследованные FD тоже требуют отдельной проверки.

### 2. Подключение к deny-only worker

`native_saveas_fd_worker.py` теперь вызывает `kernel_observer.inspect_current(peer_pid=pid, peer_uid=peer_uid)` **после** проверки `SO_PEERCRED` и старых базовых ограничений, но **до** `recvmsg + SCM_RIGHTS`, чтения маркеров или исходных synthetic FD. Ошибка приводит к прежнему диагностическому отказу и завершению с кодом 2. Сохраняются `inspect_only`, запрет записи, отсутствие пользовательских файлов и реальные production flags=false.

### 3. Заблокированный образец systemd

`probes/eurooffice-desktop/cef_lab/getdoc-gd31j27n-worker.blocked.service` — **намеренно неработоспособный образец** будущей политики, а **не** установщик/launcher. Он не имеет `WantedBy`, содержит `RefuseManualStart=yes`, обязательное никогда автоматически не создаваемое `ConditionPathExists` и **`ExecStart=/usr/bin/false`**. Он не запускает Python, не создаёт UID, не открывает socket FD3, не монтирует пользовательские каталоги.

В образце перечислены только будущие требования: `PrivateNetwork`, `PrivateMounts`, `ProtectSystem=strict`, `ProtectHome`, `NoNewPrivileges`, нулевые capabilities, `RestrictAddressFamilies=AF_UNIX`, `SystemCallFilter`, `UMask=0077`. Их применимость к конкретной Astra Linux/версии systemd **не проверена**. Не копировать образец в системный каталог и не пытаться его включать.

Отдельная опция CMake `GETDOC_GD31J27N_ENABLE_SANDBOX_WORKER_PUBLICATION_LAB` имеет значение **OFF**, а при попытке включения вызывает `GD31J27N_BLOCKED` / `FATAL_ERROR`. Старые выключенные GD31J27l/m опции сохранены.

## Подготовленные, но НЕ запускавшиеся тесты

- `test_native_saveas_sandbox_observer.py`: синтетические `/proc/self/status` и `mountinfo`, неверный UID/namespace, отсутствие `NoNewPrivs` или seccomp, `CapEff/CapPrm/CapBnd/CapAmb`, `TracerPid`, сетевые и FUSE mounts, недостаток данных; даже на «идеальной» фикстуре result никогда не разрешает publication.
- `test_native_saveas_sandbox_observer_contract.py`: source contract проверяет, что наблюдатель вызывается **до** `_receive_exact_three(sock)`, что он не способен создавать процессы/файлы и что systemd specimen точно не запускается.
- `scripts/office_acceptance.py`: два offline pre-restore suite; `scripts/tests/test_office_acceptance.py` проверяет их регистрацию.
- `native_saveas_acceptance_gate.py`: 22-й fault case `worker-live-proc-status-namespace-mountinfo-caps-deny`, 19-й обязательный ручной E2E `eurooffice-gd31j27n-live-linux-kernel-sandbox-observer-e2e`. Даже все `passed` не означают разрешения на релиз.

## Что требуется для настоящего sandbox и переноса публикации

1. **Внешний системный supervisor**, независимо от Qt/CEF, должен безопасно создать UID worker, приватные каталоги и сокетное соединение, передавать строго три одобренных FD. Обычный GUI/broker с другим UID **не должен** открывать чужие `0700` каталоги сам.
2. Supervisor обязан применять проверенный конкретный BPF/seccomp syscall allowlist и правила filesystem/mount namespace. `SystemCallFilter=@system-service` в образце — идея для аудита, **не** доказанный список необходимых syscalls. Администратор обязан убедиться, что доступ к корпоративной сети/файлам/секретам действительно исключён.
3. Нужно фактически наблюдать `SO_PEERCRED` после создания сокета и смены UID, а не считать credentials socketpair, созданной **до** `setuid`, действительными автоматически.
4. **Перенос `O_TMPFILE + fd-bound linkat + fsync` в worker НЕ выполнен.** Текущий C++ `NativeSaveAsPublish` остаётся отдельно включаемой экспериментальной CEF-функцией. До проверки отдельного UID/VM/fault-injection нельзя убирать защитные блокировки или признавать Save As пользовательским.
5. Нужны реальные регрессионные тесты C++/Python, Qt/CEF/EuroOffice, время жизни FD/процессов, ошибки `fsync`, `SIGKILL`, `EEXIST`, изменённые mounts и невозможность чтения `/proc` при усилении sandbox.

**Следующий GD-31J.27o:** внешние схемы выпуска worker под отдельной учётной записью с безопасной передачей preopened FD, независимый аудит реального OS-профиля и **после этого** прототип worker-side публикации, остающийся синтетическим и OFF до VM приемки.

Производственные DOCX/XLSX/PPTX, PDF, WORM, GUP-2, Nextcloud, SMB, GitHub Actions и `main` не меняются.
