# GD-31J.27m — граница внешнего broker: однократная передача FD и проверяемая модель sandbox

**Дата:** 1 ноября 2026 года. **База:** GD-31J.27l / Draft PR №125.  
**Статус:** код, контракт IPC, проект профиля и тестовые сценарии подготовлены; ни сборка, ни Python/MSTest, ни реальная Linux/Astra VM, ни EuroOffice/CEF/SDK не запускались. Основная ветка `main` не изменялась.

## Точное состояние разработки

В существующем лабораторном CEF-хосте `NativeSaveAsPublish` всё ещё вызывается **внутри CEF-процесса** для синтетического результата; отдельный Python worker из GD-31J.27l намеренно умеет только `inspect_only`. Поэтому **публикация в worker ещё НЕ перенесена**. Настоящий системный privileged broker/launcher и UID-/namespace-/seccomp-профиль должны быть реализованы и независимо проверены на одноразовой изолированной Astra/Linux VM. Этот PR предоставляет конкретную **unprivileged handoff boundary**, но не симулирует готовность системного sandbox.

Изменения в `probes/eurooffice-desktop/cef_lab/`:

- `native_saveas_fd_broker.py` — интерфейс `BrokerSession().handoff(sock, request, fds, broker_uid=..., expected_worker_uid=..., approved_disposable_vm=True, approved_fixture_hashes=...)`. Получает уже открытый и **связанный** `AF_UNIX/SOCK_SEQPACKET` сокет, **не создаёт процессы, UID, mount namespace и файловые пути**.
- `native_saveas_broker_profile.py` — только проверка **желаемой декларации** профиля. Даже если в профиле написано `seccompFilter=true`, это **не доказательство** применённого Linux seccomp. Результат всегда `blocked`, `kernelIsolationAttested=false`, `nativePublicationAuthorized=false`.
- `CMakeLists.txt`: новый `GETDOC_GD31J27M_ENABLE_BROKER_PUBLICATION_LAB` по умолчанию `OFF` и пока всегда `FATAL_ERROR` при попытке включения. Прежний GD31J27L gate также заблокирован. Никакого подключения нового broker к CEF/GUI.
- Тесты: `test_native_saveas_fd_broker.py`, `test_native_saveas_broker_profile.py`, `test_native_saveas_fd_broker_contract.py`. В офлайн-плане до `dotnet restore` добавлены три suite; в общий acceptance — 21-й fault case и 18-й ручной VM gate. **Тесты написаны, но не запускались.**

## Протокол broker → worker

Схема передачи специально повторяет **строго диагностический** GD31J27l `getdoc-gd31j27l-native-fd-worker-inspect-lab/1`, который не содержит произвольных файловых путей, shell-команд, URL, имени пользователя или операции `publish`.

1. Broker должен работать **не под root** и не под UID worker; `broker_uid` сверяется с действительным `os.geteuid()`. Worker UID задаётся внешней системой запуска; broker проверяет `SO_PEERCRED` установленного Unix-соединения и требует точного `expected_worker_uid` и PID >0. У сокета проверяются `AF_UNIX` и `SOCK_SEQPACKET`, а не только переданный объект Python.
2. Broker принимает только три **уже открытых** дескриптора (source regular file, scratch directory, output directory) с `O_RDONLY` и `FD_CLOEXEC`. Scratch/output должны быть разными inode, на одной файловой системе, `0700`; source — ограниченный regular 1–32 МиБ без group/world write и с единственной ссылкой. **В broker нет `os.open`, `setuid`, `mount`, `unshare`, `exec`, `subprocess`, удаления или файловой публикации**. Файловые marker и SHA дополнительно перепроверяет сам worker.
3. `worker.parse_request` требует точную схему, три фиксированных synthetic OOXML fixture/final имени, два разных ID и SHA256. Любые неизвестные поля, конфликт типов, операция `publish`, пользовательский путь или второй JSON-пакет — отказ до передачи FD.
   **Независимый pin SHA-256:** broker также требует `approved_fixture_hashes` — отдельный словарь SHA-256 для всех трёх synthetic fixtures, который обязан предоставлять внешний проверенный supervisor. Хеш в запросе должен точно соответствовать разрешённому digest выбранного fixture; самоутверждённый `sourceSha256` отправителя не является достаточным доказательством. Этот словарь не заменяет подпись источника или аппаратную аттестацию.

4. Только один `sendmsg` с `SCM_RIGHTS`, отправляющий ровно эти три FD одним `SOCK_SEQPACKET` сообщением. **BrokerSession помечается израсходованным перед первой попыткой**, повторная отправка после тайм-аута, `EINTR`, неполной передачи или ложного ответа запрещена: worker мог уже получить исходный пакет.
5. Временной предел ответа — 5 секунд, длина ответа — до 2048 байт. Допустим только `state=denied`, `laboratoryOnly=true`, либо полный `getdoc-gd31j27l-native-fd-worker-denial-lab/1` с теми же session/tab/fixture и всеми полномочиями строго `false`. Подмена на `approved`, неизвестные поля, новый ancillary FD, пересылка второго сообщения или JSON с повторными ключами — отказ.
6. Даже правильный ответ возвращается как `getdoc-gd31j27m-external-fd-broker-denial-lab/1` с `workerPreflightAttested=false`, `nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `writePermissionGranted=false`, `releaseReady=false`, `approvedForProduction=false`. Это **диагностика IPC**, а не результат Save As.

### Важное замечание о SO_PEERCRED

`SO_PEERCRED` для Unix-сокетов удостоверяет peer credentials, сохранённые ядром в момент установления соединения. Если администратор создаст socketpair **до** переключения UID и просто передаст его процессу worker после `setuid`, наблюдаемый UID может оказаться UID создателя соединения. Тогда broker обязан **отказаться**, а не «исправить» проверку. Будущий launcher должен создать корректно удостоверяемое соединение после установления идентичности worker и проверять это на реальном ядре. Привилегированный launcher пока отсутствует; такой тест невозможно честно объявить пройденным.

## Проверяемый проект профиля безопасности

`draft_profile(revision, broker_uid, worker_uid)` выдаёт синтетический шаблон требований:

- разные nonroot UID;
- разные mount и net namespace; запрет внешней сети и корпоративных mount;
- `NoNewPrivs=1`, `Seccomp=2`, `CapEff=0`, запрет ptrace, отсутствие наследуемых «лишних» FD;
- только read-only source/scratch/output FD, unix-seqpacket, socket FD3, один пакет;
- scratch/output `0700`, имена только synthetic, **`publishOperation=disabled`**.

`inspect_profile` валидирует строгое множество полей, отклоняет любой ослабленный пункт или добавленные права, но **не обращается к ядру и не применяет** systemd, seccomp, Landlock, AppArmor, firewalld, namespaces или учетные записи. Идеальный шаблон оставляет `brokerLaunchAuthorized=false`.

## Что блокирует перенос публикации

1. Для GD31J27k worker должен иметь собственные scratch/output каталоги, принадлежащие worker UID. Broker под UID CEF не должен иметь возможность самостоятельно открывать или создавать приватные каталоги другого пользователя. **Три FD обязан предоставить проверенный внешний supervisor**; источник данных и права на его чтение должны быть сведены к минимуму.
2. Нет проверенного systemd/другого launcher, который создаёт отдельный UID после контрольных проверок, ограничивает mounts и сеть, применяет seccomp и проверяет syscall allowlist. Сам факт различия namespace inode недостаточен.
3. Нет алгоритма worker-side публикации с `O_TMPFILE + fd-bound linkat + fsync`, отвязанного от GUI. **До его независимого аудита существующий C++ путь в лабораторном CEF не переключается.** Нельзя считать handoff переносом Save As.
4. Пакет `SCM_RIGHTS` не удостоверяет исходный native OnSave/CEF callback или то, что байты действительно созданы EuroOffice. Нужны независимые OS и OOXML проверки.
5. Необходимо проверить отказ при peer UID mismatch, stale `SO_PEERCRED`, удалении соединения после передачи, duplicate packets, ошибках `EEXIST`, получении >3 FD, `MSG_CTRUNC`, аварии worker и исчезновении broker. Даже при всех ручных галочках `GD31J27i` не выдаёт production права.

## Область проекта и следующий этап

Весь контур offline/disposable, никаких корпоративных файлов, SMB, WORM, GUP-2, Nextcloud и реальных документов. Ни в GitHub Actions, ни в `main` ничего не включено.

**Следующий GD-31J.27n:** подготовить внешнюю процедуру развёртывания sandbox и аудируемый код, который действительно проверяет состояние kernel после запуска под отдельным UID; только затем писать worker-side synthetic publication, устранять старую in-process публикацию в отдельном opt-in лабораторном режиме и проводить E2E на разрешённой VM. До разрешения тестов все компоненты находятся в состоянии `blocked`.
