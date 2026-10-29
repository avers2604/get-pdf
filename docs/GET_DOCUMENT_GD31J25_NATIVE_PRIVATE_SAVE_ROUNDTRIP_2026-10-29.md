# GD-31J.25 — нативное сохранение только синтетической рабочей копии и OOXML-кандидат

**Дата:** 29.10.2026. **База:** Draft PR #96 (GD-31J.24), закреплённый EuroOffice Desktop SDK `8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`. **Реальные тесты и сборки не запускались** (ранее принятое решение выполнять лабораторию после подготовки функциональных блоков). `main`, рабочий лаунчер и GitHub Actions не изменены.

## Фактический SDK save pipeline

Проверено по закреплённым `ChromiumBasedEditors/lib/src/cefview.cpp` и `.../include/applicationmanager_events.h`:

- `ASC_MENU_EVENT_TYPE_CEF_SAVE` = 3001 обрабатывается `CCefView::Apply`, вызывает внутреннее renderer IPC `document_save`.
- `onlocaldocument_onsavestart` от renderer начинает файловую конвертацию (`LocalFile_SaveStart`) и при **настоящем Save As** может породить `ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE` (=7008) для выбора назначения. В обычном Save для уже сохранённого файла отдельный Save As диалог не обязателен.
- `ASC_MENU_EVENT_TYPE_CEF_ONSAVE` = 3002 может приходить после нативной операции сохранения, но **сам по себе** не доказывает корректность OOXML/атомарность SMB/WORM или полноту изменений.

## Что именно делает этот блок

**Это НЕ прямая поддержка Save As для рабочих документов.** Для эксперимента оригинал остаётся только эталоном. Перед `OpenLocalFile` новый `--open-staged-private-save-lab` **копирует разрешённый синтетический fixture** в `QTemporaryDir/work/<fixture>`. В CEF передаётся **только эта временная копия**. Даже если SDK автоматически сохранит тот же путь, он не может перезаписать эталон через наш `OpenLocalFile`.

1. Добавлен `GETDOC_GD31J25_ENABLE_SYNTHETIC_SAVE_LAB=OFF`. Для включения нужны все прежние независимые флаги: GD31J24 manual overlay для публичной CEF нотификации, pinned SDK, два SHA-approved rebuilt SDK runtime binaries, SHA-approved host, Linux/Wayland и ручное разрешение отдельной автономной виртуальной машины.
2. Нативный код передаёт `openedPath` приватного файла в `CCefViewEditor::GetFileFormat/OpenLocalFile`, наблюдает настоящий `ContentReady`, стабильный `Editor.bin` внутри приватного профиля, `modified` от реального sender-ID редактора. Только после всех событий и отсутствия native `IsSaveLocked` создаётся одно событие `ASC_MENU_EVENT_TYPE_CEF_SAVE` через `editor->Apply`; повторные сохранения не инициируются.
3. После нативного `OnSave`, завершения Qt/CEF, повторного SHA оригинального fixture и отличающегося SHA временной рабочей копии, создаётся **неперезаписываемый** карантинный файл `gd31j25-native-candidate-<sessionN>.docx|xlsx|pptx` в заранее утверждённом синтетическом staging. `QFile::copy` не перезаписывает существующий файл. Не производится запись в WORM, SMB, корпоративные папки, а также массовая очистка непроверенных файлов.
4. Одна stdout JSON-квитанция `getdoc-synthetic-roundtrip-lab/1` содержит session/tab/fixture, исходный SHA до/после, native modified/save/cancel flags, имя quarantine-кандидата из фиксированного шаблона, размер и SHA-256. **Нет абсолютных путей или содержимого документа**, а поля `documentVerified/saveVerified/pdfVerified/printApproved` всегда false.
5. `EuroOfficeNativeRoundTripReceipt.Parse`: строгие поля/типы, без дубликатов, связка session/tab/fixture, запрет чужого назначения, неверного SHA и слишком большого размера, запрет кандидата без native modified/OnSave, при отменённом Save или если исходный fixture изменился.
6. `EuroOfficeNativeRoundTripLab.RunAsync`: независимый SHA эталона **до запуска native**, bounded завершение/kill-reap процесса через существующий host; повторная независимая проверка SHA после завершения. Проверяет карантинный файл через существующий `EuroOfficeNativeArtifactCandidate.InspectRevision`: полный ограниченный обход ZIP OOXML, `[Content_Types].xml`, соответствующая основная часть (`word/document.xml`, `xl/workbook.xml`, `ppt/presentation.xml`), DTD/ссылки/macros/ActiveX/traversal/размер, итоговый SHA и сравнение с исходным.
7. Внутренняя диагностическая вкладка ГЭТ показывает **«проверен структурный OOXML-кандидат в карантине»** либо **«сохранение не подтверждено»**. `SaveCompleted`, `DocumentVerified`, `AuthorizedForPublication` остаются false даже после структурной проверки.

## Что НЕ доказано

- Нативный CEF/Qt до сих пор не собран и не запущен с этим кодом; тесты/компиляция не запускались. Нельзя утверждать, что Save callback придёт, что редактирование произойдёт, что Windows/Astra desktop уже готовы, или что сохранённый файл открывается в MS Office.
- Структурный ZIP/XML-контроль **не равен** полной валидации ECMA-376/ISO 29500, сохранению форматирования/формул, воспроизведению страниц и совместимости с Word/Excel/PowerPoint. Реальный тест совместимости обязателен.
- Событие `modified` — уведомление, SHA изменения доказывает только изменение байтов, но не доказывает намеренное редактирование. Сохранение **не атомарно** относительно всех внешних процессов, TOCTOU остаётся на файловой системе; лабораторный процесс нельзя подключать к общей корпоративной сети.
- **Настоящий Save As** через `ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE`/типизированный `LOCALFILE_SAVE_PATH` на заданный новый путь в этом PR ещё не реализован. Здесь обычный native Save выполняется **только над предварительно изолированной копией**, а проверенный результат дополнительно копируется в неперезаписываемый quarantine. Это намеренно более безопасная промежуточная ступень.
- Ни один карантинный файл нельзя публиковать в пользовательский документооборот/WORM/GUP-2. Отдельный аттестованный `Save As` adapter + реальный офисный roundtrip и эксплуатационный release gate остаются обязательными.

## Подготовленные проверки, запуск позже

- `EuroOfficeNativeRoundTripReceiptTests.cs`: чужие сеансы и имена, фальшивое подтверждение, отменённый Save, отсутствие изменения, mismatch исходника, дубли JSON, превышение размера и ложные права.
- `EuroOfficeNativeRoundTripLabTests.cs`: синтетическая ZIP OOXML-структура, независимая проверка кандидата и исходника, испорченный ZIP, изменение эталона, отсутствие сохранённого результата, запрет запуска без отдельного разрешения.
- `probes/eurooffice-desktop/cef_lab/test_private_roundtrip_contract.py`: статическая проверка, что CEF открывает приватную копию, а не эталон, что save вызывается после modified+ready, что оригинал повторно хешируется перед quarantine copy, что .NET ничего не публикует/удаляет.
- `scripts/office_acceptance.py` и runner-self-test: offline шаг `cef-private-save-roundtrip-source-tests` и новый обязательный **ручной** release gate `eurooffice-synthetic-private-save-roundtrip-and-immutable-fixture-e2e`.
- На будущей лаборатории: реальные DOCX/XLSX/PPTX только из synthetic allowlist, изменение видимого содержания, callback CEF, сохранённый карантинный файл, независимое открытие в оригинальном EuroOffice и Microsoft Office, сохранность эталона byte-for-byte, отсутствие сетевых и пользовательских файлов, принудительное завершение, конфликты имени, неподписанный/неверно pinned native host.

Никаких реальных корп. файлов, внешнего доступа, прав WORM, автоочистки пользовательских документов и production включения. Новый PR остаётся Draft; следующий GD-31J.26 — ограниченный native `Save As` adapter, исключительно после проверки событийнoго контракта и предотвращения перезаписи.
