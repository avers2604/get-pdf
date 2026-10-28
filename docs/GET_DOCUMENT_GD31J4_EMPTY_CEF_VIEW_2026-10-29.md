# GD-31J.4 — изолированный пустой QCefView (первый native editor view)

Дата: 29 октября 2026. Статус: **исходники подготовлены, сборка и запуск ещё не выполнены**. Draft поверх GD-31J.3 PR #71. Никаких изменений `main`, CI/GitHub Actions, runtime ГЭТ, локального LibreOffice/UNO или серверного EuroOffice.

## 1. Что добавлено

- `cef_lab/cef_empty_view.cpp`: отдельная opt-in C++ программа Qt 6, использующая **реальный** upstream `QCefView`.
- В точной закреплённой ревизии `desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51` метод `QCefView::Create(manager, cvwtEditor)` **сам** создаёт `CCefViewEditor` через `CAscApplicationManager::CreateCefEditor(this)`. Мы вызываем **только** `QCefView::Create`, без двойного создания.
- Действие запуска: строго `--cef-empty-view-lab`, временный профиль `QTemporaryDir`, `CApplicationCEF::Prepare` + `Init_CEF`, один дочерний Qt-вид редактора и диагностическая надпись о запрете работы с файлами.
- Жизненный цикл ограничен **12 секундами** или ручным закрытием; затем `DestroyCefView(viewId,false)` и `CloseApplication()` в GUI-потоке. `QTimer` не гарантирует отсутствие зависания при дефекте в стороннем CEF, поэтому испытание запускать под внешним watchdog и с изоляцией процесса.
- Запрещены: `OpenLocalFile`, `CreateLocalFile`, произвольные URL, документ, сохранение, экспорт PDF, принтер, Nextcloud/GUP-2/WORM и обмен ключами. Действительный результат — **проверка создания пустого CEF view**, а не работающий офисный редактор.
- `cef_lab/CMakeLists.txt`: **вторая независимая опция**, `GETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB=OFF` по умолчанию; включение допускается **только вместе с** `GETDOC_GD31J3_ENABLE_CEF_LAB=ON` и подтверждённым изолированным стендом. Используются только закреплённые, предварительно собранные SDK и Qt6; исходники vendor не скачиваются.
- `test_empty_view_contract.py`: offline-regression на важные сигнатуры, отсутствие файловых операций, обязательность отдельной опции сборки. Этот **статический** тест не доказывает функционирование CEF.
- `scripts/office_acceptance.py`: запуск теста контракта **без запуска CEF** перед `dotnet restore`. Добавлен тест на его присутствие в плане приёмки.

## 2. Безопасность: обязательное ограничение

Зафиксированный исходник `Euro-Office/desktop-sdk/ChromiumBasedEditors/lib/src/cefapplication.cpp` устанавливает `settings.no_sandbox=true`, если SDK собран без `CEF_USE_SANDBOX`. Это **не зависит от того, что GET используется в закрытой сети**. Зелёный CMake или валидный SHA **не** подтверждают включённый sandbox.

Этап допускается только на отдельной офлайн-VM/лабораторном ПК без документов, секретов, файлов ГЭТ и доступных сетевых ресурсов, с ограниченной учётной записью. Запуск на рабочих ПК/в основном приложении/на сервере запрещён до отдельного security-review, лицензирования AGPLv3/NOTICE и проверенной упаковки CEF/helper.

## 3. Команды локальной приёмки (только на изолированном стенде)

Безопасная статическая проверка исходников, не запускает нативный CEF:

```bash
python -m unittest discover -s probes/eurooffice-desktop/cef_lab -p 'test_empty_view_contract.py' -v
python -m unittest discover -s probes/eurooffice-desktop/cef_lab -p 'test_cef_lab_gate.py' -v
```

Для отдельного ИБ-разрешённого лабораторного ПК с уже установленными Qt6/CEF, одобренными SDK/CEF assets и OS containment:

```bash
cmake -S probes/eurooffice-desktop/cef_lab -B artifacts/gd31j4-empty-view \
  -DGETDOC_GD31J3_ENABLE_CEF_LAB=ON \
  -DGETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB=ON \
  -DGETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED=ON \
  -DEUROOFFICE_DESKTOP_TREE=/approved/DesktopEditors \
  -DEUROOFFICE_RUNTIME_LIB_DIR=/approved/eurooffice-runtime/lib
cmake --build artifacts/gd31j4-empty-view --config Release
```

Дальнейший **ручной** запуск `getdoc-eurooffice-cef-empty-view-lab --cef-empty-view-lab` разрешён **только после** проверки бинарных/модульных SHA, дочернего `editors_helper`, Qt/CEF resources, блокировки сети и запуска внутри отдельного OS sandbox. Никакого автостарта и CTest для него нет.

При приёмке фиксировать: создаётся ли пустой CEF вид; ошибки/вылеты; работа/отсутствие CEF subprocess; корректное освобождение; Win10/11 и Astra X11/Wayland; DPI 100/150/200%; чистота профиля и памяти после выхода. Не использовать пользовательские документы.

## 4. Что НЕ готово

| Проверка | Статус |
| --- | --- |
| Исходники настоящего `QCefView::Create(cvwtEditor)` | Добавлены |
| Статические тесты ограничений | Добавлены, не запускались |
| CMake Qt/CEF, реальный runtime | Не собирался и не запускался |
| Пустой CEF view действительно отображается | **Не подтверждено** |
| Повторное создание, закрытие, утечки | **Не подтверждено** |
| `OpenLocalFile`/DOCX/XLSX/PPTX round-trip | **Не подключено** |
| Встраивание одной вкладки Avalonia | **Не подключено** |
| Сохранение новой редакции, WORM, CAS | **Не подключено** |
| Экспорт в PDF/собственный мастер печати ГЭТ | **Не подключено** |
| Совместное редактирование через EuroOffice server | Отдельная серверная интеграция, по умолчанию deny-all |

Следующий технический пункт **GD-31J.5**, только после успешной native приёмки: проверить хост в отдельном процессе с надёжным IPC/окном и запретом передачи адресов чужих окон, затем работу с тестовой локальной копией и конвертацию PDF. Встраивать нативный view в одну вкладку ГЭТ до подтверждённого Windows/Astra фокуса/масштабирования/процессных границ нельзя.

## Upstream

- https://github.com/Euro-Office/desktop-sdk/blob/8cc8d747cb48953bf8020c2bfbb67ede6cb55c51/ChromiumBasedEditors/lib/qt_wrapper/src/qcefview.cpp
- https://github.com/Euro-Office/desktop-sdk/blob/8cc8d747cb48953bf8020c2bfbb67ede6cb55c51/ChromiumBasedEditors/lib/src/applicationmanager.cpp
- https://github.com/Euro-Office/desktop-sdk/blob/8cc8d747cb48953bf8020c2bfbb67ede6cb55c51/ChromiumBasedEditors/lib/src/cefapplication.cpp
