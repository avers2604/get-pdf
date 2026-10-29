# GD-31J.33 — ревью совместимости GD-31J.26–32 и устранение .NET 10 blocker

**Дата:** 29 октября 2026. **Основа:** Draft PR #104 (GD-31J.32). **Тип:** совместимость исходников, синтетические тесты и документация. **Нет выполненной сборки или тестов.**

## Обнаружена блокирующая несовместимость

Проект `GetDocument.Office` объявляет `<TargetFramework>net10.0</TargetFramework>`, но GD-31J.29 и последующие шаги непосредственно вызывали `System.IO.File.CreateHardLink(destination, pending)`. Microsoft относит `File.CreateHardLink` и `FileInfo.CreateAsHardLink` к новым API **.NET 11**, появившимся в Preview 1 (2026): на стабильном net10.0 проект такой исходник не скомпилирует.

**Источники для повторной проверки при лабораторной сборке:**
- Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.io.file.createhardlink?view=net-11.0
- История dotnet/runtime: https://github.com/dotnet/runtime/issues/69030
- Релизные заметки .NET 11 Preview 1: https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview1/libraries.md

Это **исправление настоящего дефекта совместимости в исходниках**, но ещё не доказательство успешной компиляции и не повод менять TFM или включать новый нестабильный движок.

## Исправление без перехода на .NET 11

Добавлен `EuroOfficeNativeHardLinkNoReplace` — **internal-only** адаптер для изолированной synthetic lab. Он используется вместо `File.CreateHardLink` в `EuroOfficeSyntheticReviewedSnapshot.CommitCore`.

| Платформа | Реальный системный вызов | Поведение |
|---|---|---|
| Windows 10/11 | `CreateHardLinkW(newFileName, existingFileName, nullptr)` через `kernel32.dll` | Создать дополнительную запись того же файла; существующее конечное имя не заменять |
| Linux / Astra / ALT / РЕД | `link(oldPath, newPath)` через `libc` | Создать дополнительную запись в том же каталоге, отказывать при конфликте |
| Другая ОС | `PlatformNotSupportedException` | Ни копирования, ни переименования, ни перехода в другой режим |

Результат ошибки системного вызова: `IOException` и диагностический код `Marshal.GetLastPInvokeError()`. В исходниках **нет fallback на `File.Move`, `File.Copy`, `File.Replace`, перенаправление через shell или создание временной пользовательской копии**.

**Проверки до P/Invoke:** обязательные абсолютные пути, одинаковый исходный/конечный каталог и разные имена, запрет Windows UNC, существование исходной temporary-копии, проверка не только файлового пути, но и всех каталогов-предков через уже существующий `EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath`, отсутствие целевого имени по предварительной проверке. **Авторитетное решение о конфликте остаётся за системным вызовом**, а не за небезопасным `File.Exists` перед ним.

Нативная жёсткая ссылка по-прежнему требует поддержку ФС. Копия, где `pending` и `reviewed` — две ссылки на один inode/MFT record, **не WORM**, не atomic transaction с fsync каталога, не защищена от внешнего writer, не криптографическая подпись и не доказательство правильного OOXML.

## Синхронизация накопленного дерева

Согласованы три старых source contracts, проверявших вызов API, отсутствующего в .NET 10:

- `test_synthetic_reviewed_snapshot_contract.py` (GD-31J.29);
- `test_synthetic_snapshot_journal_contract.py` (GD-31J.30);
- `test_synthetic_fault_checkpoint_contract.py` (GD-31J.32).

Сохранён прежний строгий порядок `Begin → pending FileMode.CreateNew → Flush(true) → SHA/Journaling → adapter.Create → linked → File.Delete(onlyOwnPending) → completed`. Восемь внутренних управляемых исключений GD-31J.32 и неразрешённая по умолчанию лаборатория сохраняются.

Исходные документы GD-31J.29/30/32 обновлены, чтобы больше не рекомендовать `File.CreateHardLink` на `net10.0`.

## Регрессионные проверки: написаны, НЕ запускались

Новый `EuroOfficeNativeHardLinkNoReplaceTests.cs`:

- новая жёсткая ссылка на тот же локальный synthetic temp, без дополнительного копирования; изменение через исходный путь отображается через другое имя (это подтверждает именно hardlink семантику, не immutable-файл);
- повторное создание и заранее существующий файл/каталог: `IOException`, байты существующего файла не изменены;
- неверные пути, совпадение source/destination, другой каталог, отсутствующий source — явный отказ;
- symlink исходного файла, symlink каталога-предка, dangling symlink целевого имени — отказ без удаления чужих файлов.

Новый `test_net10_hardlink_compatibility_contract.py` проверяет TFM net10.0, отсутствие прямого вызова `File.CreateHardLink` в production source, наличие Windows/Linux системных entrypoints, повторного использования ancestor-path guards, отсутствие fallback на копирование/замену, наличие перечисленных тестов.

В локальную `scripts/office_acceptance.py` добавлены `cef-net10-hardlink-api-compatibility-source-tests` (до restore) и ручной `eurooffice-gd31j33-net10-windows-linux-hardlink-build-and-no-replace-vm`. Тестовая схема приёмки обновлена; GitHub Actions не включаются.

## Незакрытые риски и правила окончания работы

1. **Сборка .NET 10 и нативной CEF/Qt** не проводилась. В обязательной будущей VM проверить сигнатуры `DllImport`, точный `Marshal.GetLastPInvokeError`, Windows error codes 183/5/17, Linux `EEXIST/EACCES/EXDEV/ENOSPC`, NTFS/ext4/XFS и особенности Astra/ALT/РЕД.
2. **TOCTOU / hostile directory** по-прежнему возможен между проверкой путей, хешированием и системным `link`; только отдельная учётная запись, закрытые права и изолированная VM. Без этого метод нельзя делать production.
3. **Долговечность fsync каталога** и отключение питания не проверены; `DurableAcrossPowerLoss=false`, `SaveCompleted=false`, `AuthorizedForPublication=false` и `WormUpdated=false` сохраняются даже после лабораторного успеха.
4. **Документы MS Office** не проверены в независимом round-trip. Правильность ZIP/XML не означает сохранность форматирования Word/Excel/PowerPoint.
5. Требуется **последующее интеграционное ревью #98–#104** с настоящей сборкой и линтером, устранение предупреждений и проверка лицензий. Этот PR не снимает ранее утверждённое ограничение «проверки потом».

## Дальше

GD-31J.34: пакетное source review межмодульных CEF receipt/PInvoke/multi-target тестов, устранение достоверно выявленных несовместимостей. **Не создавать ещё десятки новых фич поверх несобранного кода**. После окончания согласованных изменений — локально собрать полный стек, прогнать unit/integration/regression и отдельно провести настоящий filesystem crash/powerloss audit.

**Только Draft поверх PR #104**, без изменений `main`, GitHub Actions, рабочего ГЭТ Документ, GUP-2/Nextcloud, WORM и корпоративных документов.
