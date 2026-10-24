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

Регистрация не является установщиком, не развёртывает сервер и не включает реальное совместное редактирование. До подписанного релиза сохраняются прежние пользовательские ассоциации и legacy-приложение.

План завершения с критериями приёмки: [GET_DOCUMENT_TRANSITION_RELEASE_GATE_2026-10-24.md](docs/GET_DOCUMENT_TRANSITION_RELEASE_GATE_2026-10-24.md). Исходное описание ГЭТ PDF: [LEGACY_GET_PDF_README_2026-10-24.md](docs/LEGACY_GET_PDF_README_2026-10-24.md).
