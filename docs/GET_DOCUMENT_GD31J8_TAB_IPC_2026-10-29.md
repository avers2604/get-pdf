# GD-31J.8 — безопасный IPC отдельного Qt-хоста, сеансы вкладок и закрытые интеграционные шлюзы

**Дата:** 29.10.2026. **Основание:** GD-31J.7 Draft PR #75. **Этап функциональной подготовки; сборка и проверки отложены по решению пользователя.** Никаких утверждений о работоспособности native editor, полной интеграции Avalonia или выпуска.

## 1. Что добавлено

### Qt-only IPC: настоящий обмен командами между отдельными процессами

`probes/eurooffice-desktop/native_host/ipc_probe.h/.cpp` и новый режим `--ipc-probe` существующего `getdoc-eurooffice-qt-probe`:
- Использует только `QCoreApplication`, **не** Qt QWidget, QCefView, CEF, офисный документ и не веб-движок.
- Строгое первое сообщение: `hello`, протокол `getdoc-eurodesktop-ipc/1`, случайный `sessionId` GUID32, точная ревизия `Euro-Office/DesktopEditors@6a671bb1ad238c61965a64de3a005c420940c814`. Ответ `hello-ack` с `features=[]`. Это **не** удостоверение личности бинарника: подлинность проверяется в .NET-политике запуска и в дальнейшей ИБ-приёмке.
- Затем можно отправить только `attach-tab` → `resize` → `close`. Все команды содержат `sessionId`, `tabId` GUID32 и строго возрастающий `sequence`. `resize` принимает только ширину/высоту 1…16384 и масштаб 0.5…4.0. После `close` процесс завершает работу.
- Установлен верхний предел 4096 байт на строку и 128 команд на процесс. Нестандартный JSON, дублированные/неизвестные поля, иная версия, повторные sequence, чужой ID, файл, URL, `open-local`, `save-as-new-revision`, `export-pdf`, `print-direct`, произвольный handle или shell-команда — **отказ без выполнения**.
- `resize` подтверждает **только принятые параметры**, он пока **не меняет размеры реального CEF/Qt editor view** и не передаёт оконные дескрипторы в Avalonia.

### .NET Client: `EuroOfficeNativeTabIpc`

- Для каждой пары `sessionId + tabId` клиент формирует канонический JSONL, проверяет ответы по полному набору полей, запрету дублей, точному `sequence`, сеансу, вкладке, состоянию и пустому `features`.
- Команды строго последовательные через `SemaphoreSlim`; timeout 5 секунд и `CancellationToken`. Ошибка EOF, некорректный ACK, отмена, превышение размера или таймаут переводят канал в терминальное `Failed`, без повторного использования сообщений/сеанса.
- Статусы: `New / Ready / Attached / Closed / Failed`. После закрытия новая операция невозможна. `AuthorizeSaveOrPrint` всегда запрещён.
- `EuroOfficeNativeIpcProcessSession` использует реальный `Process.Start` с `UseShellExecute=false` и только явным `--ipc-probe`; бинарник должен находиться в утверждённом локальном каталоге и совпадать по предварительно установленному SHA-256 (существующая `EuroOfficeNativeLabHostPolicy`). При ошибке запуска, handshake, закрытия или отмене процесс завершается/освобождается.
- **Ограничения:** hash/path + GUID не устраняют TOCTOU, не подтверждают sandbox, кодовую подпись или ACL; не использовать на реальных служебных документах. Нет настоящего редактора, считывания / сохранения файлов либо соединения с реальным GET Desktop.

### Независимое состояние вкладок: `EuroOfficeNativeTabRegistry`

- Отдельное состояние документа для каждого `tabId` и `sessionId`: Requested, ViewLoadedUnverified, Closed, Failed.
- После сбоя разрешён перезапуск с **новым sessionId** и увеличенным generation; ACK старого процесса не применяется.
- События `EuroOfficeNativeReceipt` из GD-31J.7 применяются **только** к соответствующему сеансу. Даже после `OnLoaded` / `ONSAVE` / `PRINT_END` поля `DocumentVerified/SaveCompleted/PdfReady` остаются false.
- Реестр не содержит WORM-файлов, путей, учётных данных и не открывает нативных окон. GUI вкладок и привязка Avalonia к OS window/surface требуют отдельного адаптера после лаборатории.

### Заглушки GUP-2 и собственного мастера печати

`EuroOfficeNativeExternalGates` содержит **явные отказные интерфейсы**:
- `Gup2RevisionPublishAvailable=false`. `PublishRevisionToGup2Async` — `NotSupportedException` до работающего GET-system/GUP-2 интеграционного контура, ACL, WORM, immutable CAS, проверки исходной версии и аудита.
- `GetPrintWizardTransferAvailable=false`. `TransferPdfToGetPrintWizardAsync` — `NotSupportedException` до действительного PDF export, проверки содержимого через PDF-движок ГЭТ и безопасного открывания собственного мастера печати.
- Такой интерфейс позволяет сохранить архитектурные границы: никакое уведомление от внешнего Qt/CEF процесса не является правом на запись в архив или прямой доступ к принтеру.

## 2. Подготовленные тесты, но НЕ запущенные

- `EuroOfficeNativeTabIpcTests`: handshake/attach/resize/close, чужой session/tab, sequence replay, дубликаты, размер ответа, EOF, отмена, политика запуска проверенного бинарника и **опциональный** реальный subprocess round-trip.
- `EuroOfficeNativeTabRegistryTests`: независимые вкладки, stale-event, crash/restart generation, закрытие, закрытые GUP-2/Print-шлюзы.
- `test_qt_ipc.py`: настоящий локально собранный Qt процесс, отказ от сохранения/печати/файлов, повторных sequence и чужих идентификаторов.
- `test_qt_ipc_contract.py`: отдельно проверяет отсутствие файловых/принтерных API в native IPC source. 
- `native_host/CMakeLists.txt`: тест `gd31j8_qt_tab_ipc` для локального CTest.
- `scripts/office_acceptance.py`: offline `qt-ipc-contract-tests` перед `dotnet restore`; при явном `--qt-host-binary` проверка живого `native-qt-tab-ipc`. Существующие CEF, OOXML, PDF и Office.Tests остаются в плане GD-31I.

**На данном этапе никакие тесты/сборки/лаборатория не запускались**: не утверждать PASS, runtime, полноценное встраивание, сквозную печать либо сохранение редакции.

## 3. Контракт сообщения

Только для примера (GUID и порядок sequence — иллюстрация; реальный клиент создаёт новые идентификаторы):

```json
{"command":"hello","protocol":"getdoc-eurodesktop-ipc/1","sessionId":"25fe787823da4d76b28d2fd5ffb187e8","upstreamCommit":"6a671bb1ad238c61965a64de3a005c420940c814"}
{"command":"attach-tab","protocol":"getdoc-eurodesktop-ipc/1","sequence":1,"sessionId":"25fe787823da4d76b28d2fd5ffb187e8","tabId":"b70b9c8b73414d99ae3b2db31d9c25c7"}
{"command":"resize","height":720,"protocol":"getdoc-eurodesktop-ipc/1","scale":1.5,"sequence":2,"sessionId":"25fe787823da4d76b28d2fd5ffb187e8","tabId":"b70b9c8b73414d99ae3b2db31d9c25c7","width":1280}
{"command":"close","protocol":"getdoc-eurodesktop-ipc/1","sequence":3,"sessionId":"25fe787823da4d76b28d2fd5ffb187e8","tabId":"b70b9c8b73414d99ae3b2db31d9c25c7"}
```

В IPC используется **Qt-каноническая форма JSON** (упорядоченные ключи, без лишних пробелов), что отбрасывает и дублированные ключи. Строковый транспорт — `stdin/stdout` конкретного запущенного процесса, не TCP/HTTP и не широковещательный канал.

## 4. Отложенные команды на тестовом ПК

```bash
cmake -S probes/eurooffice-desktop/native_host -B artifacts/gd31j8-qt
cmake --build artifacts/gd31j8-qt --config Release
ctest --test-dir artifacts/gd31j8-qt -C Release --output-on-failure

# Сначала вручную утвердить SHA-256 конкретного бинарника по отдельному процессу ИТ:
export GD31J8_QT_IPC_BINARY=/approved/qt/getdoc-eurooffice-qt-probe
export GD31J8_QT_IPC_SHA256=<approved-64-hex-digest>
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release
```

Для Windows заменить путь и синтаксис environment на PowerShell, но не изменять настройки производственной сети. Общий сценарий `python scripts/office_acceptance.py --feed /approved/offline-nuget --qt-host-binary /approved/qt/getdoc-eurooffice-qt-probe` **не** даёт разрешения на выпуск: `releaseReady=false` до отдельных аппаратных и ИБ-проверок.

## 5. Что дальше (GD-31J.9)

После завершения протокольной части можно писать **диспетчеризацию запросов к готовому собственному UI**: одна вкладка ГЭТ, хранение геометрии/раскладок, события закрытия и восстановление при падении дочернего редактора. Реальное присоединение поверхности CEF/Avalonia на Windows и Astra/Wayland **не реализовывать как готовое без подтверждения native HWND/OSR API**. Далее — адаптеры для **новой** редакции через CAS (не WORM-оригинал) и валидного PDF в собственный мастер печати ГЭТ. Для сетевых серверов пока допустимы заглушки.

**Статус:** подготовлена связка Qt-only IPC + .NET Process + tab registry, но нет настоящего CEF editor в IPC-контуре. Основной LibreOffice/UNO остаётся рабочим fallback; серверный EuroOffice DocumentServer — отдельное выбранное целевое решение. `main` и GitHub Actions не изменены.
