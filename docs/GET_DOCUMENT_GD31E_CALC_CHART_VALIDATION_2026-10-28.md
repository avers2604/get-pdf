# GD-31E — диаграммы и проверка данных в ГЭТ Таблицы

Дата: 28.10.2026. Задача: #59. Ветка `feature/gd31e-calc-chart-validation`, базовая `feature/gd31d-templates-calc-print`. **Draft; не выпускать без локальной приёмки.**

## Повторно используемые движки
Расширен существующий Windows LibreOffice / UNO fallback. Отдельный табличный движок не создаётся, пользователь видит только собственные меню GET DS 4.1. Сервер EuroOffice / Nextcloud / GUP-2 и авторизация не затрагиваются. Для них требуется отдельный адаптер capabilities.

## Реализовано в исходниках

| GET команда | Область действия | Вызов |
| --- | --- | --- |
| Столбчатая диаграмма | Непрерывное выделение, ≥2×2, содержит числа, максимум 5000 ячеек | `XTableCharts.addNewByName`, `com.sun.star.chart.BarDiagram` |
| Линейная диаграмма | Те же условия | `XTableCharts.addNewByName`, `com.sun.star.chart.LineDiagram` |
| Только целые ≥0 | Текущее выделение, максимум 50000 ячеек | `ValidationType.WHOLE`, `ConditionOperator.GREATER_EQUAL` |
| Только числа ≥0 | То же | `ValidationType.DECIMAL`, `GREATER_EQUAL` |
| Снять проверку данных | То же | `ValidationType.ANY` |

Перед созданием диаграммы проверяется наличие числовых данных; максимум **40 диаграмм на активном листе**, случайное неполное создание откатывается через `removeByName`. Для ограничений ввода используется стандартная ошибка `ValidationAlertStyle.STOP`. Правила задаются только **по явному выбору пользователя**, применяются к выделению и могут заменить ранее существовавшие правила в этом диапазоне. Данные в исходных ячейках не очищаются.

## Защита и управление
- Реестр `OfficeFeatureCatalog` определяет тип Calc и `RequiresEdit=true`.
- Python `FEATURES` независимо повторяет запреты и разрешает только фиксированные идентификаторы; не выполняет пользовательские arbitrary UNO URLs, файлы или макросы.
- Один диапазон и принадлежность активному листу обязательны; 5000 / 50000 — верхние пределы действий. Диаграммы на 40+ уже имеющихся объектах не добавляются.
- Печатать можно через существующий мастер ГЭТ. Прямой вызов системного диалога не добавлялся.
- Локальное автоматическое восстановление GD-31A сохраняется.

## Тестирование
Добавлено:
- `OfficeFeatureCatalogTests`: наличие/уникальность/тип/read-only нового набора, whitelist встроенного Python.
- `CalcChartAndValidationTests`: unit-тест запрета чужих и read-only операций и **интеграционный** сценарий с настоящим LibreOffice: создать XLSX с числовыми значениями, выбрать A1:B4, создать два типа диаграмм, применить/сбросить правило, сохранить и проверить, что в OOXML присутствуют drawing и dataValidation.

**Статус:** C#/Python/UI/тесты записаны, но локальный `.NET 10` SDK отсутствует, сеть контейнера не позволяет клонировать репозиторий. Приёмка не проведена. Часть UNO методов может требовать корректировки после первого запуска; не считать их проверенными.

На рабочем Windows-ПК с GETDOC_SOFFICE:

```powershell
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CalcChartAndValidationTests|FullyQualifiedName~OfficeFeatureCatalogTests"
.\scripts\verify-get-document-windows.ps1
```

Затем проверить на реальных многолистовых XLSX (включая формулы, диаграммы и фильтры), совпадение результата в Microsoft Excel и LibreOffice, повторное открытие, редактирование на сетевом диске, read-only/WORM, внешние связи и макросы. Несовпадения OOXML не скрывать. Для сложных сводных таблиц и настраиваемых списков требуется следующий этап с отдельной спецификацией.

## Источники UNO API
- https://api.libreoffice.org/docs/idl/ref/servicecom_1_1sun_1_1star_1_1sheet_1_1TableValidation.html
- https://api.libreoffice.org/docs/idl/ref/namespacecom_1_1sun_1_1star_1_1sheet.html
- https://wiki.openoffice.org/wiki/Python-Calc
