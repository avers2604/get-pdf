# GD-31J.3 — EuroOffice CEF: изолированная проверка жизненного цикла, без документов

**29 октября 2026.** Разработка продолжается поверх GD-31J.2 PR #70 и GD-31J PR #69. Это **не полноценный редактор, не встроенная вкладка ГЭТ и не разрешение на эксплуатацию**. Основная оболочка «ГЭТ Документы», вкладки, офлайн-редактирование через LibreOffice/UNO, мастер печати и WORM не изменены. GitHub Actions не подключаются.

## 1. Подтверждено по закреплённым исходникам upstream

Суперрепозиторий `Euro-Office/DesktopEditors` закреплён по `6a671bb1ad238c61965a64de3a005c420940c814`, подмодули `desktop-sdk` — `8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`, `core` — `a4a1ba1e9c8e6b6c841c3f059b3a8f82177d63f5`.

В настоящих исходниках:

- `desktop-apps/win-linux/src/main.cpp`: `QApplication`, `CApplicationCEF::Prepare`, `CApplicationCEF::Init_CEF`, подготовка папок/шрифтов/конвертера, запуск менеджера, CEF/Qt message loop, закрытие.
- `desktop-sdk/ChromiumBasedEditors/lib/include/cefapplication.h`: `Init_CEF(CAscApplicationManager*, int, char**)`, `IsChromiumSubprocess()`, `Close()`, `RunMessageLoop(bool&)`.
- `desktop-sdk/ChromiumBasedEditors/lib/src/cefapplication.cpp`: **по умолчанию** CEF `settings.no_sandbox=true`, когда **не** установлен `CEF_USE_SANDBOX`. Наличие закрытой сети, подписи сборки или SHA-256 **не заменяет изоляцию**.
- `desktop-sdk/ChromiumBasedEditors/lib/include/applicationmanager.h`: класс `CAscApplicationManager` и настройки каталогов.
- `desktop-sdk/ChromiumBasedEditors/lib/qt_wrapper/include/qascapplicationmanager.h`: `QAscApplicationManager`; этот слой требует Qt6/нативных библиотек.
- `desktop-sdk/ChromiumBasedEditors/lib/include/cefview.h`: `CCefViewEditor::OpenLocalFile` присутствует, **но в новом стенде не вызывается**, потому что не подтверждены безопасный хост, lifecycle, события завершения и сохранение новой редакции.
- `desktop-sdk/ChromiumBasedEditors/lib/qt_wrapper/src/qcefview.cpp`: проверен конкретный маршрут создания настоящего окна редактора — `QCefView::Create(manager, cvwtEditor)` внутри вызывает `CAscApplicationManager::CreateCefEditor(this)`. Значит, **не надо** вызывать `CreateCefEditor` второй раз напрямую: в дальнейшем нужно сохранить владение одним `QCefView` и его CEF-child. Для этого необходим отдельный GUI/lifecycle тест, а не нынешняя init-only проверка.

Источник: [Euro-Office DesktopEditors](https://github.com/Euro-Office/DesktopEditors), [desktop-sdk cefapplication.cpp](https://github.com/Euro-Office/desktop-sdk/blob/8cc8d747cb48953bf8020c2bfbb67ede6cb55c51/ChromiumBasedEditors/lib/src/cefapplication.cpp).

## 2. Реализованные файлы

### `probes/eurooffice-desktop/cef_lab/cef_init_smoke.cpp`

Добавлена **отдельная C++-программа** для проверки реального вызова `CApplicationCEF::Prepare/Init_CEF` с `QAscApplicationManager`.

- Принимает **только** `--cef-init-lab`; запрещены параметр документа, URL, режим редактирования и печати.
- Создаёт отдельный временный профиль Qt (`QTemporaryDir`), папки кэша, cookies, восстановления и плагинов.
- Отключает поддержку макросов и плагинов в настройках менеджера.
- Проверяет код `Init_CEF` и выполняет `Close`, не создавая `QCefView`, не вызывая `OpenLocalFile` и не запуская редакторный цикл.
- **Важное ограничение:** даже такая проверка может создавать Chromium-процессы и использовать unsandboxed CEF из upstream. Запуск разрешается только на подготовленном **отдельном лабораторном ПК без пользовательских файлов**, с внешней блокировкой сети. Ни одна из этих условий не может быть технически доказана только по флагу CMake.

### `probes/eurooffice-desktop/cef_lab/CMakeLists.txt`

- `GETDOC_GD31J3_ENABLE_CEF_LAB=OFF` **по умолчанию** — вообще не добавляет исполняемые файлы.
- Для включения нужно вручную выставить `GETDOC_GD31J3_ENABLE_CEF_LAB=ON` и `GETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED=ON`.
- До конфигурации сверяются точные Git SHA и **чистота локальных деревьев** upstream/submodules.
- Требуются **предварительно собранные локальные библиотеки** `ascdocumentscore` и `qtascdocumentscore` и Qt 6. Никакого `FetchContent`, `git clone`, скачивания CEF, сборки vendor-кода как dependency или загрузки сетевых пакетов.
- Не добавляется в `GetDocument.slnx`, MSI, пакет Linux, GET-shell либо выпуск.

### `cef_lab_gate.py` и `test_cef_lab_gate.py`

- Локальный CLI проверяет явно указанную изолированную папку, закреплённые исходники и наличие helper, CEF library, web editor assets.
- Запрещает сетевые пути, symlink-компоненты, незакреплённые и модифицированные исходные деревья, попытку использовать режим `--enable-documents`.
- Даже если предзапусковые файлы на месте, результат имеет `document_editing_approved=false` и `production_ready=false`.
- Список необходимых CEF ресурсов **не исчерпывающий** и не доказывает что CEF запустится; дополнительные файлы ресурсов ICU/CEF, зависимые native библиотеки, Qt runtime, подпись и trusted packaging должны проверяться на стенде.
- Новые Python unittest включены в текущий офлайн-приёмочный скрипт `scripts/office_acceptance.py` **до** .NET restore, без попытки реального запуска CEF.

## 3. Команды подготовки, НЕ команды запуска на рабочих станциях

Проверка безопасности каталога/версий без исполнения Chromium:

```bash
python -m unittest discover -s probes/eurooffice-desktop/cef_lab -p 'test_cef_lab_gate.py' -v
python probes/eurooffice-desktop/cef_lab/cef_lab_gate.py \
  --source /approved/DesktopEditors \
  --runtime /approved/eurooffice-runtime \
  --lab-isolated
```

**Пример CMake-конфигурации** для изолированного ПК с заранее проверенными локальными native библиотеками (не запускать на боевом компьютере):

```bash
cmake -S probes/eurooffice-desktop/cef_lab -B artifacts/gd31j3-cef \
  -DGETDOC_GD31J3_ENABLE_CEF_LAB=ON \
  -DGETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED=ON \
  -DEUROOFFICE_DESKTOP_TREE=/approved/DesktopEditors \
  -DEUROOFFICE_RUNTIME_LIB_DIR=/approved/eurooffice-runtime/lib
cmake --build artifacts/gd31j3-cef --config Release
```

**Не запускать `getdoc-eurooffice-cef-init-lab --cef-init-lab`, пока ИБ и ИТ не утвердили физическую/виртуальную изоляцию, запуск без пользовательских файлов, версию CEF и упаковку зависимостей.** Этот репозиторий не содержит готовый бинарник и не выдаёт положительного заключения по безопасности.

## 4. Критерии завершения GD-31J.3

| Направление | Сейчас | Требуемая приёмка |
| --- | --- | --- |
| Offline CEF source/runtime preflight | Исходники + тесты | Прогон Python на реальном офлайн-стенде |
| CEF initialize/close | Исходники compile target | Локальная компиляция CMake, безопасный запуск на отдельном ПК |
| Qt/CEF message loop | Не тестировался | Отсутствие зависаний/утечек, правильное завершение subprocess |
| CEF sandbox/security | Не подтверждено | Независимая ИБ-проверка опций сборки, подписи, helper и OS containment |
| Настоящий editor view | **Не создан** | Проверенный `CreateCefEditor` и `QCefView` в отдельном процессе |
| Open/save DOCX/XLSX/PPTX | **Не подключён** | Только тестовые копии, запрет макросов/записи WORM, roundtrip |
| GET print wizard / export PDF | **Не подключён** | Только PDF export с проверкой результата; без прямой печати |
| Avalonia одна вкладка | **Не подключено** | Windows/Astra, DPI, X11/Wayland, фокус/память/многомониторность |
| Nextcloud/GUP-2/SSO/CAS | **Не подключено** | Аутентификация, ACL, атомарные ревизии, аудит |
| .NET/CMake/Python integration | **Не запускались здесь** | Только локальная офлайн-приёмка; GitHub Actions не включать |

**Текущее решение:** не переводить GET Documents на нативный EuroOffice Desktop до выполнения критериев. Безопасный пользовательский fallback — текущая реализация LibreOffice/UNO.
