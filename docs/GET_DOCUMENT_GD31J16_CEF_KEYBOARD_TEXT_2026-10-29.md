# GD-31J.16 — лабораторная клавиатурная навигация и одиночные подтверждённые символы

**29.10.2026.** Stacked поверх GD-31J.15 PR #85. **Сборка, запуск MSTest/Python/CMake, CEF и проверка ввода на Windows/Astra отложены по решению пользователя.** Код не предназначен для обычного GET launcher/production. `main` и GitHub Actions не трогать.

## Контрольная точка upstream EuroOffice

Проверены исходники закреплённого `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`:

- `ChromiumBasedEditors/lib/include/cefview.h`: `CCefView::SendKeyEvent(int type,int key,int modifiers,const std::wstring& character)`.
- `src/cefview.cpp`: действительно формируется `CefKeyEvent`: `type`, `windows_key_code`, `modifiers`, `character/unmodified_character` и `CefBrowserHost::SendKeyEvent`. `type=0` — `KEYEVENT_RAWKEYDOWN`, `2` — `KEYEVENT_KEYUP`, `3` — `KEYEVENT_CHAR`.
- `qt_wrapper/src/qcefview.cpp`: для Wayland `keyPressEvent/keyReleaseEvent` передаёт key down/char/key up; в `inputMethodEvent` уже подтверждённая строка Unicode проходит через тройку `0/3/2` при нулевом virtual key code — чтобы специальный символ не воспринимался как VK, см. upstream issue #133. Комментарий upstream **прямо указывает**, что полноценная композиция/preedit IME не реализована таким способом, нужен отдельный `ImeCommitText` wrapper. Предположения о полной поддержке CJK/IME не допускаются.
- При `Ctrl+Tab` и `Alt+F4` upstream `OnPreKeyEvent` содержит специальную обработку. **Поэтому эти сочетания не проходят новый IPC**: ими продолжает управлять ГЭТ/ОС.

## Новая отдельная лабораторная опция

`GETDOC_GD31J16_ENABLE_KEYBOARD_LAB=OFF` по умолчанию. Требует `GETDOC_GD31J15_ENABLE_CLICK_LAB=ON`, все предыдущие разрешения, pinned исходники/CEF и отдельную утверждённую изолированную Wayland VM. Отдельный исполняемый режим:

```text
--open-staged-wayland-keyboard-lab <synthetic-staging-root>
    gd31j6_writer.docx|gd31j6_calc.xlsx|gd31j6_impress.pptx
    <session-guid32> <tab-guid32>
```

`--open-staged-wayland-click-lab`, `--open-staged-wayland-input-lab` и `--open-staged-wayland-stream-lab` НЕ приобретают клавиатурные права. Включение флага компиляции не включает обычный интерфейс GET: требуется дополнительный `keyboardLabApproved=true` в **internal** `MainWindow.StartNativeCefOsrLabStreamAsync` совместно с `pointerInputLabApproved`, `pointerClickLabApproved` и `labApproved`. Программа проверяет SHA256 pinned CEF executable и только три синтетических файла, стандартный `Process` не запускается из production launcher.

## Контракт `getdoc-cef-osr-input/1`

Строгий JSONL в private stdin ранее запущенного процесса, точный `sessionId`, `tabId`, `sequence` и только два новых типа сообщений:

1. `nav-key`: ключ из **закрытого списка** `ArrowLeft/Right/Up/Down`, `Home`, `End`, `PageUp`, `PageDown`, `Escape`, `Backspace`, `Delete`, `Enter`, `Tab`, флаг `shift:boolean`; никаких произвольных VK, Ctrl, Alt, Meta/Win, F-клавиш или аккордов. Qt handler маппит каждое имя на фиксированный Windows VK и атомарно шлёт в CEF пару `SendKeyEvent(0,...)` → `SendKeyEvent(2,...)`. Наличие вызовов не подтверждает реальную реакцию редактора — ACK из CEF ещё нет.
2. `text-char`: **ровно один** Unicode BMP `codePoint:int`, не строка, не paste, не суррогат, не C0/C1/DEL, не bidi override/format control. Это уже подтверждённый символ из Avalonia `TextInput`, не IME preedit. Qt создаёт `std::wstring` с двумя единицами символа и нулевым native scan-code, передаёт `SendKeyEvent(0,0,0,...)`, `SendKeyEvent(3,0,0,...)`, `SendKeyEvent(2,0,0,...)`, по upstream pattern. Кириллица в BMP допустима; полноценная русская раскладка/IME E2E пока не проверена.

Оба типа требуют ранее установленного focus и **отпущенной левой кнопки**. Для всего IPC остаются лимиты GD-31J.14: 4096 символов на команду, 128 сообщений на сеанс, 30 событий/с, fail-closed при ошибке, replay, изменении ID или неизвестном поле. Нет клавиатурных логов, пользовательских путей, данных документа и передачи сети. *Сам код символа может быть чувствительным — разрешено исключительно для синтетических документов на VM без пользовательской информации*.

## Avalonia

- В `NativeOfficeDiagnosticPane` тестовое превью делает себя `Focusable`. После явного клика мышью и успешного `SendFocusAsync(true)` UI вызывает `Focus()` на preview, только в keyboard-lab режиме.
- `KeyDown` отправляет **лишь** разрешённые navigation keys, опционально Shift. `Ctrl/Alt/Meta` остаются у операционной системы и оболочки GET. `KeyUp` отслеживает окончание зарезервированного сочетания; `TextInput` не отправляется, если зарезервированные модификаторы активны.
- `TextInput` допускает ровно один уже подтверждённый UTF-16 BMP `char`. Несколько символов, суррогатные пары, контрольные и Unicode-format символы не передаются в нативный процесс. Отдельный `IME preedit`/candidate UI пока отсутствует, clipboard отключён.
- Обработчики используют прежнюю `_labGestureGate` и `EuroOfficeOsrLabInputChannel`, без прямых вызовов CEF из UI. При переключении вкладки фокус снимается; при ошибке input канал отзывается, отключается только соответствующий лабораторный процесс по его CancellationToken.

## Что записано, но НЕ проверено

- `EuroOfficeOsrLabKeyboardTests.cs`: отказ без разрешения, независимый CLI, навигация/кириллица, запрет спецклавиш и Unicode format, отсутствие clipboard/IME, требования focus и release мыши.
- `EuroOfficeOsrStreamProcessPolicyTests.cs`: новый CLI из SHA-approved бинарника, старые режимы не изменяются.
- `test_osr_keyboard_contract.py`: gate OFF, разрешённые CEF APIs, отсутствие generic IME/clipboard, точный UI whitelist.
- `scripts/office_acceptance.py`: новый будущий локальный этап `cef-osr-keyboard-source-contract-tests`.

**Ни одна сборка/проверка не запускалась по указанию пользователя.** Наличие структурных проверок и тестов в коде не означает их PASS. Runtime ввод текста, IME, DPI, проверка Ctrl+Tab/W/P/Alt+F4 и отсутствие утечек требуют полноценной локальной лаборатории на Windows/Astra, но этот CEF стенд пока Wayland-only и может работать с `no_sandbox=true` без ОС-песочницы.

## Следующий этап GD-31J.17

Сначала определить и внедрить формальный интерфейс IME-preedit/commit/cancel с ограничением размеров, только после подтверждения API в pinned CEF; отдельно согласовать буфер обмена, запрет скрытого чтения файлов и безопасный обмен лишь одобренными типами контента. Параллельно нужен кроссплатформенный CEF OSR (Windows/X11) без произвольного HWND/XID reparenting. До реального доказательства настоящего редактирования штатным движком остаётся LibreOffice/UNO. Запись в GET-system/GUP-2 CAS/WORM, Nextcloud совместное редактирование и экспорт PDF в собственный мастер печати не активированы этим лабораторным режимом.
