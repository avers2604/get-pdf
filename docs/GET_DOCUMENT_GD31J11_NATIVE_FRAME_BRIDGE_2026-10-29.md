# GD-31J.11 — переносимый диагностический raster bridge Qt → IPC → Avalonia

**Дата:** 29.10.2026. **Статус:** функциональный код в Draft поверх GD-31J.10 PR №79; **сборка, тесты и лаборатория отложены** по решению пользователя. Никакого merge в `main` и GitHub Actions.

## Цель

Прямое встраивание чужого нативного окна `QCefView` (через Win32 `HWND`, Linux XID / `QWidget*` / XReparentWindow) без подтверждённого контракта жизненного цикла, безопасного владельца окна, процесса и фокусировки не является переносимой реализацией «одна вкладка ГЭТ». Особенно опасен Wayland/Astra Linux, где чужой процесс нельзя безопасно `reparent` без соответствующего compositor-protocol.

Поэтому в рамках GD-31J.11 создана **первая настоящая цепочка переноса изображения между отдельными процессами**, без пересылки OS window handles. Она предназначена **только для собственноручно нарисованного Qt-процессом тестового изображения**, а не для содержимого реального EuroOffice, пока не будут подтверждены CEF OSR (off-screen rendering) API и изоляция.

```text
getdoc-eurooffice-qt-probe --ipc-probe [QCoreApplication, QtGui QImage/QPainter]
    → hello / attach-tab / request-frame / close
    → оригинальный Qt-only PNG 320×180, Base64, SHA-256
    → stdin/stdout JSONL (один process, текущие sessionId/tabId/sequence)
    → EuroOfficeNativeTabIpc.RequestPreviewFrameAsync
    → EuroOfficeNativeDiagnosticFrame.Parse (проверка envelope/PNG/digest)
    → EuroOfficeNativeSessionCoordinator.RefreshDiagnosticFrameAsync
    → AvaloniaNativeSessionView / UI Dispatcher
    → MainWindow.ShowNativeDiagnosticFrame
    → NativeOfficeDiagnosticPane.Image в существующем TabControl
```

**Это тестовый pixel bridge, а НЕ готовый EuroOffice editor.** CEF здесь не инициализируется, `OpenLocalFile` не вызывается, реальные документы не передаются. Скриншоты экрана/пользователя не захватываются; кадр искусственно строится через Qt `QPainter`.

## Реализация

### Qt host

`probes/eurooffice-desktop/native_host/ipc_probe.cpp`: новая IPC команда `request-frame` разрешена **только после** `hello` и `attach-tab`, с последовательным `sequence` и текущим `sessionId/tabId`. Программа рисует оригинальный Qt-only PNG размером 320×180, кодирует в Base64, передаёт SHA-256 и маркирует источник `source=qt-painter-test-only`. Никакой файл не открывается и не сохраняется, нет сети, CEF, печати и оконного реparenting. Сам diagnostic IPC, как и раньше, закрывается через `close`, максимум 128 команд.

В Qt остаётся прежняя каноническая сериализация JSON и отказ от дубликатов/неизвестных полей. Полезная нагрузка ограничена 48 KiB, ASCII JSON; C# приёмник допускает максимум 65 536 символов на *ответ кадра*. Обычные команды и ACK по-прежнему ограничены 4096 символами.

### C# transport / Office core

- `EuroOfficeNativeDiagnosticFrame.cs`: строгая схема ответа, ограничение размера/разрешения, точный `tabId/sessionId/sequence`, `source=qt-painter-test-only`, Base64, PNG-сигнатура/IHDR, SHA-256. Отвергает поддельные CEF frames. Флаги `CefRendered`, `OfficeDocumentVerified`, `RevisionCommitted`, `PrintApproved` всегда `false`.
- `EuroOfficeNativeTabIpc.cs`: метод `RequestPreviewFrameAsync` внутри существующей блокировки сообщений, 5-секундного timeout и терминального fail-closed. Приём PNG не создаёт права открыть/сохранить/распечатать файл.
- `EuroOfficeNativeIpcProcessSession`: проксирует запрос кадра в уже запущенный и hash-проверенный Qt-only хост. Не включает CEF.
- `EuroOfficeNativeSessionCoordinator.RefreshDiagnosticFrameAsync`: проверяет идентификаторы и объявленные права, затем отдаёт кадр `IEuroOfficeNativeSessionView.PreviewFrameAsync`. Ошибка превращается в потерю соединения/ошибку вкладки, а не в подтверждение документа.

### Avalonia GET UI

- `AvaloniaNativeSessionView`: доставляет кадр только в UI Dispatcher.
- `MainWindow.ShowNativeDiagnosticFrame`: проверяет текущую вкладку, sessionId/tabId и её статус; не обращается к `DocumentWorkspace`, LibreOffice или PDF.
- `NativeOfficeDiagnosticPane`: отображает PNG через `Bitmap(Stream)` внутри **существующей** вкладки `TabControl`. Сверяет пиксельный размер после декодирования, освобождает предыдущую bitmap при обновлении и скрывает/освобождает изображение при потере хоста. Вверху явно написано: **«Доставлен тестовый кадр Qt (без CEF и офисного документа)»**.
- `MainWindow.StartNativeLabSessionAsync` (по-прежнему `internal`, НЕ вызывается из пользовательского launcher) сразу после успешного attach просит один тестовый кадр; это не циклическое видео, не редакторное окно, не webview и не CEF OSR.

## Подготовленные испытания — запуск ПОЗЖЕ

- `EuroOfficeNativeDiagnosticFrameTests`: правильные поля и false-флаги, неправильный session/tab/sequence, несоответствие SHA/PNG, размеры/лимиты, дублированные и неизвестные поля.
- `EuroOfficeNativeTabIpcTests`: последовательность hello→attach→request-frame→close, frame в нужном session.
- `EuroOfficeNativeSessionCoordinatorTests`: передача frame в один tab, отказ от чужого frame, fault и освобождение транспорта.
- `probes/eurooffice-desktop/test_qt_ipc.py`: локальный тест настоящего Qt-бинарника с проверкой подписи PNG, исходной геометрии, SHA-256 и ID.
- `scripts/tests/test_native_frame_shell_contract.py`: source-only ограничения без Qt, CEF или P/Invoke.
- `scripts/office_acceptance.py` включает source-only контракт, а ранее существующий `gd31j8_qt_tab_ipc` CTest запускает обновлённый `test_qt_ipc.py` при финальной приёмке.

**В этом сеансе не запускались** CMake/Qt/Cef, .NET/MSTest, Python unittest, Windows/Astra Desktop, реальные два монитора. Статическая проверка исходников НЕ доказывает компиляцию и исправность DPI/focus/частоту кадров.

## Ограничения и дальнейшие шаги GD-31J.12

**Что пока отсутствует:**

1. Реальная картинка **из EuroOffice CEF**, подтверждённая поддержка OSR (`OnPaint`, popup/drag/caret/focus/IME/scroll/dpi) и её упаковка в безопасный frames/ring buffer.
2. Достоверный, изолированный native editor процесс: CEF может быть собран с `no_sandbox=true` без `CEF_USE_SANDBOX` в закреплённом upstream. Установить secure OS sandbox/системные ограничения и подтвердить лицензию прежде, чем передавать корпоративные документы.
3. Быстрая передача кадров без Base64/JSON (поток PNG 320×180 годится только для разовой диагностики). Для реального editor потребуется ограниченный shared-memory transport с аутентификацией peer, generation/frame counter, backpressure и безопасным pixel-format validation. Нельзя использовать HWND/XID неизвестного источника.
4. Маршрутизация реального ввода мыши, клавиатуры, IME и текстового выделения, clipboard/accessibility, high DPI, много мониторов, Win10/11/Astra Wayland.
5. Реальный OOXML roundtrip, серверное совместное редактирование, WORM/CAS commit, PDF export и собственный мастер печати ГЭТ. Всё это остаётся отдельными закрытыми адаптерами.
6. Измерения производительности, accessibility, runtime E2E на старых компьютерах, release-ready security check.

**Текущий LibreOffice/UNO остаётся штатным офлайн fallback.** Архитектура «своё оформление ГЭТ поверх готового функционального движка» сохраняется; демонстрационный кадр ни в каком случае не должен показываться как действующий офисный редактор.
