# GD-31J — EuroOffice DesktopEditors: изолированный native API probe, v1

Дата: 28.10.2026. Основа: GD-31I PR #68 → GD-31H PR #67 → #66 → #65 → #64 → #63 → #62 → #60 (исходный EuroOffice архитектурный PR #42). **Draft, не релиз и не готовый редактор.**

## 1. Что реально реализовано

**Не выполняется замена LibreOffice/UNO.** В `GetDocument.Office` добавлен чистый C# протокол `EuroOfficeDesktopProbeProtocol` для будущего самостоятельного нативного процесса (не для внедрения Qt в основной процесс Avalonia).

- Закреплён протокол `getdoc-eurodesktop-probe/1` и upstream commit `6a671bb1ad238c61965a64de3a005c420940c814`.
- Формат `hello`: `{ "command": "hello", "protocol": "...", "upstreamCommit": "..." }`. Ответ должен содержать **ровно** `protocol`, `engine=EuroOfficeDesktopEditors`, `upstreamCommit`, `features` — без лишних/дублирующихся полей. Длина ответа не больше 16 KiB, чтение ограничено, поддерживается cancellation.
- Разрешённые *названия потенциальных* действий: `open-local`, `save-as-new-revision`, `export-pdf`, `resize`. **Ни одна из них не становится рабочей по факту перечисления:** необходимы подтверждённый native host и явное согласование лаборатории; метод `Require` иначе отклоняет вызов. `print-direct`, макросы, JS/UNO dispatch, произвольные URL и внешние пути отсутствуют в разрешённом наборе.
- Открытие и экспорт возможны только для **локальных staging-копий** DOCX/XLSX/PPTX без макросов размером до 128 MiB. Запрещены URL, выход за утверждённую папку, reparse-point/symlink и Windows UNC. Код проверяет лишь метаданные и путь; **не выполняет полную OOXML/AV проверку**. Её нужно добавить при реализации реального нативного хоста.
- `CreateSaveRequest` **по умолчанию запрещён** (`wormOriginal=true`). Только доверенный вызывающий код, подтвердивший редактируемую рабочую копию, может сформировать запрос с `wormOriginal=false`. Это **НЕ сохранение файла**: реальный хост обязан заново проверить конфликт/запись и гарантировать отсутствие перезаписи. WORM-оригинал и исходный путь в GUP-2 не передаются в этот прототип.
- Для печати допустим только запрос `export-pdf` во **вновь создаваемый** временный PDF. Он затем должен пройти проверки и передаваться штатному **мастеру печати ГЭТ**. Прямой вызов принтера EuroOffice не разрешён. Текущий код **не экспортирует реальный PDF**.
- `resize` проверяет диапазон размеров 1–16384 и масштаб 0.5–4.0, но ещё не передаёт реальное изображение в Avalonia.

Добавлен `EuroOfficeDesktopProbeTests` (MSTest): строгий handshake, неправильные поля/версии/функции, отсутствие прав без lab approval, WORM, staged-only, no-overwrite, PDF print request, размер/DPI и EOF/cancellation.

### Проверяемый транспорт без редактора

`probes/eurooffice-desktop/mock_host.py` — **не нативный SDK и не офисный редактор**. Он отвечает только на `hello`, выставляет `features=[]` и возвращает код 2 на любое действие с документами. `test_mock_host.py` запускает этот тестовый процесс через Python subprocess и проверяет отказ. Тест включён в `scripts/office_acceptance.py` до .NET restore; это не добавляет Python к production приложению.

Запуск самостоятельно, без .NET/Qt:

```sh
python -m unittest discover -s probes/eurooffice-desktop -p test_mock_host.py -v
```

## 2. Проверка API настоящего SDK (compile-only)

`probes/eurooffice-desktop/sdk_api_probe.cpp` — **только проверка сигнатур нативного исходного кода**, он не линкует CEF, не запускает приложение и не создаёт окно.

Установлены C++ статические проверки:
- `CAscApplicationManager::CreateCefEditor(CCefViewWidgetImpl*) -> CCefViewEditor*`;
- `CCefViewEditor::OpenLocalFile(path, format, params) -> void`;
- `CCefViewEditor::GetLocalFilePath() -> std::wstring`;
- `QCefView` наследуется от `QWidget` и `CCefViewWidgetImpl`;
- `QAscApplicationManager` наследуется от `CAscApplicationManager`;
- константы `ASC_MENU_EVENT_TYPE_CEF_SAVE=3001`, `ONSAVE=3002`, `PRINT_START=5011`.

`CMakeLists.txt` требует Qt6 Core/Gui/Widgets/OpenGLWidgets и проверяет **только локальные Git SHA** до попытки компиляции:
- DesktopEditors: `6a671bb1ad238c61965a64de3a005c420940c814`
- desktop-sdk: `8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`
- core: `a4a1ba1e9c8e6b6c841c3f059b3a8f82177d63f5`

Никакого `FetchContent`, `git clone`, `latest`, сетевого скачивания или автоматического vendor обновления.

Пример на предварительно подготовленной офлайн-машине с Qt6/CMake/компилятором C++17 и исходниками upstream:

```sh
cmake -S probes/eurooffice-desktop -B artifacts/gd31j-header-build \
  -DEUROOFFICE_DESKTOP_TREE=/approved/sources/DesktopEditors
cmake --build artifacts/gd31j-header-build --config Release
```

Этот шаг покажет, что исходные заголовки имеют ожидаемый ABI/API на уровне компиляции, но **не доказывает совместимость двоичной сборки**, Qt/CEF thread-loop, сохранение, печать, корректность файла или возможность встраивания в Avalonia.

## 3. Встраивание Qt/CEF в Avalonia: что осталось

Стабильного готового C API/DLL для Avalonia не обнаружено, поэтому **не** подключён `QCefView` напрямую через P/Invoke: его жизненный цикл относится к Qt/C++ UI thread. Нельзя переиспользовать существующий `EmbeddedWindowHost` для произвольного чужого окна, пока не проверены происхождение HWND, владелец процесса, DPI и правила закрытия. На Astra Linux дополнительно требуются отличающиеся механизмы Wayland/X11.

Этап 2 GD-31J (отдельный PR после лаборатории):
1. Собрать официальный pinned EuroOffice DesktopEditors и проверить запуск без сети, лицензии, подписи, входные файлы, память и ресурсы слабого ПК.
2. Отдельный минимальный Qt/CEF host (НЕ основное приложение), инициализация `QAscApplicationManager`, `QCefView`, реальный `OpenLocalFile`, сигналы save/print, штатное закрытие.
3. Реализовать версионированный IPC между Avalonia и только проверенным host: бинарный SHA, запуск без shell, таймауты/ограничение доступа, события и жизненный цикл. `hello.features` разрешать лишь после реальных интеграционных тестов.
4. Запретить живую запись в рабочие WORM/SMB-файлы: только безопасные staging-копии, сохранение новой ревизией в GET/GUP-2 через CAS; для печати — выдача валидного PDF своему GET print wizard.
5. Проверить несколько вкладок, повторный запуск/падение дочернего процесса, Ctrl+Z, IME/кириллицу, DPI, Windows 10/11 и Astra X11/Wayland, 2 монитора и режимы слабого ПК.
6. Провести **AGPLv3 и прочие лицензии** и аудит зависимостей (в upstream headers есть также отдельные условия для GUI-ресурсов); не включать код/ресурсы upstream в наш exe/MSI без лицензирования.

## 4. Матрица реальной готовности

| Проверка | Статус |
| --- | --- |
| Известные pinned сигнатуры C++ и C# контракт | Исходники добавлены, статическая сверка |
| Test-double protocol и MSTest | Код тестов написан, **не запускался** |
| Compile-only CMake на Qt6/CEF headers | **Не запускался** |
| Живой Qt/CEF host | **Отсутствует** |
| Встроенная вкладка Avalonia с EuroOffice | **Отсутствует** |
| Реальный DOCX/XLSX/PPTX open/save | **Не реализован** |
| PDF в собственный мастер печати | **Только безопасный запрос, без PDF** |
| SSO/ACL/GUP-2/WORM/revisions | **Не подключены к прототипу** |
| Windows/Astra E2E | **Не запускались** |

**Не сливать**, не включать в production, не вводить пользователя в заблуждение о работе нативного редактора. Для текущей офисной работы остаётся локальный LibreOffice/UNO и серверный EuroOffice с DenyAll до подтверждённой интеграции.

## Исходники

- https://github.com/Euro-Office/DesktopEditors
- https://github.com/Euro-Office/desktop-sdk/blob/8cc8d747cb48953bf8020c2bfbb67ede6cb55c51/ChromiumBasedEditors/lib/include/applicationmanager.h
- https://github.com/Euro-Office/desktop-sdk/blob/8cc8d747cb48953bf8020c2bfbb67ede6cb55c51/ChromiumBasedEditors/lib/include/cefview.h
- https://github.com/Euro-Office/desktop-sdk/blob/8cc8d747cb48953bf8020c2bfbb67ede6cb55c51/ChromiumBasedEditors/lib/qt_wrapper/include/qcefview.h
- https://github.com/Euro-Office/desktop-sdk/blob/8cc8d747cb48953bf8020c2bfbb67ede6cb55c51/ChromiumBasedEditors/lib/include/applicationmanager_events.h
