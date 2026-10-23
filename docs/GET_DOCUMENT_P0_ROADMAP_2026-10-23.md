# ГЭТ Документ — P0, 23 октября 2026

## Состояние
Это первый небольшой инкремент архитектуры, а не готовый релиз.
Основа — feature/pdf-workspace-improvements (Draft PR #2 поверх Draft PR #1).
Никаких изменений GUP-Scan, GUP-2 и GET-system нет. Не сливать PR до проверки.

## GD-01 (исходники в текущем PR)
- Перенесены DocumentSession и DocumentPageEntry в независимый net10.0 проект GetDocument.Core.
- Сохранено старое пространство имён GetPdf для совместимости вызывающего WPF-кода.
- Добавлены только интерфейсы IPlatformPrintService, IPlatformFileAssociationService, IPlatformSecretStore.
  **Адаптеры НЕ реализованы, существующая печать не заменялась.**
- Добавлен классификатор расширений файлов, который НЕ открывает файлы, не проверяет сигнатуры
  и не доказывает поддержку формата. Конвертация DOC/XLS/PPT должна создавать новые рабочие копии.
- Добавлены MSTest с регрессиями Undo/Redo, распознавания форматов и ограничений печати.

## GD-02 — Avalonia (следующий P0)
Изолированный GetDocument.Desktop с вкладками, просмотром, пустым состоянием и командами печати.
Перенос WPF PDF backend в отдельные платформенные адаптеры с E2E проверкой.
Не заявлять поддержку печати в Avalonia, пока она не реализована и не протестирована.

## GD-03 — Office (P0)
Отдельная лаборатория ProEdit / LibreOffice: реальные DOCX на русском, сложные таблицы, колонтитулы,
правки, шрифты, сравнительный рендер и сохранение. Правила лицензий перед внедрением.
Старые Office форматы переводить в копии только после анализа содержимого.

## GD-04 — Файлы (P0)
Работа с SMB-папками Windows Server 2016 под личными ACL сотрудника.
Linux/Samba запланирован. Не сохранять UNC пути/имена по умолчанию; не использовать
FileSystemWatcher как единственный источник информации, периодически пересканировать.
Контроль reparse points, файлов-ловушек, ACL, отмены и сетевых ошибок.

## GD-05 — Шаблоны (P0)
Scriban в ограниченном окружении, версии корпоративных шаблонов и аудит.
Scriban НЕ является полноценным DOCX renderer.

## GD-06 .. GD-10
ExcelDataReader, изображения/NetVips/OpenCV, версионирование и комментарии,
серверное совместное редактирование, локальные интеллектуальные инструменты.
Существующее WORM и GUP-2 не менять без отдельной интеграционной приёмки.
Web.Contracts — только заглушка, без веб-сервера.

## Проверенный фактический набор зависимостей старого WPF
NAPS2.Sdk 1.4.0; NAPS2.Images.Wpf 1.4.0; NAPS2.Tesseract.Binaries 1.4.0;
NAPS2.Pdfium.Binaries 1.3.0; PDFsharp-WPF 6.2.4; PdfPig 0.1.16;
Aspose.PDF.FOSS 26.10.0; CommunityToolkit.Mvvm 8.4.2;
Serilog 4.4.0; Serilog.Sinks.File 7.0.0; MSTest.Sdk 4.5.1.
В этом PR **нет новых сторонних NuGet зависимостей**.

## Локальная приёмка
В доступном окружении нет dotnet SDK, поэтому dotnet build/test не запускались.
Прежний отчёт 185/185 MSTest не распространяется на новые файлы.
На Windows:
```
dotnet build GetPdf.slnx -c Release
dotnet test tests/GetPdf.Tests/GetPdf.Tests.csproj -c Release
dotnet test tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj -c Release
```
На Linux:
```
dotnet build src/GetDocument.Core/GetDocument.Core.csproj -c Release
dotnet test tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj -c Release
```
После компиляции обязательны локальные UI тесты Windows PDF: вкладки, OCR, аннотации,
печать, черновики и запуск MSI. Никаких GitHub Actions.
