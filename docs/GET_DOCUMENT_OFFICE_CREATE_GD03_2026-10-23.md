# GD-03: создание реальных DOCX, XLSX, PPTX

## Что добавлено
- Независимый `GetDocument.Office` (`net10.0`): один пакет `DocumentFormat.OpenXml` версии 3.5.1.
- Три исходно пустых файла: DOCX (параграф), XLSX (лист «Лист1»), PPTX (слайд, макет, master, тема).
- Для PresentationML используется официальный MIT-licensed образец Microsoft из Open-XML-SDK,
  файл `Office/Internal/PowerPointUtils.cs`, с сохранением copyright.
- Лаунчер создаёт черновик во вкладке. «Создать файл и сохранить как…»
  предлагает диалог для DOCX/XLSX/PPTX и создаёт действительный OOXML-файл.
- Новый файл строится сначала в отдельном временном файле **в той же папке**;
  потом используется `File.Move` **без overwrite**. Никогда не перезаписываем
  существующий исходник, общий файл или WORM-документ.
- После успешного сохранения вкладка сохраняет тот же ID и связывается с новым путём.
  PDF просмотр, печать, GUP-2 не менялись.

## Граница функциональности
Создание пакета не равно полноценному Word/Excel/PowerPoint:
- Содержимое нового файла открывается внешними современными офисными редакторами.
- Rich DOCX/XLSX/PPTX в самой Avalonia пока показывают пояснение, а не рендер.
- Сложное редактирование и сохранение существующего форматирования ещё не подключены.
- LibreOffice, ProEdit, старые DOC/XLS/PPT, печать, совместная работа — следующие отдельные лаборатории.
- Не вводим внешние сетевые зависимости или облачную обработку.
- «Онлайн Дурак» не меняется, никакой сетевой сервер не запускается.

## Приёмка перед merge
В рабочем окружении нет `dotnet` CLI; MSTest/компиляция C# не запускались.
Добавлены тесты: OOXML package shape, отсутствие overwrite, несовпадение
расширений, сохранение Tab ID и отказ от конфликтующих путей.
Проверить на Windows и Linux:
```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj -c Release
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release
```
Ручная приёмка: создать DOCX, XLSX, PPTX; открыть файлы LibreOffice
(и Microsoft Office на Windows); проверить диалог сохранения в SMB, отмену,
существующий путь, повторную попытку и сетевой отказ.
Отдельно запустить сравнение ProEdit/LibreOffice на русских реальных DOCX.
Не включать GitHub Actions, не сливать без успешной проверки.
