# GD-31J.14 — сессионный ввод мыши и фокуса в Wayland CEF, стратегия Windows/X11

**Дата:** 29 октября 2026. **Основание:** GD-31J.13 PR #83, stacked Draft PR. **Тесты/сборка/испытания отложены по решению пользователя.** Основной `main` и GitHub Actions не меняются.

## 1. Реальные API закреплённого EuroOffice

Исходник `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51` подтверждает:

- `ChromiumBasedEditors/lib/include/cefview.h`: `CCefView::focus(bool)`, `SendMouseMoveEvent(x,y,mouseLeave,modifiers)`, `SendMouseWheelEvent(x,y,deltaX,deltaY,modifiers)`, `SendMouseClickEvent(...)` и `SendKeyEvent(...)`.
- `ChromiumBasedEditors/lib/src/cefview.cpp`: события реально поступают в `CefBrowserHost::SendMouseMoveEvent`, `SendMouseWheelEvent`, `SendMouseClickEvent`, фокус — `SetFocus`. Это реальные методы движка, не заглушки.
- `ChromiumBasedEditors/lib/qt_wrapper/src/qcefview.cpp`: на Wayland Qt получает локальные координаты в DIP, умножает их на `devicePixelRatio()` и передаёт CEF **физические пиксели**, так как OSR view rect использует физический размер с CEF device scale factor=1.0.
- Поставщик имеет обработчики `mousePressEvent/mouseMoveEvent/wheelEvent/focusIn/focusOut` и отдельных клавиатуры/IME. Но между **разными процессами** они не вызываются автоматически: нужен собственный строго ограниченный IPC.

## 2. Добавлено в GD-31J.14

### Новый пользовательский путь, строго лабораторный

```text
GET/Avalonia NativeOfficeDiagnosticPane (единственный существующий TabControl)
  → жест на тестовом CEF-preview: PointerPressed означает ТОЛЬКО фокус
  → PointerMoved / PointerWheelChanged (<=13 перемещений/с, bounded wheel)
  → вычисление доли от видимой картинки, исключая letterbox
  → EuroOfficeOsrLabInputChannel (строгая очередь + sequence)
  → stdin JSONL определённого SHA-approved CEF subprocess
  → OsrInputPump Qt QTimer (не внутри CEF OnPaint)
  → проверка command/session/tab/sequence/dimensions/rate
  → вычисление физических координат из QCefView width/height * devicePixelRatioF()
  → CCefViewEditor::focus / SendMouseMoveEvent / SendMouseWheelEvent
```

Этот механизм работает **только** в отдельном `--open-staged-wayland-input-lab`, созданном для трёх известных синтетических файлов GD-31J.6 и запускаемом на утверждённом офлайн-стенде Linux/Wayland с проверенной локальной сборкой.

**Нажатие мыши на превью исключительно выдаёт фокус, но не вызывает `SendMouseClickEvent`.** Клик, выделение и редактирование ячеек/текста таким путём **не реализованы**. Клавиатура, IME, drag/drop, clipboard, печать, сохранение и GUP-2 также не включаются. Документный импорт/сохранение не становятся подтверждёнными из-за события ввода.

### Изоляция сборки и CLI

В `cef_lab/CMakeLists.txt` опция `GETDOC_GD31J14_ENABLE_INPUT_LAB=OFF` по умолчанию. Её включение требует также все предшествующие флаги GD31J3/4/6/12/13, утвердённый pinned upstream и отдельную офлайн-VM. В `GetDocument.Desktop` обычный launcher **не вызывает** новую лабораторную точку входа.

Две независимые CLI-команды:
- `--open-staged-wayland-stream-lab <root> <fixture> <session> <tab>` — прежний просмотр до 8 кадров, stdin **не перенаправляется**, команд ввода нет;
- `--open-staged-wayland-input-lab <root> <fixture> <session> <tab>` — отдельный CEF preview + stdin input, только при `GETDOC_GD31J14_POINTER_INPUT=1`.

В обоих случаях сохраняются 15-секундный CEF lifecycle, максимум 8 кадров с частотой до 2 FPS, не более одного pending stdout packet и завершающий `end`-receipt; stdin никогда не превращается в командную оболочку или доступ к файлам.

### Схема `getdoc-cef-osr-input/1`

**Строго канонический JSONL** из C# `SortedDictionary`; Qt проверяет точный roundtrip `QJsonDocument::Compact` (отвергаются повторные поля/неоднозначные сериализации), протокол, session GUID32, tab GUID32 и последовательные `sequence=1..128`. Ограничение **4096 байт** на строку, максимум **128** команд на сеанс и **30** команд в секунду. При нарушении — отказ и завершение лабораторного процесса.

Разрешено только:
- `{"command":"focus","focused":true|false,...}` — событие браузерного фокуса; на смене вкладки/уходе указателя отправляется false.
- `{"command":"move","x10000":0..9999,"y10000":0..9999,...}` — нормализованные координаты указателя **после** фокуса.
- `{"command":"wheel","x10000":0..9999,"y10000":0..9999,"deltaX":-120..120,"deltaY":-120..120,...}` — одно ненулевое ограниченное событие прокрутки **после** фокуса.

Каждое сообщение передаёт те же `sessionId`, `tabId`, `protocol`, `sequence`. Для координат Qt заново использует действительный `QCefView::devicePixelRatioF()`, а не DPI, переданный из UI клиента. Нет недоверенных native handles, JavaScript strings, файловых путей и URL.

Нет подтверждения выполнения каждого события: успешная запись в stdin **не доказывает**, что CEF применил его, и не даёт прав на работу с документом.

### Управление жизненным циклом

- `EuroOfficeOsrLabInputChannel` в C#: `SendPointerMoveAsync`, `SendWheelAsync`, `SendFocusAsync`; `SemaphoreSlim`, bounded `CancellationToken`, 2-секундный write timeout и строгий sequence. Любая ошибка записи отзывает канал, чтобы нельзя было повторно отправить уже частично записанный пакет. `SendMouseClick/KeyboardOrIme/ShareClipboard` явно `NotSupportedException`.
- `osr_input.{h,cpp}`: нативный `OsrInputPump`, `O_NONBLOCK`, один `read` за Qt timer tick и ограниченный разбор, только известные команды. События вызывают реальные проверенные CEF API на Qt event thread; **никаких** функций из CEF `OnPaint` для обработки ввода.
- `EuroOfficeOsrStreamProcessLab`: отдельные `CreateInputStartInfo` и `RunInputLabAsync` с `RedirectStandardInput=true` только в интерактивной лаборатории. Канал передаётся в пользовательский UI через короткую callback-регистрацию и отзывается при завершении или аварии процесса.
- `NativeOfficeDiagnosticPane`: подписывается на `PointerPressed/PointerMoved/PointerWheelChanged/PointerExited` только для **лабораторного изображения**. Учитывается `Stretch.Uniform` и возможные поля letterbox. Перемещения ограничены до ~13/с. При уходе указателя/переключении вкладки фокус снимается. При потере канала интерфейс отключает ввод и не заявляет успех.
- `MainWindow`: отдельная опция `pointerInputLabApproved=false` в **internal** `StartNativeCefOsrLabStreamAsync`. По умолчанию ничего не меняется. Только при двух явных разрешениях (`labApproved` и `pointerInputLabApproved`) вызывается интерактивный lab process. Все обработчики и Attach/Detach работают в UI Dispatcher, а прежний поток без ввода сохраняется.

## 3. Windows 10/11, Astra Linux X11 и Wayland — выбор направления

| Платформа | Что подтверждено в pinned upstream | Реализация GD-31J.14 | Рабочий маршрут |
| --- | --- | --- | --- |
| Astra Linux / Wayland | `SetAsWindowless`, CEF `OnPaint` | Есть лабораторный код preview+pointer/wheel/focus, запуск не проверен | LibreOffice/UNO до локальной проверки OSR |
| Astra Linux / X11 | `SetAsChild` native window | Не включать небезопасный `XReparentWindow`/чужой XID | LibreOffice/UNO |
| Windows 10/11 | `SetAsChild` HWND | Не включать произвольный `SetParent`/чужой HWND | LibreOffice/UNO |

Для соблюдения **единого окна ГЭТ, вкладок, собственного дизайна и закрытой сети** предпочтительный будущий путь Windows/X11 — отдельная upstream-адаптация CEF к проверенному OSR/windowless и унифицированному input transport (без переноса произвольных окон между процессами). До такой адаптации использовать существующий функциональный локальный редактор и только пассивный лабораторный EuroOffice. Не подменять неподтверждённым CEF действующие DOCX/XLSX/PPTX.

## 4. Подготовленные тесты — не запускались

- `EuroOfficeOsrLabInputChannelTests`: порядок focus→move→wheel→focus-out, сессионные идентификаторы, диапазоны координат, лимиты прокрутки, запрет прямой передачи клика/клавиатуры/clipboard, отмена и отзыв после повреждения канала.
- `EuroOfficeOsrStreamProcessPolicyTests`: stdin доступен **только** в новой explicit CLI, обычный поток остаётся чтением кадров, SHA-approved executable.
- `test_osr_input_contract.py`: строгая проверка CMake default OFF, native poller, sequence, bounded stdin, DPI и отсутствия SendKey/SendMouseClick/clipboard.
- `scripts/office_acceptance.py`: добавлен этап `cef-osr-input-source-contract-tests` перед offline restore; реальный CEF E2E/Qt/Windows/Astra остаются отложены.

**Ни один тест в этом этапе не запускался.** Не утверждать successful compile/runtime; CEF может быть `no_sandbox=true` без `CEF_USE_SANDBOX`. Запуск допускается только после утверждённого OS containment на тестовой офлайн-VM без служебных документов.

## 5. Следующий функциональный этап GD-31J.15

Подготовить безопасное назначение фокуса и взаимодействие с реальными элементами документа, включая клики и двойные клики, но только после отдельной валидации UX/CEF event state и лабораторного input policy. Для ввода текста/IME и clipboard должны появиться отдельные контракты с запретом скрытой передачи файлов/данных. Параллельно исследовать/подготовить Windows/X11 upstream-compatible OSR, чтобы получить настоящий редактор внутри одного GET окна. Работа с WORM, GUP-2/GET-system, Nextcloud и свой мастер печати остаются в строго отдельных адаптерах.
