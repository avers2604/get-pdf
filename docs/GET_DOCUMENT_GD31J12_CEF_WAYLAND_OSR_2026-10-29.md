# GD-31J.12 — реальный CEF OSR OnPaint в изолированном Wayland-стенде

**Дата:** 29 октября 2026. **Статус:** подготовлен код; сборки/тесты/лаборатория **отложены по решению пользователя**. База GD-31J.11 PR №80, Draft. Не менять `main`, не включать GitHub Actions и не устанавливать в рабочую систему ГЭТ.

## 1. Что удалось подтвердить по upstream

Изучены **точно закреплённые** исходники `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`:

- `ChromiumBasedEditors/lib/include/cefview.h`: `CCefViewWidgetImpl::OnPaint(const void*,int,int)`; `CCefView::SendMouseClickEvent`, `SendMouseMoveEvent`, `SendMouseWheelEvent`, `SendKeyEvent`.
- `ChromiumBasedEditors/lib/src/cefview.cpp`: `CAscClientHandler : CefRenderHandler`, `OnPaint(PET_VIEW,...)` вызывает `GetWidgetImpl()->OnPaint(buffer,width,height)`; popup `PET_POPUP` **пока не композитится**. `GetViewRect` рассчитывает физический размер по `cef_width * GetDeviceScaleFactor`, а `GetScreenInfo` для OSR сообщает `device_scale_factor=1.0`.
- `ChromiumBasedEditors/lib/qt_wrapper/include/qcefview.h` и `src/qcefview.cpp`: Qt `QCefView::OnPaint` уже реализован, в Wayland запоминает полученный BGRA кадр и выдаёт его в GL-overlay с отложенным repaint. Qt также содержит обработчики `mousePressEvent`, `mouseMoveEvent`, `wheelEvent`, `keyPressEvent`, `keyReleaseEvent` и `inputMethodEvent`.
- `ChromiumBasedEditors/lib/src/cefapplication.cpp`: при `QT_QPA_PLATFORM=wayland` CEF включает `windowless_rendering_enabled=true`. На **Windows и X11** текущая реализация вызывает `SetAsChild` (native child window), а не OSR. Поэтому **нельзя утверждать, что OnPaint на этих системах выдаст кадры**. Для Windows/X11 потребуется отдельный технически подтверждённый путь либо upstream изменение windowless режима.
- Без `CEF_USE_SANDBOX` в upstream может действовать `settings.no_sandbox=true`. **Флаг лаборатории и закрытая сеть сами по себе не являются sandbox.** Требуется отдельная офлайн-VM/ОС-изоляция без доступа к служебным документам, принтерам, сети, SSO или WORM.

## 2. Изменённый код

### C++: реальный CEF OnPaint для тестовых OOXML

`probes/eurooffice-desktop/cef_lab/cef_staged_open.cpp`:
- Старый `--open-staged-lab` и проверка трёх синтетических DOCX/XLSX/PPTX по SHA-256 **сохранены**.
- Новый **только с компиляторным флагом** режим `--open-staged-wayland-osr-lab <staging-root> <fixture-name>`: отказывает, если `QT_QPA_PLATFORM` не равен `wayland` или Qt запустился не под Wayland.
- `SyntheticEditorView : QCefView` теперь переопределяет **настоящий** `OnPaint(const void*,int,int)`. Сначала вызывает `QCefView::OnPaint`, чтобы не ломать существующий Wayland presenter; затем один раз создаёт маленькую независимую копию буфера BGRA в `QImage`. Не запускает внутри CEF callback вложенный Qt event loop, не пишет файлы, не обращается к `QWidget*` другого процесса.
- Копирование ограничено 4096×4096 и максимумом ~4.2 млн пикселей исходного изображения. Диагностический кадр уменьшается до **256×144** с соблюдением пропорций. Запись между CEF callback и Qt завершением защищена `QMutex` и атомарным флагом; не накапливает последовательность кадров.
- Для лабораторного Wayland режима запускается `QTimer` с периодом 10 мс, который вызывает **подтверждённый upstream API** `CApplicationCEF::DoMessageLoopEvent()` из Qt event loop. Внутри `OnPaint` этот метод **не вызывается**.
- После завершения event loop, но **до освобождения `QCefView`**, кадр копируется в память. После остановки менеджера создаётся отдельный `getdoc-cef-osr-lab/1` JSON receipt, `GD31J12_OSR_CAPTURE_UNVERIFIED`, необязательный PNG/Base64/sha256 и флаги `documentVerified=false`, `saveVerified=false`, `pdfVerified=false`, `printApproved=false`, `laboratoryOnly=true`.
- Если CEF не прислал кадр либо PNG превысил 48 KiB, отчёт отражает отсутствие экспортированного кадра. **Даже наличие кадра не доказывает правильное открытие или редактирование OOXML.** Первый `OnPaint` может содержать пустую страницу/загрузочный экран.

### CMake: выключено по умолчанию

`cef_lab/CMakeLists.txt`: дополнительная опция `GETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB=OFF`. Требует уже разрешённого `GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB=ON`, следовательно GD-31J.4/3 и подтверждённой изоляции; не допускается конфигурировать вне Linux. Определение `GETDOC_GD31J12_WAYLAND_OSR_CAPTURE=1` передаётся только тестовому target; в основной проект ничего не линкуется.

### C#: квитанция и подготовка ввода

- `EuroOfficeNativeOsrLabReceipt.cs`: отдельный строгий парсер именно **лабораторной CEF OSR** квитанции. Проверяет схему, источник, обязательные false-флаги, отсутствие неизвестных/дублированных полей, размер, сигнатуру PNG/IHDR, SHA-256. Память с кадром выдаётся только через защитную копию. `ApprovedForGetTab=false`, `OfficeDocumentVerified=false`, `PrintApproved=false`. **Не подключён** к штатному `EuroOfficeNativeTabIpc` и не позволяет открыть документ в пользовательской вкладке.
- `EuroOfficeOsrInputMapping.cs`: чистая геометрическая функция `MapPointer` для преобразования DIP координат Avalonia в физические пиксели CEF Wayland OSR с DPR/HiDPI 100–200% и ограничениями размеров. Она **не отправляет** события пользователю или браузеру. Ввод клавиатуры/IME и clipboard пока fail-closed, `NotSupportedException`. Местный `QCefView` upstream уже обрабатывает обычные Qt mouse/key/IME события внутри своей отдельной лабораторной программы; это **не** означает, что ввод из основной вкладки Avalonia туда переадресуется.

## 3. Подготовленные тесты — проверим ПОТОМ

- `test_osr_capture_contract.py`: проверка точного `QCefView::OnPaint`, вызова base, `QMutex`, запрета Windows/X11 и реальных документов, лабораторных false-флагов и CMake default OFF.
- `EuroOfficeNativeOsrLabReceiptTests`: валидный/отсутствующий OSR кадр, ложные success-флаги, чужой источник, неверный SHA, размеры и дубликаты.
- `EuroOfficeOsrInputMappingTests`: координаты при DPR 1.0/1.25/1.5/2.0, выход за границы, NaN/неверный DPI, запрет неподтверждённых keyboard/clipboard команд.
- `scripts/office_acceptance.py`: будущий `cef-wayland-osr-source-contract-tests` перед `dotnet restore`. **Автоматического включения native CEF/OSR в CTest нет**, так как CEF может быть без sandbox. Реальный запуск — только после отдельной ИБ-подготовки и ручного одобрения.

**Сборка Qt/CMake/CEF, Python tests, .NET/MSTest, X11/Wayland/Windows GUI, multi-monitor/DPI и реальный запуск не выполнены в этом этапе.** Квитанция ещё не испытана на настоящем runtime.

## 4. Отложенная ручная лаборатория (НЕ для рабочих ПК)

```bash
# Только на отдельной одобренной офлайн-VM с уже закреплёнными
# исходниками EuroOffice, CEF resources/helper, Qt6 и native SDK.
python probes/eurooffice-desktop/prepare_office_fixtures.py \
  --out /approved/empty/gd31j6-fixtures

cmake -S probes/eurooffice-desktop/cef_lab -B artifacts/gd31j12 \
  -DGETDOC_GD31J3_ENABLE_CEF_LAB=ON \
  -DGETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB=ON \
  -DGETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB=ON \
  -DGETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB=ON \
  -DGETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED=ON \
  -DEUROOFFICE_DESKTOP_TREE=/approved/DesktopEditors \
  -DEUROOFFICE_RUNTIME_LIB_DIR=/approved/eurooffice-runtime/lib

cmake --build artifacts/gd31j12 --config Release

# ONLY AFTER SECURITY REVIEW AND OS-ISOLATION; no real files.
QT_QPA_PLATFORM=wayland \
  artifacts/gd31j12/getdoc-eurooffice-cef-staged-open-lab \
  --open-staged-wayland-osr-lab \
  /approved/empty/gd31j6-fixtures gd31j6_writer.docx
```

Ожидаемый по конструкции отчёт имеет источник `cef-osr-wayland-synthetic-only`; положительного результата **не обещаем**, пока не выполнены настоящие испытания.

## 5. Что дальше: GD-31J.13

**Адаптер вывода настоящих CEF кадров внутри вкладки** должен подключать данные только от отдельно проверенного, process-bound CEF OSR host, не доверяя произвольному JSON stdout/флагу `OnPaint`. Необходимо сопоставлять tab/session/generation, подтверждать происхождение процесса, избегать TOCTOU, обеспечить ограниченное streaming/backpressure, пропускную способность без Base64, popup/compositing, мышь/клавиатуру/IME/clipboard, масштаб и фокус. На Windows/X11 нужен отдельный проверенный механизм OSR, поскольку в текущей версии EuroOffice там работает native-child. **Не передавать чужие HWND/XID или `QWidget*` между процессами.**

Функции записи новых редакций, GUP-2/GET-system WORM/CAS, Nextcloud collaborative editing и экспорт PDF в фирменный мастер печати остаются закрытыми. Основной локальный LibreOffice/UNO — текущий fallback.
