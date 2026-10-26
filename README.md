# ГЭТ Документ / GET Document

**Новая объединённая настольная оболочка документов СПб ГУП «Горэлектротранс»** для закрытого контура Windows/Linux: одно окно, лаунчер, общие вкладки PDF, Word, Excel, презентаций и изображений. Исходный проект «ГЭТ PDF» сохранён как legacy-клиент, пока новый клиент не достигнет паритета.

> **Статус: интеграционная ветка / не релиз.** Настольный интерфейс и часть локальных операций реализованы, но сборка и функциональные испытания в настоящем Windows/Linux окружении **не выполнены**. Базовые Office-редакторы не обеспечивают полной совместимости с Microsoft Office. Collabora/ONLYOFFICE серверное редактирование, сохранение через GUP-2, WORM/SSO и эксплуатационная проверка **не завершены**. Нельзя менять корпоративные ассоциации файлов или выводить из эксплуатации GetPdf.exe до приёмки.

## Что находится в репозитории

| Компонент | Расположение | Реальный статус |
|---|---|---|
| Единый лаунчер, общие вкладки | `src/GetDocument.Desktop` | Avalonia, исходный код; проверить UI на Windows/Linux |
| Файловая маршрутизация, single-instance IPC | `src/GetDocument.Core` | Локальные пути через per-user named pipe; не передавать GUP-2 токены |
| PDF | `src/GetDocument.Desktop/Preview` | Просмотр отдельных растровых страниц; полноценная печать/формы/редактирование остаются в legacy WPF |
| DOCX, XLSX, PPTX | `src/GetDocument.Office` | Создание файлов и ограниченное редактирование простых DOCX/XLSX; не полноценный Office |
| XLS/XLSB, изображения, шаблоны, файлы | `src/GetDocument.Desktop` | Просмотр/базовые операции, ограничения зависят от формата |
| Редакции и комментарии | `src/GetDocument.Revisions` | Локальный прототип, не GUP-2 corporate revision API |
| Совместная работа | `src/GetDocument.Collaboration` | Контракты сессий, конкурентного CAS и неизменяемых редакций |
| WOPI HTTP шлюз | `src/GetDocument.WopiGateway` | Код CheckFileInfo/GetFile/PutFile, default deny; реальный ACL store отсутствует |
| WOPI host | `src/GetDocument.WopiHost` | Отдельный серверный процесс, **по умолчанию deny-all**; не часть desktop |
| Старое приложение «ГЭТ PDF» | `src/GetPdf` | Сохранено без удаления до подтверждения функционального паритета |
| «ГЭТ Сканирование» | отдельный репозиторий | Не встраивать TWAIN/WIA и драйверы в этот репозиторий; интеграция в лаунчере пока заглушка |

## Сборка и локальная проверка

Требуется .NET SDK 10 и предварительно одобренные офлайн NuGet-зависимости. GitHub Actions не включать. Для Linux:
```bash
bash scripts/verify-get-document-linux.sh
dotnet run --project src/GetDocument.Desktop/GetDocument.Desktop.csproj
```

Для Windows PowerShell:
```powershell
.\scripts\verify-get-document-windows.ps1
dotnet run --project .\src\GetDocument.Desktop\GetDocument.Desktop.csproj
```

Ручная проверка перед выпуском: один/два процесса и пользователя, многовкладочность, реальные OOXML, SMB, отсутствие перезаписи WORM, PDF-печать/формы/подписи, безопасное открытие из GUP-2, совместная работа 2/5/10 пользователей и контроль прав. Без успешных результатов **не маркировать release-ready**.

## Совместимость и миграция

Для тестовых Windows-стендов можно **добровольно зарегистрировать** новый клиент в меню «Открыть с помощью», не меняя приложение по умолчанию и старый `GetPdf.PDF`:
```powershell
.\packaging\register-get-document-capabilities.ps1 -ExePath "C:\Program Files\GET Document\GetDocument.Desktop.exe" -OpenSettings
```

Для Linux (Astra/ALT/РЕД и другие desktop-системы) предусмотрена регистрация в пользовательском каталоге приложений **без изменения MIME по умолчанию**:
```bash
bash packaging/register-get-document-linux.sh /opt/get-document/GetDocument.Desktop
bash packaging/test-register-get-document-linux.sh
```
Скрипт требует существующий исполняемый файл и добавляет `get-document.desktop` только для текущего пользователя. Shell smoke-тест выполняется в изолированном временном `XDG_DATA_HOME`. Упаковку и цифровую подпись дистрибутивов выполнить отдельно.

Регистрация не является установщиком, не развёртывает сервер и не включает реальное совместное редактирование. До подписанного релиза сохраняются прежние пользовательские ассоциации и legacy-приложение.

## Дополнение GD-13 — интеграция GUP-2 и навигация PDF (25.10.2026)

В интеграционной ветке [GD-13](docs/GET_DOCUMENT_GD13_RETURN_PDF_NAV_2026-10-25.md) объединены исходники серверно-зависимого возврата рабочей DOCX/XLSX-копии (PR #32) с GD-12 WebView/WOPI/OOXML (PR #33). При выключенной корпоративной политике кнопки возврата нет; токен GUP-2 в приложение не переносится, используется разовый код. При закрытии вкладки отправка отменяется на клиенте.

PDF-вкладка получила **перелистывание первой/последней страницы, поле перехода, Enter и Ctrl+G** с безопасной проверкой диапазона. Текстовое выделение, печать, PDF-редактирование и юридическая проверка подписей в новом Avalonia-клиенте **ещё не перенесены**. Все новые MSTest написаны, но отсутствие .NET SDK не позволяет подтвердить сборку.

## GD-14 — поиск в PDF и конфликт версий GUP-2 (25.10.2026)

В новой PDF-вкладке добавлен **поиск текстового слоя через существующий PdfPig**, а не внешний просмотрщик: `Ctrl+F`, `Enter`, `F3`, `Shift+F3`, переход между страницами, содержащими искомую фразу. Для полностью отсканированных файлов по-прежнему необходим OCR. **Подсветка отдельных глифов и полноценное редактирование/печать PDF ещё отсутствуют.**

Возврат DOCX/XLSX в GUP-2 теперь различает конфликт редакций `HTTP 409` (логика перенесена из PR #35). При конфликте **нет автоматического повторного отправления или перезаписи**. Копия остаётся у сотрудника.

Исходники и локальная приёмка: [GD-14 PDF text search](docs/GET_DOCUMENT_GD14_PDF_TEXT_SEARCH_2026-10-25.md). Сборка и MSTest ещё не выполнялись; GD-14 Draft.

## GD-15 — быстрый масштаб PDF без повторного рендера

Код ветки GD-15 добавляет масштабы 60/75/100/125/150%, кнопки `−`/`+` и `Ctrl++`/`Ctrl+-`/`Ctrl+0`. Масштаб индивидуален для каждой вкладки, применяется к уже готовому растру и не запускает PDFium заново. Это относительный масштаб **просмотра**, не размер при печати. [Подробности и приёмка](docs/GET_DOCUMENT_GD15_PDF_ZOOM_2026-10-25.md). Проверки сборкой и GUI ещё не выполнены.

## GD-16 — собственный мастер печати (25.10.2026)

Кнопка «Печать» и `Ctrl+P` для сохранённого PDF теперь открывают **собственную вкладку «Мастер печати ГЭТ»**, а не системный диалог. Внутри — три шага: выбор установленного принтера, диапазон/копии/A4/A3/дуплекс, подтверждение. Справа предварительно отображается первая PDF-страница.

Передача заданий: Windows — локальный headless WPF/XPS worker `GetDocument.PrintWindows.exe`; Linux — установленный CUPS (`lpstat`/`lp`). Установленные драйверы печати остаются необходимыми. Linux и Windows backends написаны, но на реальных принтерах ещё не проверены; Windows требует публикации worker вместе с desktop. **Это пока печать PDF, а не DOCX/XLSX/PPTX.**

Код, ограничения, сборка Windows и аппаратная приёмка: [GD-16 GET Print Wizard](docs/GET_DOCUMENT_GD16_CUSTOM_PRINT_WIZARD_2026-10-25.md). .NET SDK отсутствует в доступном исполнителе, сборка/MSTest не подтверждены.

План завершения с критериями приёмки: [GET_DOCUMENT_TRANSITION_RELEASE_GATE_2026-10-24.md](docs/GET_DOCUMENT_TRANSITION_RELEASE_GATE_2026-10-24.md). Исходное описание ГЭТ PDF: [LEGACY_GET_PDF_README_2026-10-24.md](docs/LEGACY_GET_PDF_README_2026-10-24.md).

## GD-17 — Euro-Office основной серверный движок (26.10.2026)

Для **ГЭТ Документы** основным редактором DOCX/XLSX/PPTX выбран **Euro-Office Document Server** (внутренний сервер, совместное редактирование). LibreOffice остаётся локальным автономным вариантом; ONLYOFFICE и Collabora — только legacy/альтернативные адаптеры. В отдельной Draft-ветке GD-17 добавлены `OfficeServerEnginePolicy`, `EuroOfficeSessionFactory` и MSTest: повторно используется контракт подписанной Docs API-конфигурации, но **без подключённого сервера/SSO/GUP-2 сохранения редакций сессии работают только в режиме просмотра**. Имеющийся PDF viewer и мастер печати остаются независимыми от Office-сервера. [Решение, безопасность и последовательность приёмки](docs/GET_DOCUMENT_EURO_OFFICE_PRIMARY_2026-10-26.md).

**GD-17B:** подготовлены отдельный HTTPS launch broker `GetDocument.EuroOfficeHost`, одноразовая 128-битная ссылка для WebView, CSP-защищённая страница Euro-Office DocsAPI и `DenyAll` до подключения GUP-2/SSO. Добавлены тесты и [инструкция по offline-стенду](docs/GET_DOCUMENT_EURO_OFFICE_BROKER_GD17B_2026-10-26.md). Эти изменения находятся в Draft PR #42, реальные корпоративные документы пока открыть через этот сервер нельзя.


**GD-17C.1:** [привязка текущей вкладки к GUP-2](docs/GET_DOCUMENT_GUP2_BINDING_GD17C_2026-10-26.md) через существующий одноразовый код и проверку SHA-256 для сохранённых DOCX/XLSX. Запрещённая по умолчанию отдельная ИТ-политика `AllowGup2TabBinding`; без передачи файла и без повышения прав. **Это проверка метаданных, а не работающая SSO-сессия Euro-Office**. 

**GD-17C.2:** [сервисный протокол аттестации GUP-2](docs/GET_DOCUMENT_EUROOFFICE_ATTESTATION_GD17C2_2026-10-26.md) с HMAC и строгой проверкой read-only метаданных; сопряжённый [GUP-2 PR #174](https://github.com/avers2604/Gup-2/pull/174). Вход в Euro-Office и выдача файлов ещё намеренно запрещены до реального SSO, ACL content gateway и immutable revision sink.

**GD-17D.1:** добавлен [строго проверяющий HMAC content клиент](docs/GET_DOCUMENT_EUROOFFICE_CONTENT_GD17D_2026-10-26.md) для чтения рабочих DOCX/XLSX только с закреплённой S3 VersionId после разрешения GUP-2 — связанный [GUP-2 PR #176](https://github.com/avers2604/Gup-2/pull/176). Передача до 64 МиБ в памяти, SHA-256 до выдачи байтов. Функция отключена по умолчанию; запуск Euro-Office, callback и сохранение новых версий ещё не подключены.

**GD-17D.2a — callback:** [контроль JWT и GUP-2 CAS](docs/GET_DOCUMENT_EUROOFFICE_CALLBACK_GD17D2_2026-10-26.md); новый [GUP-2 Draft PR #177](https://github.com/avers2604/Gup-2/pull/177). Нативный callback Euro-Office, отдельный JWT outbox, безопасное подтверждение статуса 2/6 и DеnyAll revision sink. Пока **запись не реализована**, 503 вместо ложного успеха, флаги off, реальные файлы/WORM и печать не затронуты.

**GD-17D.2b / GD-23:** [GUP-2 commit через штатные CAS/архив/AV](docs/GET_DOCUMENT_EUROOFFICE_COMMIT_GD17D2B_2026-10-26.md), отдельный [GUP-2 Draft PR #179](https://github.com/avers2604/Gup-2/pull/179). Там внесён внутренний реальный механизм фиксации новой рабочей версии, append-only журнал сохранений и replay-защита. Серверный обмен JWT/SSO и сетевой вызов commit ещё не подключены, `EuroOfficeHost` остаётся DenyAll.

**GD-24:** серверный C# [клиент HMAC-возврата рабочей Office-редакции](docs/GET_DOCUMENT_EUROOFFICE_SAVE_TRANSPORT_GD24_2026-10-26.md) и связанный [GUP-2 Draft PR #182](https://github.com/avers2604/Gup-2/pull/182) с независимым save-grant, подписанным document.key, whitelist внутренних peer, CAS/архивом и replay-ответами. Инициатор trusted SSO и защищённый Document Server download пока не подключены; callback host остаётся DenyAll.

**GD-25 Euro-Office:** [подписанный JWT callback, защищённая загрузка по закреплённому IP и обмен SSO intent](docs/GET_DOCUMENT_EUROOFFICE_TRUSTED_BROKER_GD25_2026-10-26.md). Не передавать секрет save-grant в UI. Проектный ASP.NET host по-прежнему DenyAll до доверенных callback/session stores и тестов.
