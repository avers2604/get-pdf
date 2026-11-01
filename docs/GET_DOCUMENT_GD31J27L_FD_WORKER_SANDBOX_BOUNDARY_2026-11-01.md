# GD-31J.27l — отдельный Linux FD-worker: протокол и жёсткая блокировка без реального sandbox

**Дата:** 1 ноября 2026 года. **База:** Draft PR №124 (GD-31J.27k). **Статус:** подготовлены отдельный worker-процесс, read-only IPC-проверки, контракты и проект внешней изоляции; **не** выполнены сборка C++, Python/MSTest, CEF/EuroOffice SDK, отдельная Linux/Astra VM или настоящие проверки sandbox. Пока это **не подключённое к редактору** лабораторное проектирование, не production Save As.

## Почему новый процесс необходим

Ранние GD-31J.27j–k предотвратили публикацию имени другого inode и перестали публиковать прямо SDK-owned writable inode. Но GUI/CEF renderer и код публикации продолжают работать под общим UID, имеют общий доступ к рабочему sandbox и могут быть скомпрометированы. Отдельный пользовательский процесс с тем же UID не является достаточным ограничителем. Необходим внешне сконфигурированный доверенный broker/launcher, который проверяемо разделяет Linux UID, области файловой системы, network/mount namespaces, capabilities и seccomp.

**Этап l не запускает такой broker и не выдаёт UID.** Вместо непроверенной привилегированной автоматизации сделан отдельный, намеренно **deny-only** worker, который безопасно принимает лабораторный IPC, но ещё не способен сохранять файлы.

## Реальные изменения

### native_saveas_fd_worker.py

Linux-исполняемый worker, который в отдельном процессе получает **только** сокет на заранее назначенном дескрипторе 3. Этот сокет должен быть `AF_UNIX + SOCK_SEQPACKET`; проверяется `SO_PEERCRED`. Никаких TCP, клиентских URL, файловых путей из JSON, HTTP, shell, subprocess, elevated setuid или chmod/rename/unlink рабочих документов.

Один ограниченный пакет (не более 2048 байт) содержит точную схему `getdoc-gd31j27l-native-fd-worker-inspect-lab/1`:

```json
{
  "schema": "getdoc-gd31j27l-native-fd-worker-inspect-lab/1",
  "operation": "inspect_only",
  "fixtureName": "gd31j6_writer.docx",
  "sessionId": "102030405060708090a0b0c0d0e0f010",
  "tabId": "f010e0d0c0b0a0908070605040302010",
  "sourceSha256": "64 lower-case hex characters",
  "expectedFinalName": "gd31j25_writer_saved.docx",
  "laboratoryOnly": true
}
```

Показан **формат**, а не действующий авторизованный запрос: для SHA нужен точный digest synthetic fixture. Два ID должны быть корректными, различными, без пути или бизнес-информации. Для DOCX/XLSX/PPTX строго три allowlist-пары fixture/final; неизвестные поля и повторные JSON-ключи — отказ.

В одном SCM_RIGHTS-сообщении должны прийти **ровно три** `FD_CLOEXEC`-дескриптора без права записи:
- Регулярный synthetic source FD `O_RDONLY`, ровно одна ссылка, ограниченный размер 32 МиБ, SHA-256 сравнивается при чтении **через открытый FD** со стабильными `dev/ino/size/mtime/ctime`.
- Отдельный FD на scratch directory, принадлежащий worker UID, `0700`, точный private owner marker.
- Отдельный FD на output directory, принадлежащий worker UID, `0700`, точный output marker и пустое final basename. Проверяется тот же `st_dev`, разные inode каталогов, `O_NOFOLLOW` для маркеров и конечного имени.

Полученные FD закрываются при любом исходе, в том числе при неверном числе дескрипторов, обрыве или повреждённом пакете. Передача обычного пути, произвольного filename, writable FD или неутверждённого каталога невозможна в рамках синтаксиса запроса.

### Kernel preflight

Внутри реально запущенного worker проверяются факты ядра, а не поля входного JSON:

- `geteuid` не root и не равен UID процесса с другой стороны Unix socket.
- `/proc/self/status`: `NoNewPrivs=1`, `CapEff=0`, `Seccomp=2` (kernel seccomp filter mode).
- Сравнение kernel namespace inode для `mnt` и `net` между worker и реальным peer PID, полученным из `SO_PEERCRED`: namespaces должны различаться.
- Наличие корректного `SO_PEERCRED`, однократный IPC, тайм-аут чтения 5 секунд, без fallback к потоковому сокету/путям/сети. Статические файлы не создаются.

**Эти условия необходимы, но НЕ достаточны.** Отличающиеся namespaces сами по себе не удостоверяют mount whitelist, отсутствие доступа к корпоративному хранилищу или правильность seccomp syscall policy. Даже при полностью успешном preflight worker выдаёт только `state=denied` и возвращает код **2**. `nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `approvedForProduction=false`, `writePermissionGranted=false`, `releaseReady=false`.

### Отсутствие связи с CEF GUI — намеренно

Текущий `native_saveas_policy.h` по-прежнему использует предыдущую лабораторную реализацию в основном CEF хосте. В этом PR нет GUI→worker вызова и нет privileged broker, systemd unit или worker publication action. Это предотвращает ошибочное утверждение, будто отдельный UID уже включён. Опция CMake `GETDOC_GD31J27L_ENABLE_ISOLATED_WORKER_LAB` объявлена **OFF**; попытка включения пока вызывает **FATAL_ERROR**, поскольку внешний broker не прошёл аудит.

## Проверки подготовлены, не выполнялись

- `test_native_saveas_fd_worker.py` — точная JSON-схема, лишние/дублированные поля, неправильные source/SHA/fixture/session, неверный UID, root, same UID, capabilities, seccomp, NoNewPrivs, неподходящий mount/network namespace, закрытые/неподходящие FD, marker/permissions, уже существующее final name и отказ при same-UID `SO_PEERCRED`.
- `test_native_saveas_fd_worker_contract.py` — статически закрепляет FD3, `SO_PEERCRED`, `SOCK_SEQPACKET`, `SCM_RIGHTS`, CLOEXEC, отсутствие subprocess/rename/unlink/write API, постоянные `production=false` и CMake gate.
- `scripts/office_acceptance.py` — оба шага помещены до offline restore; ручной E2E `eurooffice-gd31j27l-distinct-uid-fd3-worker-isolation-e2e`.
- В строгую матрицу `native_saveas_acceptance_gate.py` добавлен 20-й случай `worker-fd-only-distinct-uid-mount-net-seccomp` и 17-й ручной gate. Даже при всех `passed` общий результат остаётся `requires-independent-review`, **без разрешения Save As**.

## Требования к будущему trusted broker / sandbox (ещё НЕ реализовано)

1. Запуск **в одноразовой изолированной Linux/Astra VM** при физически/политически отключённой внешней и корпоративной сети. Запрет корпоративных mount, WORM, SMB, секретов, привилегированных токенов. Отдельная disposable локальная test-учётная запись.
2. Supervisor под утверждённой администраторами ОС политикой создаёт отдельный UID worker, приватные synthetic scratch/output directories, отдельные mount+network namespaces, read-only root/bind-whitelist, запрет ptrace и нежелательного `/proc`, `NoNewPrivileges`, пустые capabilities и **проверенную** seccomp-программу syscall allowlist. Проверить отсутствие inherited дескрипторов к файлам и сети.
3. Supervisor получает результат CEF **после завершения CEF**, проверяет pinned native host + runtime library SHA, и передаёт worker **только** разрешённые три FD через `SCM_RIGHTS`. IPC связать с exact one-use session/tab и digest; повторные/переставленные или переданные чужим peer credentials — отказ.
4. На отдельном этапе перенести саму snapshot/O_TMPFILE/no-replace/fdatasync публикацию в worker, удалить код публикации из GUI-процесса. Пока это не реализовано — прежний in-process lab сохранён, все production права false. Перед передачей не запускать на обычном пользовательском ПК и не считать достигнутой настоящую OS isolation.
5. Требуются реальные тесты `/proc/self/status`, namespace identity, seccomp audit, UID mapping, FileDescriptor lifetime, `SO_PEERCRED`, изоляции filesystem, режимов отказа `fsync`, `EEXIST` и death-of-parent/broker. Наличие отдельного PID само по себе не является доказательством.

## Следующий этап

**GD-31J.27m:** внешний доверенный sandbox broker и kernel-level профиль в disposable VM, фиксация boundary успешной передачи FD и перенос публикации из CEF host в worker с отдельным UID, только после стендовой проверки. В реальном продукте политиками должен управлять системный администратор; минимизация прав важнее скорости внедрения.

**Текущий PR не даёт прав на сохранение файлов.** Не объединять в main, не включать GitHub Actions; реальных тестов и компиляции пока не было.
