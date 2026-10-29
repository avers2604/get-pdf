# GD-31J.26 — строго ограниченный нативный Save As EuroOffice Desktop

**Дата:** 29.10.2026. **Репозиторий:** `avers2604/get-pdf`, база Draft PR #97 GD-31J.25. **Статус:** код и тесты подготовлены; реальные сборка, .NET/Python тесты и CEF/Wayland VM **не запускались** по ранее утверждённому порядку работ. Никакого production-ready заявления.

## Проверенный API закреплённого EuroOffice SDK

Использована реально существующая цепочка `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`:

1. `ChromiumBasedEditors/lib/src/cefwrapper/client_renderer_wrapper.cpp`: V8 `LocalFileSave(params, ...)` отправляет IPC `onlocaldocument_onsavestart`.
2. `ChromiumBasedEditors/lib/src/cefview.cpp`: если `params` содержит `saveas=true`, обработчик инициирует `ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE` (7008), содержащий `CAscLocalSaveFileDialog` с ID редактора, исходным форматом и списком допустимых форматов.
3. `CCefView::Apply` обрабатывает `ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE_PATH` (7009), извлекает `CAscLocalSaveFileDialog::get_Path/get_FileType`, затем вызывает `LocalFile_SaveStart(path, format)` (кроме особого режима crypto, который **не разрешён** в синтетической лаборатории).
4. `ASC_MENU_EVENT_TYPE_CEF_ONSAVE` (3002) потенциально сообщает об окончании native Save; это не гарантия сохранности/совместимости OOXML и не производственная квитанция.

Для GD-31J.26 вызывается **одна** фиксированная форма `window.AscDesktopEditor.LocalFileSave('saveas=true')` в editor iframe через уже существующий `CCefView::ExecuteInAllFrames(..., false)`. Этот V8 метод задокументирован *кодом* pinned SDK, но правильность его выполнения в реальной сборке **ещё предстоит проверить**. Никаких пользовательских JS-строк, путей или сетевых адресов не принимается.

## Сделано

- Новый CMake-флаг `GETDOC_GD31J26_ENABLE_GUARDED_SAVEAS_LAB=OFF`; требует подтверждённых предыдущих GD-31J.25/24/23/22 gates, отдельно вручную изменённый pinned SDK с GD-31J.24 bridge, закреплённые SHA библиотек и host, отдельную изолированную offline Linux/Wayland VM.
- Команда `--open-staged-guarded-saveas-lab <staging> <synthetic_fixture> <session32> <tab32> <format>`, где три fixture из локального signed/attested manifest, а `format` только **соответствующий источнику OOXML**: writer → `docx`, calc → `xlsx`, impress → `pptx`. Преобразование DOCX → PDF, DOCX → XLSX и выбор обычного пользовательского файла **не реализованы** — запрещены.
- Ещё до запуска CEF создаётся копия проверенного исходного synthetic fixture в `QTemporaryDir/work/<fixture>`; только копия открывается как native editor. Эталон остаётся отдельным и контролируется SHA-256 до/после.
- CEF `SyntheticEventObserver` получает типизированный callback 7008, только если совпали `get_SenderId` и `CAscLocalSaveFileDialog::get_Id/get_FileType` с ожидаемым редактором; при непустом `SupportFormats` необходим соответствующий формат. Дубликаты запросов или неверные ID/format помечаются как отказ.
- Перед началом диалога необходимо получить валидные лабораторные признаки `bindingSeen`, `conversionObserved`, `onContentReady`, `modified` и отсутствие `IsSaveLocked`. Следом Qt owner-thread отвечает **ровно одним** `ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE_PATH` (7009) с заранее сформированным назначением `<QTemporaryDir>/work/gd31j26-private-saveas.<ext>` — без пользовательских путей и перезаписи существующей цели.
- После native callback `OnSave` и завершения CEF проверяются эталон, отсутствие дополнительных Save As запросов, выбранный путь и SHA/размер файла. **Только если** контроль пройден, новый результат копируется в `<staging>/gd31j26-saveas-candidate-<session32>.<ext>` через `QFile::copy`, запрещающий замену существующего файла.
- Сессия выдаёт ровно одну квитанцию `getdoc-native-saveas-private-lab/1` с session/tab/fixture, выбранным форматом, source SHA до/после, типизированным запросом, подтверждением назначенного пути, callback, запретом cancel и размером+SHA карантинного результата. Не содержит абсолютных путей/контента документа. Поля `documentVerified/saveVerified/pdfVerified/printApproved` **всегда false**.
- `EuroOfficeNativeSaveAsReceipt` строго отсекает неразрешённые форматы, чужие session/tab/fixture, повторы и чужие JSON-поля, неверные SHA/размеры, отсутствие typed request, ложные права и неправдоподобные результаты.
- `EuroOfficeNativeSaveAsLab` независимо повторяет SHA оригинала до/после запуска и использует существующий `EuroOfficeNativeArtifactCandidate.InspectRevision` для ограниченной проверки ZIP, XML, OOXML частей, макросов/ActiveX/путей и SHA-256. Результат остаётся карантинным и **не** становится WORM-оригиналом или полноценной новой редакцией.
- `MainWindow.StartNativeSaveAsLabAsync` и `NativeOfficeDiagnosticPane.SetNativeSaveAsReview` работают только в закрытой диагностической вкладке, без нового пункта в обычном launcher/собственном мастере печати. Рабочий fallback LibreOffice/UNO не затронут.

## Тесты, подготовлены, НЕ запускались

- `EuroOfficeNativeSaveAsReceiptTests.cs`: строгая session/tab/format/filename привязка, неразрешённый DOCX→PPTX, cancel, отсутствующий диалог, чужое назначение, ложный OnSave, ZIP метаданные и подменённые/дублированные поля.
- `EuroOfficeNativeSaveAsLabTests.cs`: отдельные синтетические OOXML ZIP, независимый структурный review, неправильный формат до запуска процесса, изменённый эталон, повреждённый ZIP, отсутствие сохранённого результата, отсутствие типизированного диалога.
- `test_guarded_native_saveas_contract.py`: отдельный default-OFF CMake gate, реальный типизированный callback и ответ, приватное назначение, отсутствие перезаписи, оригинальный SHA, отказ от произвольного пути и прав публикации.
- `scripts/office_acceptance.py`: новый `cef-guarded-native-saveas-source-tests` до restore и обязательный ручной `eurooffice-guarded-sameformat-saveas-event-and-private-path-e2e`.
- Реальная автономная VM: негативные сценарии конфликтов целевого имени, повторного callback/дублирующихся iframe, cancel, неверных форматов, неверного SDK/binary SHA, сбоев процесса, link/traversal, недоступного `Editor.bin` и попытки доступа к пользовательским путям. Нужен полноценный DOCX/XLSX/PPTX раунд-трип в оригинальном EuroOffice и Microsoft Office.

## Не готово

- GUI-кнопка пользователя с произвольным выбором пути/формата, пользовательский диалог «Сохранить как», конвертация между форматами или PDF-экспорт; **этот режим только лабораторный**.
- Гарантированная модификация содержимого OOXML при вызове V8 `LocalFileSave`; источник может не сформировать сохранение без отдельного editor save pipeline. Сценарий честно выдаст `candidateWritten=false`.
- Валидность ZIP/XML означает лишь структурную проверку, не совместимость формул, макетов, рисунков, шрифтов/презентаций с Microsoft Office.
- Физическая компиляция, полное тестирование, Windows/Astra/ALT/Linux производственная реализация, реальное совместное редактирование, защищённая запись в WORM/GUP-2 и сериализация бизнес-конфликтов.

Встроенная upstream CEF может работать без sandbox; нативный процесс допустим только в отдельной disposable offline VM, **без корпоративных секретов и монтированных документов**. Лицензия AGPL и корпоративная политика развёртывания должны быть проверены отдельно. PR строго Draft; main и Actions не изменены.

**Следующий GD-31J.27:** после стендовой проверки фактической цепочки `LocalFileSave` — изолированный native script/save lifecycle proof и только затем UI-мастер для пользовательского Save As с транзакционным staging/locking, аудитом и без изменения WORM оригиналов.
