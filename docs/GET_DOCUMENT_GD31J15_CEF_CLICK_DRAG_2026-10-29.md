# GD-31J.15 — лабораторные клики, двойной клик и выделение мышью в EuroOffice CEF OSR

**Дата:** 29.10.2026. **Базовый Draft:** GD-31J.14 PR №84. **Только подготовка исходников; сборки, тесты и GUI-испытания отложены пользователем.** Изменения не предназначены для `main`, установщика и рабочей сети.

## 1. Назначение и точная граница

Этап GD-31J.14 создал строго ограниченный Wayland-only IPC для `focus`, `move`, `wheel` в настоящем CEF `QCefView` на **трёх синтетических OOXML-файлах**. На том этапе клик намеренно оставался запрещённым.

GD-31J.15 добавляет **отдельно разрешаемое** нажатие/отпускание левой кнопки и нормализованное перетаскивание (для будущего выделения текста). Наличие вызова `SendMouseClickEvent` **не является доказательством**, что редактор правильно выделил ячейку/текст, открыл документ, изменил или сохранил его. Настоящая интерактивность требует отдельного CEF/Qt/Avalonia E2E на изолированной VM.

### Подтверждение реального upstream API

`Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`:

- `ChromiumBasedEditors/lib/include/cefview.h`: `CCefView::SendMouseClickEvent(int x,int y,int button,bool mouseUp,int modifiers,int clickCount)`, `SendMouseMoveEvent`, `SendMouseWheelEvent`, `focus(bool)`.
- `ChromiumBasedEditors/lib/src/cefview.cpp`: `SendMouseClickEvent` делегирует в `CefBrowserHost::SendMouseClickEvent`. `button=1` — левая кнопка, `mouseUp=false/true`, `clickCount=1/2`.
- `ChromiumBasedEditors/lib/qt_wrapper/src/qcefview.cpp`: при удержании `Qt::LeftButton` штатный `GetCefModifiers` устанавливает `EVENTFLAG_LEFT_MOUSE_BUTTON=1 << 4`. Повторное `mouseDoubleClickEvent` не передаётся отдельно — в Wayland вторая физическая `MousePress` уже учитывается через `clickCount=2`.
- Все координаты продолжают преобразовываться из относительных координат превью во **физические пиксели** `QCefView` с учётом реального `devicePixelRatioF()`. Нативное окно чужого процесса по-прежнему не присоединяется к `Avalonia`.

## 2. Что изменено

### Новые сборочный флаг и CLI

`probes/eurooffice-desktop/cef_lab/CMakeLists.txt`:

- `GETDOC_GD31J15_ENABLE_CLICK_LAB=OFF` по умолчанию. Для включения требуется также `GETDOC_GD31J14_ENABLE_INPUT_LAB=ON`, предыдущие CEF/OSR лабораторные opt-in и подтверждённая отдельная офлайн-изоляция.
- Только в экспериментальном target определяется `GETDOC_GD31J15_CLICK_LAB=1`.
- Ранее существующие `--open-staged-wayland-stream-lab` (только PNG) и `--open-staged-wayland-input-lab` (фокус/мышь/прокрутка, БЕЗ кликов) остаются без изменений по смыслу.
- Новый `--open-staged-wayland-click-lab <staging> <fixture> <sessionGUID32> <tabGUID32>` отказывает без отдельного compile-time gate. Работает **только Linux/Wayland** на лабораторном ПК с синтетическими файлами и привязанными хешами.

### Native CEF event pump

`cef_lab/osr_input.h/.cpp`: отдельный конструктор `OsrInputPump(..., clicksApproved)`. Разрешены только дополнительное `left-down` и `left-up`, причём:

1. Лабораторный режим кликов и действующий фокус обязательны; только левая кнопка.
2. `left-down` возможен только при отпущенной кнопке и `clickCount=1|2`. Повторный down отклоняется.
3. `left-up` возможен только после down. На release используется сохранённый счётчик клика, чтобы CEF видел согласованные `(down,2)/(up,2)`.
4. При перемещении между down и up отправляется `SendMouseMoveEvent` с `EVENTFLAG_LEFT_MOUSE_BUTTON=1<<4`, что позволяет движку трактовать движение как выделение, если документ действительно отображён.
5. Прокрутка и переход фокуса в false при удержании кнопки запрещены: вызывающая оболочка обязана выполнить release. Любой неверный JSON, session/tab/sequence, replay или команда прекращает лабораторный input poller, без автоматического повторения.
6. Нет `SendKeyEvent`, `ExecuteJavaScript`, clipboard, drag-and-drop файлов, произвольных путей, URL, сетевых вызовов, печати и сохранения.

Весь ввод обрабатывается из `QTimer` **на Qt owner thread**, не в `OnPaint`. Сохраняются лимиты 4096 байт на сообщение, 128 команд на сеанс и 30/с.

### .NET

- `EuroOfficeOsrLabInputChannel` получает отдельный `clickLabApproved=false`; появились `SendLeftButtonDownAsync`, `SendLeftButtonUpAsync`, `ReleaseHeldButtonAsync`. Состояния `focused`/кнопка отслеживаются при подтверждённой записи в stdout? **Нет:** только после успешной локальной записи в stdin, без ACK нативного CEF. Команда в канале не гарантирует выполнения CEF.
- Поля новых сообщений `command=left-down|left-up`, `sessionId`, `tabId`, `sequence`, `protocol=getdoc-cef-osr-input/1`, `x10000/y10000`; `left-down` дополнительно содержит `clickCount=1|2`. Никаких документных данных и текстовых параметров.
- Канал `fail-closed`: ошибка или частичная запись блокирует дальнейшую отправку во избежание повторения небезопасного состояния.
- `EuroOfficeOsrStreamProcessLab.CreateClickStartInfo` выбирает **отдельный** CLI и прежнюю проверку локального SHA бинарника/каталога, синтетического fixture+manifest. `RunInputLabAsync(..., clickLabApproved=false)` создаёт input channel с новым разрешением только для этого режима.
- `MainWindow.StartNativeCefOsrLabStreamAsync` имеет два явных флага: `pointerInputLabApproved=false` и `pointerClickLabApproved=false`. Клик-режим нельзя включить без общего разрешения на ввод. Обычный launcher/пользовательская функция открытия документа не вызывают эти internal методы.

### Avalonia

- `NativeOfficeDiagnosticPane` отличает старый режим «нажать для фокуса» от отдельно разрешённого клика.
- В click-lab режиме `PointerPressed/PointerReleased` управляют строго парным `left-down/left-up`; `PointerMoved` между ними остаётся в том же CEF сеансе. События сериализованы внутренним `SemaphoreSlim`, чтобы быстрый press/release не нарушал их порядок.
- `PointerCaptureLost`, уход за пределы изображения и переключение вкладок инициируют отпускание перед `focus=false`. При смене таба приоритет имеет сброс ввода; ни один CEF input не становится автоматической операцией с файлом.
- Локальный double-click определяется только для повторного нажатия в пределах 450 мс и небольшого радиуса; передаётся `clickCount=2` **в одной паре** сообщений (без дублирующего `mouseDoubleClickEvent`).
- Для изображения с `Stretch.Uniform` пропуски/letterbox **не** превращаются в координаты документа. Вторая половина режима (клавиатура/IME) **не подключена**.

## 3. Тесты подготовлены, не запускались

- `EuroOfficeOsrLabPointerClickTests.cs`: отдельное opt-in, сопряжённые down/up, выделение, double click, невозможность повторного down/up, недопустимый clickCount/координаты, отсутствие автоматического focus loss при удержании, блокировка клавиатуры/clipboard.
- `EuroOfficeOsrStreamProcessPolicyTests.cs`: отдельный флаг CLI `--open-staged-wayland-click-lab`, ранее существующие stream/input пути сохраняются, SHA-policy не меняется.
- `test_osr_click_contract.py`: CMake OFF, отделение нативного обработчика и разрешённых CEF вызовов, запрет KeyEvent/clipboard, подготовка освобождения кнопки на UI.
- `scripts/office_acceptance.py`: новый `cef-osr-click-source-contract-tests`, в будущей локальной лабораторной приёмке до restore.

**В этом этапе не выполнялись Python unittest, MSTest/.NET, CMake, Qt/CEF, запуск приложения, испытания двойного клика, DPI и многомониторности.** Поэтому не утверждать работающую selection/редактирование или готовность.

## 4. Ограничения и следующий этап

**Главные остающиеся задачи:** настоящий текстовый ввод и IME, clipboard с политикой утечек, drag selection fidelity, режимы доступности и масштаба, popup/context menu, ускоренный transport, процессы/песочница, Windows/X11 OSR и сохранение редакций через GET-system/GUP-2 CAS/WORM. На Windows/X11 закреплённый upstream использует `SetAsChild`; перенос произвольного HWND/XID в Avalonia запрещён. Сейчас штатный fallback — LibreOffice/UNO.

**GD-31J.16:** отдельный whitelist-проект для keyboard navigation и полноценной IME композиции; различать клавиши управления и текст, не передавать произвольную строку в CEF до утверждения ИБ. Приоритет — кроссплатформенный единый дизайн ГЭТ и сохранение текущих рабочих модулей.
