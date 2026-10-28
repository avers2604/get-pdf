# GD-31J.2 — изолированный Qt-хост и проверка запуска EuroOffice DesktopEditors

**Дата:** 29.10.2026. **Статус:** исходники и тесты настоящего отдельного Qt-процесса добавлены; сборка/запуск Windows и Astra **не выполнены в текущей среде**. **Это диагностический Qt host, НЕ подключённый Chromium/EuroOffice редактор.**

Наследует GD-31J PR #69, GD-31I #68 и цепочку GD-31A–H. `main` не затронут, GitHub Actions не включались. Действующий локальный движок ГЭТ остаётся **LibreOffice/UNO**, основной целевой серверный — **EuroOffice DocumentServer** после безопасного запуска и WORM/CAS.

## 1. Что действительно сделано

### Реальный отдельный процесс на Qt 6

`probes/eurooffice-desktop/native_host/main.cpp` — маленькое самостоятельное приложение Qt 6 с двумя строго заданными режимами:

- `--headless-probe`: запускает `QCoreApplication`, читает **ровно одно** каноническое JSONL hello, проверяет протокол `getdoc-eurodesktop-probe/1` + закреплённый upstream SHA; возвращает JSON с `features=[]`, затем выходит с кодом 0. Неканонический/повторный/неизвестный JSON, другая версия, запрос открытия/сохранения/печати, данные более 16 KiB или EOF завершаются кодом 2 **без ответа и без доступа к файловой системе**.
- `--gui-probe`: делает то же hello и показывает **реальное отдельное диагностическое окно Qt**, в котором явно указано, что офисное редактирование, сохранение и печать отсутствуют. При закрытии окна процесс штатно завершается.

Оба режима **не содержат** интеграции SDK, CEF, конвертера, документа, печати или сетевых функций. Процесс с Qt GUI **НЕ встроен в Avalonia** и пока не проходит проверку многомониторности. Он позволяет без ложных заявлений проверить ровно жизненный цикл окна и обмен IPC на Windows/Astra.

Для него добавлены `native_host/CMakeLists.txt`, локальные сценарии `test_qt_host.py` и `launch_qt_window.py`. Сборка не скачивает исходники и не меняет систему. Чтобы проверить **настоящие** pinned заголовки SDK/Qt, использовать отдельно уже добавленный `probes/eurooffice-desktop/CMakeLists.txt` из GD-31J (#69).

### Запуск из C# без подключения к рабочим документам

`src/GetDocument.Office/EuroOfficeDesktop/EuroOfficeNativeLabHost.cs`:
- `EuroOfficeNativeLabHostPolicy`: путь к исполняемому файлу **только в утверждённом локальном каталоге**, без UNC и ссылок/reparse-point на всём пути; размер до 512 MiB; SHA-256 должен совпасть с заранее разрешённым хешем (его нельзя заменять автоматически вычисленным во время приёмки).
- `EuroOfficeNativeLabHostSession.StartAsync`: требует `labApproved=true`, запускает отдельный процесс с `UseShellExecute=false`, stdin/stdout IPC, фиксированный `--headless-probe` или `--gui-probe`, timeout 10 сек на hello. Любое несовпадение протокола, ошибка процесса, ненулевой exit или **даже объявление неподтверждённой возможности редактирования** приводит к отказу; дочерний процесс завершается.
- `DisposeAsync` завершает Qt-процесс и освобождает ресурсы. Никаких открытий документов/печати/автоматического fallback он не вызывает.

**Ограничение безопасности:** проверка SHA-256 и локального пути — это *лабораторная политика*, а не замена механизма проверки подписи, политики доверенных файлов/обновления, защиты от подмены файла между проверкой и запуском (TOCTOU) или sandboxing. Поэтому процесс не подключён к живому пользовательскому UI.

Новые `EuroOfficeNativeLabHostTests` проверяют неправильный SHA, отсутствующие/пустые файлы, выход из утверждённого каталога, отсутствие полномочий при одном лишь `hello`. Дополнительный интеграционный MSTest запускает **реальный** `GD31J2_QT_HOST_BINARY`, если на лабораторном стенде заданы проверенный бинарник и `GD31J2_QT_HOST_SHA256`; иначе он **Inconclusive**.

## 2. Локальная сборка и проверка (не на рабочем ПК сотрудника)

Потребуется заранее установленный Qt 6 с Widgets, CMake 3.20+, C++17 компилятор, Python 3.10+ для тестов. Никакой `git clone`, WiX, download и облачный API не запускаются.

### Windows (VS/CMake/Qt 6)

```powershell
cmake -S probes/eurooffice-desktop/native_host -B artifacts/gd31j2-qt -DCMAKE_PREFIX_PATH="D:/approved/Qt/6.*/msvc"
cmake --build artifacts/gd31j2-qt --config Release
ctest --test-dir artifacts/gd31j2-qt -C Release --output-on-failure

$env:GD31J2_QT_HOST_BINARY = "D:\approved\qt-host\getdoc-eurooffice-qt-probe.exe"
$env:GD31J2_QT_HOST_SHA256 = "<ранее зафиксированный SHA-256>"
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --filter "FullyQualifiedName~EuroOfficeNativeLabHostTests"
```

Важно: `D:/approved/Qt/6.*/msvc` — **пример заменяемого пути**, CMake не разворачивает wildcard; указать фактическую локальную версию. Не использовать автоматически вычисленный hash как разрешение на доверие. Полный `GD31J2_QT_HOST_BINARY` должен находиться в утверждённой директории, которая не перезаписывается незаверенными пользователями. Не запускать на рабочем документе или под администратором без нужды.

### Astra/Linux

```bash
cmake -S probes/eurooffice-desktop/native_host -B artifacts/gd31j2-qt
cmake --build artifacts/gd31j2-qt -j2
ctest --test-dir artifacts/gd31j2-qt --output-on-failure
python probes/eurooffice-desktop/launch_qt_window.py artifacts/gd31j2-qt/getdoc-eurooffice-qt-probe
```

GUI-тест провести вручную: открыть окно, отключить сеть, изменить DPI, проверить русскую надпись/фокус, закрыть его и убедиться, что процесс не остался. Этот сценарий **не** проверяет CEF в Wayland/XWayland, печать или редактор, и не удовлетворяет критерию полноценной встроенной вкладки.

### Интеграция в общий GD-31I контроль

Опционально: `python scripts/office_acceptance.py --feed /approved/nuget --qt-host-binary /approved/getdoc-eurooffice-qt-probe`. Если Qt бинарник указан, локальная приёмка включает **настоящий** headless-smoke тест перед `dotnet restore`. Позитивный handshake **не** меняет `releaseReady=false`: нужны отдельные проверки интерфейса, WORM, печати, адаптера, сохранения и лицензий.

## 3. Почему сейчас нельзя честно встроить реальный редактор

Проверенная upstream последовательность `Euro-Office/desktop-apps/win-linux/src/main.cpp`:

```text
QApplication / Qt DPI mode
     → CApplicationCEF::Prepare(argc, argv)
     → CApplicationCEF::Init_CEF(applicationManager, ...)
     → fonts/cache/local_editors_path/converter paths
     → AscAppManager::initializeApp/startApp
     → message loop (CEF or Qt)
     → CloseApplication / CEF shutdown
```

Анализ `Euro-Office/desktop-sdk` подтверждает `CreateCefEditor(CCefViewWidgetImpl*)` и `OpenLocalFile(...)`, но они зависят от реального CEF runtime, менеджера и Qt UI thread. Простой запуск `QWidget` или подмена `IntPtr` не создает полноценный редактор. В этом PR **нет** upstream SDK/CEF бинарников, адаптера сохранения, редактора документа или окна, встраиваемого в Avalonia.

**Следующий технический шаг GD-31J.3** — только на разрешённом локальном Qt/CEF стенде: изучить точную инициализацию менеджера, проверить загрузку файлов на независимой тестовой копии, события, реальный export PDF и отсутствие перезаписи, затем реализовать контролируемую связь с Avalonia; если нативный editor требует нарушить единое окно ГЭТ, сохранить LibreOffice fallback и исследовать другой путь.

## 4. Проверка готовности

| Функция | Статус |
| --- | --- |
| Отдельное Qt GUI приложение | Исходники есть, требуется сборка/запуск |
| Строгий JSONL handshake | Код и тесты готовы к запуску |
| Проверка бинарника и supervision из C# | Код и тесты готовы к запуску |
| SDK native header compile | Отдельный стенд из PR #69, не выполнен |
| Подключённый CEF + реальный редактор | **Не готово** |
| DOCX/XLSX/PPTX открыть/сохранить | **Не готово** |
| Экспорт PDF для своего мастера печати | **Не готово** |
| Одна вкладка Avalonia + Win/Linux | **Не готово** |
| Сетевой совместный редактор, WORM/CAS | **Не готово** |

**Сборка и тесты в текущем сеансе не запускались; статический code review не является доказательством прохождения CMake/.NET/MSTest.** Текущие PR — Draft, `main` не менялся, GitHub Actions не включались.
