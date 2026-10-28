# GD-31J.8 — IPC и вкладки нативного EuroOffice, GUP-2 и GET Print

Дата: **29 октября 2026**. Ветка поверх GD-31J.7 / Draft PR #75. **Проверки и сборку откладываем по решению пользователя.** Это функциональный код с тестовыми сценариями, а не успешный локальный тест, релиз или включённый на рабочих ПК редактор.

## 1. Положение в архитектуре

```text
GET Desktop (Avalonia; единое окно)
  └─ Native Office tab state (EuroOfficeNativeTabRegistry)
     └─ Bounded JSONL diagnostics (EuroOfficeNativeIpcPump)
        └─ isolated Qt/CEF lab process [future security-approved owner]
           └─ QCefView + synthetic DOCX/XLSX/PPTX [existing GD-31J.6]
  ├─ GET PDF / собственный мастер печати [blocked: trusted adapter absent]
  └─ GUP-2 / GET-system immutable CAS + ACL + audit [blocked: server absent]
```

**Никакой чужой `QWidget*`, `HWND`, CEF handle или каталог профиля не передаётся в Avalonia.** Это логическая связь состояний вкладок, а не готовое встраивание Qt в одну визуальную вкладку. Нельзя использовать у LibreOffice existing `EmbeddedWindowHost.FindWindow(string marker)` без дополнительной проверки принадлежности окна нужному PID и OS оконной иерархии. Локальный LibreOffice/UNO остаётся действующим резервным редактором.

## 2. Версионированный IPC

Добавлен `src/GetDocument.Office/EuroOfficeDesktop/EuroOfficeNativeIpc.cs`:

- `getdoc-eurodesktop-ipc/1`, **только UTF-8 JSONL** (одна строка, LF), сообщение до **8192 символов**, строгая проверка полей без дубликатов.
- Каждое сообщение имеет canonical lowercase GUID `sessionId` и `sequence` (1..1 000 000). Нет авторизации только по GUID: в будущем **доверенный процесс должен быть запущен и привязан к этому сеансу** отдельной проверкой OS identity/подписанного бинарника.
- Разрешены только `kind=event` с `view-loaded(10)`, `modified(1012)`, `on-save(3002)`, `print-end(5013)`, `closed(2001)`, либо `kind=snapshot` с вложенным проверяемым `GD31J7_EVENTS_ONLY` receipt.
- Запрещены `open-local`, `save`, `print-direct`, `run-macro`, `javascript`, `exec`, произвольные пути, URL и документные байты в протоколе наблюдения.
- `ReadFrameAsync(TextReader, CancellationToken)` ограничивает чтение, учитывает отмену, запрещает незавершённые пакеты и EOF вместо необходимого закрытия.

Пример сообщения отчёта (синтетические значения, не результат запуска):

```json
{"protocol":"getdoc-eurodesktop-ipc/1","kind":"event","sessionId":"e38a9a2f-9b21-4dad-8f20-28ad39d0b31f","sequence":2,"event":"closed","eventCode":2001}
```

## 3. Модель вкладок и реакции на аварии

`EuroOfficeNativeTabRegistry`: `Register(tabId,documentId,sessionId)`, `Apply(tabId,frame)`, `Snapshot(tabId)`, `Abort(tabId,errorCode)`, `RemoveTerminal(tabId)`.

- Ровно один нативный сеанс на вкладку, без совместного использования одного `sessionId` двумя документами. Лимит по умолчанию 12; допустимый лимит конфигурации 1–32.
- Номера сообщений строго последовательны (`1,2,3,...`); повтор, пропуск или чужой session/tab отклоняются.
- Состояния: `Requested`, `ViewLoadedUnverified`, `Closed`, `Failed`. Новая редакция, PDF и печать **всегда неподтверждены**. `StatusText` предназначен для будущей вкладки GET.
- Отдельный `EuroOfficeNativeIpcPump` может принимать ограниченный поток `TextReader` (будущий перенаправленный stdout проверенного процесса). При отмене, ошибке, неожиданном EOF состояние конкретной вкладки становится Failed; другие вкладки не затрагиваются. Он **сам не запускает Qt**, и его успех **не доказывает нулевой exit code процесса**. Это обязанность будущего process supervisor.

### Как появился один реально совместимый отправитель

Доработан экспериментальный `cef_lab/cef_staged_open.cpp`, уже содержащий настоящий `OpenLocalFile` (но не запускаемый сейчас):

- Прежний тестовый режим `--open-staged-lab <root> <fixture-name>` сохраняет обратную совместимость: stdout диагностического маркера и receipt из GD-31J.7.
- Новый **только для изолированной лаборатории** режим `--open-staged-lab <root> <fixture-name> --ipc-session <lowercase-GUID>`. Он дополнительно строго проверяет UUID (Qt `QUuid::WithoutBraces`).
- **Только после закрытия** 15-секундного окна CEF он отправляет `snapshot sequence=1` с `GD31J7_EVENTS_ONLY`, затем `closed sequence=2`. Никаких новых команд открытия/сохранения/печати через stdin.
- Это **реальная однонаправленная диагностическая передача после завершения окна**, а **НЕ** живой полноценный IPC редактора, не редактирование и не мост Qt window в Avalonia. Код компиляции/запуска не проверен, пока лаборатория отложена.

## 4. Сохранение через GUP-2 и передача в свой GET Print

Добавлен `EuroOfficeNativeHandoff`. Он получает только **предварительно осмотренные** `EuroOfficeNativeArtifactCandidate` из GD-31J.7 и формирует типизированные:

- `EuroOfficeNativeRevisionProposal(documentId,expectedBaseVersion,SHA256,length,format)`;
- `EuroOfficeNativePrintProposal(documentId,PDF_SHA256,length)`.

Новые предложения содержат только метаданные (без локального пути к служебному документу). Модель версии требует `expectedBaseVersion`, чтобы будущий GET-system/GUP-2 применял проверку version CAS и аудит. Оба состояния **blocked**, результаты `Committed=false`, `WormOriginalModified=false`, `PrintApproved=false`.

- `CommitRevisionAsync` **всегда** возвращает `NotSupportedException`: пока не подключены подтверждение ACL/SSO, trusted CAS, immutable WORM-оригинал, контроль конфликта версий, запись новой редакции и журнал аудита.
- `OpenInGetPrintWizardAsync` **всегда** возвращает `NotSupportedException`: пока нет подтверждённого канала передачи/проверки настоящего PDF и собственного мастера печати.
- Ни одного обращения к принтеру, серверу, сетевой папке, файловой записи или API GUP-2 новая заглушка не делает.

**Предупреждение:** текущий `EuroOfficeNativeArtifactCandidate` публично конструируем и содержит только базовую ZIP/PDF проверку. Его SHA не является доказательством доверенного происхождения, отсутствия уязвимостей, корректного рендера или атомарного снимка. Даже после подключения сервера нельзя считать одного proposal достаточным для публикации.

## 5. Тесты подготовлены, не запускались

| Файл | Назначение |
| --- | --- |
| `EuroOfficeNativeIpcTests.cs` | Строгий JSONL, отбрасывание чужих команд и ошибок, отмена, snapshot |
| `EuroOfficeNativeTabRegistryTests.cs` | 2 независимые вкладки, replay/sequence, лимиты, terminal cleanup |
| `EuroOfficeNativeIpcPumpTests.cs` | поток, EOF, отмена, ошибка только одного сеанса |
| `EuroOfficeNativeHandoffTests.cs` | GUP-2 и GET Print остаются заблокированными до trusted server |
| `test_native_ipc_contract.py` | source contract формата native snapshot+closed |
| `scripts/office_acceptance.py` | добавлен `native-ipc-contract-tests` до .NET restore |
| `scripts/tests/test_office_acceptance.py` | тест самого плана проверки |

Команды для последующего **локального** запуска после готовности функциональности:

```powershell
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --filter "FullyQualifiedName~EuroOfficeNativeIpc|FullyQualifiedName~EuroOfficeNativeTabRegistry|FullyQualifiedName~EuroOfficeNativeHandoff"
python -m unittest discover -s probes/eurooffice-desktop/cef_lab -p test_native_ipc_contract.py -v
```

`main` не изменён, GitHub Actions не включаются, Draft не сливать без будущей локальной приёмки.

## 6. Следующий кодовый этап GD-31J.9

- Supervisor **проверенного по SHA-256 подписанного** нативного процесса, который привязывает stdout/exit code и tab/session; потеря связи и ненулевой exit должны приводить к Failed до терминального визуального статуса.
- Рабочий **двусторонний** IPC требует протокол подтверждения команд и изоляции OS process, а также реальной SDK реализации callback; текущий GD-31J.8 только про наблюдения.
- Для реального единого окна ГЭТ: исследовать отдельный Qt window handoff для Win32 и X11/Wayland **с верификацией OS/PID/ownership и lifecycle**. Qt/CEF нативный указатель нельзя передавать в процесс Avalonia.
- Отдельно реализовать доверенный GUP-2/GЕT-system CAS и офисный PDF export. До их подключения — fail closed.
