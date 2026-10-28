# GD-31C — расширение собственных панелей офисного редактора, 28.10.2026

**Статус:** подготовлено в отдельной интеграционной ветке поверх GD-31A / PR #60; не релиз и не доказательство полного MS Office-паритета. Основное решение об EuroOffice для корпоративных сеансов сохраняется. Настоящий этап реализует **локальный Windows LibreOffice/UNO fallback** в Avalonia-вкладках, не меняет EuroOfficeHost и не требует сервера.

## Новые функции

| Движок | Элемент GET DS | Команда | Встроенный API |
| --- | --- | --- | --- |
| Writer | Оглавление → Вставить | `writer-toc-insert` | `com.sun.star.text.ContentIndex`, `insertTextContent`, `update` |
| Writer | Оглавление → Обновить | `writer-toc-update` | `DocumentIndexes.getByIndex(i).update()` |
| Calc | Закрепление → Верхняя строка | `calc-freeze-top-row` | `XViewFreezable.freezeAtPosition(0, 1)` |
| Calc | Закрепление → Первый столбец | `calc-freeze-first-column` | `XViewFreezable.freezeAtPosition(1, 0)` |
| Calc | Закрепление → Снять | `calc-unfreeze` | `XViewFreezable.freezeAtPosition(0, 0)` |
| Calc | Пересчитать | `calc-recalculate` | `XCalculatable.calculateAll()` |
| Impress | Копия слайда | `impress-duplicate-slide` | `XDrawPagesDuplicator.duplicate(XDrawPage)` |

Никакая функция **не запускает собственный диалог LibreOffice**: используется специализированный UNO API. Для оглавления используются стили заголовков, существующая структура и выбранная позиция курсора. При выборе первой колонки после верхней строки новое закрепление **заменяет** предыдущее (не комбинирует оба варианта).

## Безопасность и UX

- `OfficeFeatureCatalog` хранит явный список команд (ID, название, kind, RequiresEdit). Запрещены чужой тип документа, произвольный идентификатор и изменения в read-only.
- Отдельный `op_feature` в существующем `lo_editor.py` **повторно** проверяет whitelist/тип/read-only до вызова UNO.
- GET DS 4.1: команды размещены в существующей панели, видимость определяется типом открытого документа; недоступные write controls отключаются на read-only.
- Сохраняются единые вкладки и мастер печати ГЭТ. Нет новых серверов, макросов, внешних URL, дополнительных NuGet-пакетов и автоматического доступа к WORM.
- Новый список не утверждает поддержку сложных полей DOCX, VBA, всех моделей условного форматирования XLSX или полного round-trip PPTX. Эти функции требуют отдельной проверки на реальных документах.

## Автоматизированная приёмка

- MSTest `OfficeFeatureCatalogTests`: соответствие активному редактору, уникальность ID, отказ от чужих/неизвестных операций, read-only ограничения, одинаковые ID в C# и встроенном Python bridge.
- `ExecutesDirectUnoActionsWhenLibreOfficeInstalled`: реальный Windows/LibreOffice, создание DOCX/XLSX/PPTX, последовательное выполнение новых команд, сохранение Word и PowerPoint.
- **В этом сеансе:** сопоставлены семь ID через GitHub connector по C#, UI, Python и тестам (7/7 в каждом месте), но **это только статическая проверка**. .NET 10 SDK и Windows LibreOffice отсутствуют, MSTest/GUI/реальная бинарная сборка **не запускались**.

На утверждённом тестовом Windows ПК, где доступен `GETDOC_SOFFICE`, выполнить локально:

```powershell
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~OfficeFeatureCatalogTests
.\scripts\verify-get-document-windows.ps1
```

Ручная приёмка: DOCX с заголовками 1–3 и обновлением оглавления; XLSX с формулами, закреплением и сохранением, сравнение открытия в Microsoft Excel; PPTX с фигурами, дублированием слайда, сохранением и сравнением с MS PowerPoint. Проверить права read-only, сетевые файлы и восстановление GD-31A.

### Официальные ссылки UNO

- https://api.libreoffice.org/docs/idl/ref/interfacecom_1_1sun_1_1star_1_1sheet_1_1XViewFreezable.html
- https://api.libreoffice.org/docs/idl/ref/interfacecom_1_1sun_1_1star_1_1drawing_1_1XDrawPageDuplicator.html
- https://api.libreoffice.org/docs/idl/ref/interfacecom_1_1sun_1_1star_1_1text_1_1XDocumentIndex.html

**Остаётся до merge:** Windows E2E, UI-проверка ввода и фокуса, DOCX/XLSX/PPTX round-trip, локальное тестирование на слабых ПК. Для EuroOffice серверной ветки потребуется отдельный адаптер этих же функций, без прямого переноса неподдерживаемых UNO операций в Docs API. GitHub Actions не запускать.
