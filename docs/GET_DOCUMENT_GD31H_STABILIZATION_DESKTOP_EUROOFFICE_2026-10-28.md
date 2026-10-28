# GD-31H — аудит поставки и исследование EuroOffice DesktopEditors

Дата проверки: **28.10.2026**. Статус: анализ кода и источников; локальная компиляция .NET / LibreOffice / EuroOffice Desktop **не выполнена**. `main` не изменён; никакого автоматического переключения движков.

## 1. Базовая архитектура и целевой результат

- **GET Documents** остаётся единой пользовательской оболочкой: лаунчер, вкладки, дизайн-система 4.1, история/ревизии, PDF, сканирование и **свой мастер печати**.
- **EuroOffice DocumentServer** утверждён как первичный серверный редактор для совместной работы, но включается только после рабочего SSO, trusted launch, ACL, callback, immutable CAS и WORM. `DenyAll` сохраняется по умолчанию.
- **LibreOffice/UNO** — реальная текущая локальная реализация офисных операций на Windows. Она остаётся резервной при отсутствии серверных подтверждений и до функциональной аттестации нового движка.
- **EuroOffice DesktopEditors** — отдельный *кандидат на будущий локальный движок*. Отдельный проект не является подтверждением встраиваемого Avalonia API: нельзя заменить зависимость и считать, что появился встроенный редактор.

## 2. Зафиксированный upstream для лаборатории

Исследован реальный публичный репозиторий: [Euro-Office/DesktopEditors](https://github.com/Euro-Office/DesktopEditors). Целевая исследовательская ревизия main на 28.10.2026:

```text
Euro-Office/DesktopEditors @ 6a671bb1ad238c61965a64de3a005c420940c814
core               a4a1ba1e9c8e6b6c841c3f059b3a8f82177d63f5
core-fonts         9cd64d6eedc72b75b7271f325f075cb8c73e59fa
desktop-apps       7a1297b3172a0993401308fbd09633e0597704e0
desktop-sdk        8cc8d747cb48953bf8020c2bfbb67ede6cb55c51
dictionaries       0020f5ed5174e2a213171c4c7da9ec06ca140869
document-templates 40cc88d509bac3283539b80b9534a41191d7c4ff
sdkjs              40d9b7780f11a5cadc259f9b9815a364de8caa96
sdkjs-forms        5c2f5a010dae3581adda34d9bab20b1903c94081
web-apps           ce0ba49545810a56a3878081ab762c6a7ae8dd4e
```

Это **исследовательский commit-pin**, а НЕ выбранный production-релиз. Полные SHA на девять Git submodule получены из Git tree commit. Не подтягивать `main`/latest автоматически ни в GET, ни на изолированный стенд.

Upstream состоит из `desktop-apps` (оболочка), `desktop-sdk`, `core` (конвертация), `sdkjs`, `web-apps` и ресурсов. В официальных инструкциях указаны сборка через PowerShell/MSVC/CMake на Windows и Docker-based build на Linux, Qt и Chromium Embedded Framework. Официальная документация описывает **готовое Desktop приложение**, но не подтверждает готовый нативный контрол для Avalonia, безопасный C ABI или API собственных кнопок GET. Требуется proof-of-concept.

Источники:
- https://github.com/Euro-Office/DesktopEditors
- https://github.com/Euro-Office/DesktopEditors/tree/main/build
- https://github.com/Euro-Office/desktop-sdk
- https://euro-office.github.io/documentation/installation/releases/

## 3. Матрица вариантов локального движка

| Вариант | Преимущества | Издержки/риски | Решение GD-31H |
| --- | --- | --- | --- |
| A. Оставить LibreOffice/UNO | Уже есть GET toolbar, recovery и тестовые сценарии | Некоторые команды требуют корректировки UNO и XLSX round-trip | **Сохранить в текущем коде** |
| B. Запускать EuroOffice DesktopEditors отдельным приложением | Готовый офлайн-редактор, проверка OOXML, возможно меньше своего кода | Нарушает обязательное единое окно GET, две системы UI/печати | **Только лабораторное сравнение**, не внедрять как основной UX |
| C. Встроить редактор DesktopEditors в Avalonia через SDK/CEF | Потенциально один офлайн/онлайн стек | Потребует изучения native API, управления окнами/DPI, Qt/CEF, процесса/песочницы, лицензий; API не подтверждён | **Прототип после изучения кода**, пока статус blocked |
| D. Веб-редактор EuroOffice DocumentServer в существующем NativeWebView | Есть подписанный launch/CSP контракт, coauthoring | SSO/ACL/callback/WORM и реальный сервер пока недоступны | **Серверный приоритет**, продолжать отдельно, fail-closed |

**Критерий перехода:** вариант C может заменить A только если доказаны: встраивание в одну вкладку ГЭТ без второго окна; все офисные команды доступны через документированные API; офлайн запуск; однозначный процесс сохранения *новой редакции*; WORM/read-only; Ctrl+Z/буфер обмена/IME/русская раскладка/масштабирование/2 монитора; собственная GET печать через PDF/CUPS/IPP; Windows 10/11 и Linux/Astra; запуск на слабых ПК; модель распространения/AGPLv3 и NOTICE. Иначе не менять действующую архитектуру.

## 4. Техническое исследование — последовательность на стенде

1. Получить исходники на разрешённой сборочной машине через `git clone`, переключить на закреплённый SHA, затем `git submodule update --init --recursive`; сверить `git submodule status`, подписи, лицензии, 3rd-party NOTICE, SBOM.
2. Изучить `desktop-apps/win-linux`, `desktop-sdk/ChromiumBasedEditors`, SDK bridge, CEF/Qt родительское окно и события открытия/сохранения. **Не выдумывать native export или CLI**, пока он не обнаружен в конкретной ревизии.
3. Прототип **вне основного GET**: 1 вкладка Avalonia, локальный DOCX/XLSX/PPTX, редактор как дочерний компонент (если API существует); закрыть окно/процесс, отмена, перехват печати. Отдельно Windows и Astra Linux.
4. Замеры: cold/warm start, idle RAM и большой XLSX, GPU/no GPU, DPI 100/150/200%, отключение сети и сервера, 2 монитора; отчёт по доступности UIA/AT-SPI.
5. Испытать реальные шаблоны и таблицы: сложные стили, оглавления, сводные таблицы/диаграммы, формулы, валидация, PowerPoint-анимации, рукописные подписи/формы, кириллица.
6. Только после подтверждения совместимости API: **новый provider** и собственные feature IDs (не пропускать UNO-команды напрямую через DocsAPI); отдельное подтверждение редактором + тесты.
7. Провести аудит AGPLv3 (включая модификации и предоставление соответствующего исходного кода там, где это требуется), брендинга/логотипов, 3rd-party лицензий и возможностей обновления в закрытой сети. Отсутствие коммерческой продажи не освобождает автоматически от условий лицензии.

## 5. GD-31A–G: аудит по текущему дереву

- Stacked открытые Draft PR: **#60 → #62 → #63 → #64 → #65 → #66**. Основание — `feature/get-document-euro-office-primary` (#42). `main` не содержит этот стек.
- В последних версиях статически сверены **18 из 18** идентификаторов C# OfficeFeatureCatalog, Python `FEATURES` и собственной панели GET.
- EuroOffice провайдер содержит **0** разрешённых UNO команд до собственной API-аттестации. Скрытая автоматическая подмена движка запрещена.
- Выявленный пробел в GD-31G: `OfficeRecoveryStore` проверял ZIP структуру, длины и коэффициент сжатия, но не читал содержимое XML. Поэтому повреждённая XML-часть могла получить валидный manifest и появиться в списке восстановления.
- В GD-31H добавлена потоковая проверка каждого ZIP элемента (без распаковки на диск) и XML через `XmlReader` с `DtdProcessing.Prohibit` и `XmlResolver=null`. Дополнены MSTest сценарии на повреждённый XML/DTD и валидный дополнительный XML. Это **не** полноценный OOXML semantic check и не антивирус.

### Команды локальной приёмки (без GitHub Actions)

```powershell
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

Проверка встроенного Python-моста даже без установленного UNO (только синтаксис; отдельно от интеграционных тестов):

```powershell
python -m py_compile src/GetDocument.Office/LibreOffice/lo_editor.py
python -m py_compile src/GetDocument.Office/LibreOffice/lo_bridge.py
```

**Критерий merge**: успешные локальные сборки и тесты, штатная работа UNO на Windows, независимая проверка сложных DOCX/XLSX/PPTX в ГЭТ, сохранение WORM, восстановление после реального падения, отсутствие запрещённых URL/макросов; окончательный документ по приёмке. Не утверждать результат без результатов тестового компьютера.

## 6. Не выполнено этим PR

- Нет нативного интегрированного EuroOffice Desktop в ГЭТ и нет готового SDK-адаптера.
- Нет работающего серверного SSO + доверенного callback/сохранения.
- Нет подтверждения, что Calc DataPilot экспортирует редактируемый Excel PivotTable во всех допустимых версиях.
- Нет локальной компиляции/.NET 10, LibreOffice/UNO runtime, аппаратного принтера, Windows/Astra, идущих от этого сеанса.
- Многочисленные предыдущие Draft PR **не слиты и не считаются релизными**.
