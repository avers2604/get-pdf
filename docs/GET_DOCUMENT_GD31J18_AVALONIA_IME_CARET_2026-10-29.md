# GD-31J.18 — Avalonia 12.1.4 IME-клиент, достоверная геометрия курсора и безопасный fallback Windows/X11

**Дата:** 29.10.2026. **База:** GD-31J.17 Draft PR №87. **Статус:** код и тесты подготовлены, но сборка и лаборатория **НЕ проводились** по решению пользователя. Не менять `main`, не включать GitHub Actions. К CEF-превью допускать только ранее утверждённый синтетический офлайн Linux/Wayland-стенд без корпоративных данных.

## 1. Фактически проверенные API, без предположений

- Проект `src/GetDocument.Desktop/GetDocument.Desktop.csproj` закрепляет **Avalonia.Desktop 12.1.4**.
- Официальные исходники `AvaloniaUI/Avalonia@12.1.4`:
  - `src/Avalonia.Base/Input/TextInput/TextInputMethodClient.cs` содержит **абстрактный класс** `TextInputMethodClient` (это НЕ интерфейс в текущей версии), обязательные `TextViewVisual`, `SupportsPreedit`, `SupportsSurroundingText`, `SurroundingText`, `CursorRectangle` и `Selection`; виртуальные `SetPreeditText(string?)` и `SetPreeditText(string?,int?)`, события через `RaiseCursorRectangleChanged()` и `RequestReset()`.
  - `InputElement.TextInputMethodClientRequested` просит `TextInputMethodClientRequestedEventArgs.Client`. **Переопределять события самостоятельно не нужно.**
  - `Avalonia.Rect` не имеет `Rect.Empty`; пустой прямоугольник в данном коде — `default(Rect)`.
- Закреплённый `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51` имеет CEF `OnPaint` только в Wayland OSR и подтверждённый `SendKeyEvent` для уже committed characters, однако **не даёт в текущем созданном GET OSR JSONL подтверждённых координат реального курсора документа**. По PNG preview и последней позиции мыши определить его достоверно нельзя. Нельзя выдавать их за `CursorRectangle`.

## 2. Добавленные компоненты

### `EuroOfficeImeCaretGeometry.cs` (Office, без UI)

- `EuroOfficeImeCaretPosition` содержит **только потенциальную** позицию курсора из будущего нативного редакторного telemetry API: `SessionId`, `TabId`, `FrameNumber`, физический размер области CEF, прямоугольник курсора в физических пикселях.
- `MapToPreview` проверяет точный session/tab/frame, ненулевую и конечную (`double.IsFinite`) геометрию, диапазоны размеров и нахождение курсора внутри документа. Прямоугольник преобразуется в логические координаты Avalonia-превью, учитывая **`Stretch.Uniform` и letterbox**. Этот метод не проверяет происхождение telemetry; создание DTO **не является аутентификацией**.
- `EuroOfficeCefOsrPlatformPolicy.IsSupported` разрешает OSR из pinned SDK только когда `operatingSystemLinux=true` и backend **строго** `QT_QPA_PLATFORM=wayland`. Windows, X11, offscreen и непроверенные среды получают `ProductionFallback="LibreOffice/UNO"`.

### `AvaloniaNativeImeLabClient.cs` (Desktop)

- Реальная реализация подтверждённого **Avalonia 12.1.4 `TextInputMethodClient`**, использует `Visual` тестового CEF-превью, `SupportsPreedit=true` (только локальное отображение факта preedit), `SupportsSurroundingText=false`, `SurroundingText=string.Empty`, `Selection=(0,0)`. Не может читать текст документа, скопировать выделение или управлять буфером обмена.
- `SetPreeditText(string?,int?)` фильтрует malformed BMP/control/bidi/format, ограничение из GD31J17, отбрасывает некорректное положение курсора; передаёт локальное значение only callback владельцу вкладки. **НЕ** пишет preedit в CEF. Пустой/null текст означает cancel.
- `SupplyCaretFromFutureAuthenticatedNativeAdapter(...)` проверяет сессию/поколение и координаты и сообщает `CursorRectangleChanged`. Этот метод **не вызывается текущим GET кодом**. У него в будущем должна быть доверенная проверка исходящего CEF telemetry от закреплённого дочернего процесса, а не просто DTO из произвольного JSON.
- `CanRegisterWithOsIme` остаётся `false`, пока одновременно нет активного разрешённого lab session и проверенной геометрии. При `Revoke` сбрасываются координаты, курсор и composition/IME Reset. Настоящий OS IME нельзя включить из-за одного `OnPaint` или факта нажатия мыши.

### `NativeOfficeDiagnosticPane.cs`

- Подписка на настоящее `_preview.TextInputMethodClientRequested`, но `e.Client` присваивается **только при** `ImeCommitLabApproved`, фокусе и `CanRegisterWithOsIme=true`. Пока нет достоверного CEF caret telemetry, новая подписка остаётся **безопасно неактивной**. Старое `TextInput` не получает новых прав.
- `OnLabImePreeditChanged` обновляет только локальное `EuroOfficeImeLabComposition` (не stdout, не clipboard), не выводит введённый текст в диагностическую строку; отмена/ошибка уничтожает только локальный буфер preedit.
- Смена вкладки или потеря CEF host вызывает `Revoke`, а удаление вкладки — `Dispose`. При этом прежние клики, фокус, поток кадров и штатный LibreOffice/UNO fallback не меняются.

## 3. Будущая лабораторная проверка

- `EuroOfficeImeCaretGeometryTests`: перевод в Uniform-превью, bounds/NaN, replay/stale tab/session, Windows/X11 fallback.
- `test_avalonia_ime_caret_contract.py`: проверка наличия конкретных методов Avalonia 12.1.4, **запрет** регистрации OS IME без настоящих координат, отсутствие получения курсора из PointerMoved, отсутствие unsafe HWND/XID и clipboard-данных.
- `scripts/office_acceptance.py`: добавлен шаг `avalonia-ime-caret-source-contract-tests`. Два обязательных ручных release-gate: `eurooffice-cef-authenticated-caret-ime-candidates` и `eurooffice-windows-x11-osr-or-uno-fallback`. PASS модульных тестов не должен автоматически снимать эти требования.
- **Запусков MSTest/.NET, Python unittest, CMake/Qt/CEF и реальной OS IME не было.** Код должен пройти компиляцию, функциональные, регрессионные, DPI-многомониторные, ИБ и E2E проверки позже. Заранее нельзя утверждать готовый IME, production или корректность редакторного caret/selection.

## 4. Windows 10/11 и Astra Linux / X11

`EuroOffice CEF SetAsChild` в pinned SDK не предоставляет проверенный кросспроцессный OSR для Windows или X11. Нельзя делать чужие `SetParent(HWND)`, `XReparentWindow`, `xcb_reparent_window` в качестве быстрого решения; это ломает изоляцию, фокус и DPI, создаёт риски отказов. До адаптации upstream (OSR + официальный IPC + sandbox + caret telemetry) **рабочий локальный движок — LibreOffice/UNO**, единая оболочка и вкладки ГЭТ остаются прежними.

## 5. Следующие задачи GD-31J.19

1. Реальный **native editor caret** callback из upstream/CEF с версионированным session/tab/frame/GUID, проверенной provenance процесса, bounded telemetry/TTL и без передачи surrounding text. Только после этого — разрешение кандидатов OS IME с точным caret rectangle.
2. Отдельный межплатформенный OSR/GL/texture путь для Windows/X11 без небезопасного окна стороннего процесса. Рабочая интеграция EuroOffice требует теста.
3. Настоящая CEF `ImeSetComposition/ImeCommitText` (или подтверждённый официальный upstream wrapper) для сложных языков и supplementary-plane Unicode. Пока доступно лишь отправление уже committed BMP из GD-31J.17.
4. Буфер обмена остаётся заблокирован. Будущий paste требует отдельной типизированной политики, user gesture, allowlist форматов и DLP, но **не** включён в GD-31J.18.
