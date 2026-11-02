# GD-31J.27ag — строгий протокол сквозной проверки supervisor → broker → worker

**Дата:** 2 ноября 2026 г. **База:** Draft PR №145, GD31J27af.  
**Статус:** код и тесты подготовлены, *не исполнялись*. В этом PR нет живого трёхпроцессного запуска, реальной Astra Linux VM, root-службы, systemd/D-Bus, SMB/WORM, корпоративных документов, SCM_RIGHTS, SaveAs, реальных тестов/сборок или публикации. Основная ветка `main` не изменена.

## Почему не соединяем три процесса в одном тестовом Python-процессе

В действующей архитектуре supervisor/root, broker и worker имеют **разные UID, credentials на connected Unix SOCK_SEQPACKET, pidfd, собственный fd3 и процессы**. Их нельзя корректно проверить последовательными вызовами в одном процессе: `SO_PEERCRED` не сменит PID/UID, передача `SCM_RIGHTS` требует реально связанного получателя, а блокирующие `sendmsg/recvmsg` могут повиснуть без одновременной работы второй стороны. Эмулировать это положительными флагами и объявить E2E успешно выполненным было бы ошибкой.

**Выбранный этап** — отдельный *чистый и явно недоверенный* валидатор записи событий для будущего независимого тестового окружения; это **не реальный E2E**, а проверка формы и последовательности сценария. Запуск реальных процессов возможен только позже, после ручного утверждения offline VM и OS-изоляции.

## Новый модуль

`native_saveas_three_party_offline_trace.py`:

- Не импортируется штатным `worker.main`, `broker_listener.main`, `RootSendOnce.send`, CEF, GD31J27af collector. Нет `subprocess`, `fork`, `setuid`, `os.open`, `socket`, network, shell, systemd, файловых записей или таймеров.
- Принимает только **канонический ASCII JSON + один LF** ≤8192 байт без duplicate/extra keys, NaN, произвольных полей, `releaseReady`, `documentPath`, `writePermissionGranted`. `schema=getdoc-gd31j27ag-synthetic-three-party-denial-transcript/1`, `source=synthetic-operator-replay-untrusted`, `mode=inspect_only`, `status=denied`, `documentAccessCount=0` как строгое `int`.
- Требует совпадения `runId` (32 lowercase hex) и `fixtureSha256` (64 lowercase hex, не нулевой) с **вне сценария закреплёнными** значениями. `hmac.compare_digest` защищает сравнение от простой ошибки, но эти pins не удостоверяются модулем и могут быть подделаны тем же составителем записи.
- Ровно **девять** событий с обязательными `step=1…9`, `role`, `event`, `result` — никакие поля для записи/публикации, повтора или иного исхода не разрешены.
- `OneShotTraceReview.inspect` потребляет сессию **до** разбора. После любой ошибки второй вызов того же объекта запрещён. Это не долговременное глобальное хранилище использованных `runId`: новый объект способен повторно принять те же байты, поэтому **нет независимой анти-replay-аттестации**.

### Ожидаемая последовательность

| № | Актор | Событие | Результат в записи |
|---:|---|---|---|
| 1 | supervisor | vm-preflight | observed |
| 2 | supervisor | root-manifest-review | observed |
| 3 | supervisor | synthetic-fds-offered | observed |
| 4 | broker | synthetic-fds-observed | observed |
| 5 | broker | nonce-issued | observed |
| 6 | worker | fd3-nonce-received | observed |
| 7 | worker | inspect-only-refusal | denied |
| 8 | broker | refusal-received | denied |
| 9 | supervisor | no-write-cleanup-observed | observed |

**Все события — только текстовые утверждения неустановленного автора.** `observed` не означает фактическую работу OS, корректный SCM_RIGHTS, подпись supervisor, получение inode, выполнение systemd unit или реальный отказ worker. Поля `traceOrderObserved=true` и `noDocumentAccessClaimObserved=true` означают лишь совпадение структуры и строки `documentAccessCount=0`, а **не** подлинность или полноту доказательств.

Независимо от совпадения всех полей `osEvidenceAttested`, `transportAttested`, `workerExecutableAttested`, `trustedVmIsolationAttested`, `trustedSupervisorAttested`, `nativePublicationAuthorized`, `writePermissionGranted`, `releaseReady`, `approvedForProduction` — **строго false**.

## Что проверить при будущей РЕАЛЬНОЙ лаборатории Astra Linux

**Это ручной список условий, не автоматически исполняемый скрипт:**

1. Внешний оператор документирует **одноразовую VM**, отключённую от корпоративного контура: нет SMB, WORM, Nextcloud, реальных DOCX/XLSX/PPTX/PDF, ни одного business-mount и сетевого пути к пользовательским данным. Закрепляет OS image digest и independent trust anchor.
2. Не запускать production services или корпоративный GET-system. Отдельно и независимо проверить фактические effective systemd unit properties, подписи бинарей, import/ELF dependency closure, cgroup, mount/network/PID namespaces, Astra МРД и seccomp. Наблюдение должно быть независимым от worker.
3. Создать **только синтетические** read-only fixtures из ранее закреплённого каталога. Разные UID supervisor, broker, worker; проверить `SO_PEERCRED` после изменения UID, `SO_PEERPIDFD`, реальные `pidfd_open`, FD3, O_NOFOLLOW и одноразовые Unix sockets.
4. Запускать три процесса только средствами **отдельно проверенного** VM harness, не привязывать `RootSendOnce.send` к обычному приложению. Воспроизвести реальные события: supervisor одноразово передаёт только синтетические FD, broker делает one-shot nonce, worker отвечает **только denied** и закрывает handles.
5. Внести события в отдельно подписанный transcript с OS timestamp, process IDs, kernel proofs и операторским trust anchor. **Нынешний GD31J27ag JSON не содержит ни подписи, ни журналирования, ни доказательства kernel origin.** Настоящий E2E нельзя считать пройденным лишь потому, что его JSON проходит этот валидатор.
6. Проверить fault-injection: duplicate/extra SCM_RIGHTS, wrong UID, stale PID, недоступный procfs, разрыв во время send/recv, hang/timeout, crash worker, ложный `saved`, повтор того же session, неизвестные files/mounts. **При любом сбое — отказ, неизвестный исход не ретраить.**
7. Только после объективного независимого review собранных реальных OS evidence можно планировать следующий **deny-only** тест. Включение реального native SaveAs по-прежнему **не разрешается**.

## Отложенные регрессионные тесты и ограничения

`test_native_saveas_three_party_offline_trace.py` — mock-only: точный ожидаемый порядок, дополнительное/отсутствующее событие, swapped steps, попытки `success`/`saveas`/`publish`, запрет `documentAccessCount>0`/bool, unknown fields, noncanonical JSON, duplicate keys, чужой run/fixture SHA, consumed после ошибки. `test_native_saveas_three_party_offline_trace_contract.py` — source-only отсутствие любых system calls, runtime imports, production permissions.

Оба suite зарегистрированы **до restore** в `scripts/office_acceptance.py`; `scripts/tests/test_office_acceptance.py` проверяет порядок и `releaseReady=false`.

Список `native_saveas_acceptance_gate.py`: **41 fault cases / 38 mandatory manual E2E gates**. Новые:
- `triparty-trace-replay-duplicate-write-claim-deny`;
- `eurooffice-gd31j27ag-independent-triparty-denial-trace-vm-e2e`.

CMake: `GETDOC_GD31J27AG_ENABLE_TRACE_PUBLICATION_LAB=OFF`, при ON безусловный `FATAL_ERROR GD31J27AG_BLOCKED`. Все предыдущие l–af блокировки сохранены.

## Что осталось после GD31J27ag

Основной блокер теперь **не ещё один валидатор**, а отсутствие независимого рабочего Astra Linux VM стенда, проверяемого способа запускать три процесса под разными UID, подписи реального runtime evidence и реального исполнения подготовленных тестов. В следующем этапе следует **закончить эти условия и выполнить испытания при явном разрешении оператора**, а не наращивать цепочку синтетических уровней контроля.

**Ничего не запускалось:** Python unit tests, C++/Qt/CEF build, systemd, VM, kernel PID/FD calls, production or synthetic SaveAs. GitHub Actions и main не изменялись.
