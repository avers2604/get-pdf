# GD-31J.5 — Qt display / DPI / dual-monitor / shutdown acceptance

**29 октября 2026.** Продолжение GD-31J.4 PR #72, не релиз. Цель — сделать проверяемыми **реальный жизненный цикл и экранную геометрию отдельного Qt-процесса**, не выдавая это за подтверждённую интеграцию EuroOffice CEF или единую вкладку Avalonia.

## 1. Реализованные диагностические возможности

`probes/eurooffice-desktop/native_host/main.cpp`: добавлены **два отдельных режима Qt-only**. Они не загружают SDK, CEF, документы, сеть и пользовательские профили.

- `--display-probe`: создаёт `QApplication` и обычное `QWidget`, показывает его, программно меняет размер 800×500 → 1024×640, записывает сведения о Qt-экранах и событиях (show/resize/move/focus/screenChanged/dpiChanged), закрывает окно и завершает процесс. Для местного smoke используется `QT_QPA_PLATFORM=offscreen`. Это **виртуальный тест** Qt и не подтверждает реальные мониторы.
- `--display-live`: выводит пояснение в Qt-окне, даёт **30 секунд** на перенос между двумя физическими мониторами (включая при необходимости изменение системного масштаба), затем закрывает окно и печатает **один JSON-отчёт в stdout**. Проверяются два настоящих `QScreen`, реальное событие `QWindow::screenChanged`, фокус, масштабирование. **Но даже такие события не являются доказательством правильной отрисовки CEF или единой вкладки ГЭТ.**
- Оба режима возвращают `runtimeKind="qt-only-no-cef"`, `cefInitialized=false` и `realMonitorVerified=false` **без исключений**. Протокол отчёта: `getdoc-qt-display-diagnostics/1`; дополнительных разрешений/файловых команд нет.

Тест `probes/eurooffice-desktop/test_qt_display.py` запускает **настоящий скомпилированный Qt-бинарник** на виртуальном дисплее с отказом от путей документов и direct-print и проверкой внешнего завершения дочернего процесса. Он требует установленного Qt6 и доступного `offscreen` платформенного плагина. CMake `native_host/CMakeLists.txt` теперь включает `gd31j5_qt_offscreen_display` в локальный CTest, только если есть Python. Ранее существовавшие тесты GD-31J.2 сохраняются.

`validate_qt_display.py` + `test_qt_display_evidence.py` независимо проверяют отчёт без Qt. Валидация отказывает при неверной схеме JSON, неизвестных ключах, NaN/невалидном DPI, отсутствии закрытия окна, `manualMode=false`, отсутствии двух экранов, неположенных флагах, отсутствии реального переключения экрана и фокуса. **Положительное заключение — только `preliminary-manual-evidence`** с `cefVerified=false`, `avaloniaEmbedded=false` и `releaseReady=false`. Это не формальная приёмка, а материал для ручной проверки.

Общие офлайн-тесты GD-31I дополнены `qt-display-evidence-tests` перед .NET restore. Новые CEF/Qt артефакты не копируются в основной GET и не активируют GitHub Actions.

## 2. Как проверить на отдельном локальном ПК

### A. Автоматическая проверка Qt-only offscreen (Windows и Astra/Linux)

Нужны установленный **Qt6 Widgets**, C++17 компилятор, CMake 3.20+, Python3 и Qt-плагин `offscreen`. Выполнять в клоне ветки GD-31J.5, без `git fetch`/сетевых пакетов и без доступа к реальным документам:

```bash
cmake -S probes/eurooffice-desktop/native_host -B artifacts/gd31j5-qt
cmake --build artifacts/gd31j5-qt --config Release
ctest --test-dir artifacts/gd31j5-qt -C Release --output-on-failure
python -m unittest discover -s probes/eurooffice-desktop -p 'test_qt_display_evidence.py' -v
```

На Windows при генераторе MSVC путь к `.exe` может быть внутри `Release`. CMake/CTest передаёт его тесту автоматически.

Это проверка **отдельного Qt**, **не** CEF/`QCefView`. Для CEF по-прежнему обязательны независимая безопасность и разрешённый стенд GD-31J.3/4. Запрещено автоматически запускать `--cef-empty-view-lab` в CTest.

### B. Ручная проверка **двух настоящих** мониторов

На лабораторном рабочем месте с двумя подключёнными дисплеями: запустить `getdoc-eurooffice-qt-probe --display-live`, перетащить окно на другой дисплей и обратно, проверить фокус и масштабирование. JSON из **stdout** сохранить в `artifacts/gd31j5-display-live.json` (в каталоге `artifacts`, который не коммитится). Например, на Astra:

```bash
./artifacts/gd31j5-qt/getdoc-eurooffice-qt-probe --display-live > artifacts/gd31j5-display-live.json
python probes/eurooffice-desktop/validate_qt_display.py artifacts/gd31j5-display-live.json
```

На Windows PowerShell 5.1 надёжнее сохранить **сырые байты** через Python, чтобы не получить UTF-16/BOM при стандартном перенаправлении:

```powershell
python -c "import pathlib, subprocess; p=pathlib.Path('artifacts/gd31j5-display-live.json'); r=subprocess.run(['artifacts/gd31j5-qt/Release/getdoc-eurooffice-qt-probe.exe','--display-live'],stdout=subprocess.PIPE,timeout=45); p.write_bytes(r.stdout); raise SystemExit(r.returncode)"
python probes/eurooffice-desktop/validate_qt_display.py artifacts/gd31j5-display-live.json
```

Путь к Qt .exe зависит от CMake генератора и локального расположения. На локальном стенде отдельно записать ОС, тип сессии (Win32/X11/Wayland), DPI каждого монитора, разрешения, видеодрайвер, масштаб 100/125/150/200%, скриншоты/наблюдение фокуса, наличие зависших процессов. Если событие `screenChanged` не возникло, валидатор **откажет** и это нужно разбирать, а не править результат вручную.

**Заключение валидатора не заменяет человеческое наблюдение**, подпись администратора, проверку поведения пользовательской панели ГЭТ и тест настоящего `QCefView` в окружении с CEF (в частности, Wayland OSR и XWayland).

## 3. Отдельная проверка аварий

Проверка `test_process_can_be_terminated_and_reaped` в `test_qt_display.py` создаёт диагностический Qt-процесс, принудительно завершает и ожидает освобождения. Она **не** тестирует CEF helper, потерю пользовательских изменений, восстановление OOXML или CAS: эти сценарии остаются обязательной отдельной задачей.

## 4. Статус на момент PR

| Пункт | Реализация | Проверка |
| --- | --- | --- |
| Отчёт Qt-разрешения и DPR/DPI | Есть | Требуется CMake/CTest |
| События смены монитора/масштаба | Есть | Требуются физические мониторы |
| Отслеживание фокуса/закрытия | Есть | Требуется реальный Windows/Astra запуск |
| Принудительное завершение Qt | Тест есть | Требуется реальный бинарник |
| Формальная защита от ложной приёмки | Валидатор и тесты есть | Локальные Python unittest не запускались здесь |
| Настоящий CEF и QCefView | Отдельный стенд GD-31J.4 | Не испытан |
| Вкладка Avalonia/офисное сохранение/PDF | Не подключено | **Не готово** |

Доступная среда разработки не содержит Qt6 и .NET SDK, GitHub недоступен из контейнера; реальная лабораторная сборка/запуск **в текущем сеансе не проводились**. Сохранять новые изменения в Draft PR, не включать их в `main` до проверки. Локальный редактор LibreOffice/UNO и принятая архитектура EuroOffice DocumentServer не меняются.
