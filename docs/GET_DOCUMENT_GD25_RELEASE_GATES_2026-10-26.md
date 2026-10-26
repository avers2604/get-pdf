# GD-25 — проверяемая локальная приёмка и формальный release gate ГЭТ Документ

Дата: 26.10.2026. База: GD-24 / PR №49. Статус: Draft.

## Цель

После цепочки GD-16–GD-24 проверки выполнялись через dotnet test, но успешный exit code не гарантировал прохождение **обязательных** сценариев. Некоторые MSTest-тесты могут завершаться `Inconclusive` из-за отсутствия LibreOffice/драйвера или запускаться не в той ОС, а сборка приложения при этом формально выглядеть успешной.

GD-25 добавляет **независимый формальный выпускной барьер**, анализирующий реальные TRX-файлы Microsoft.Testing.Platform/MSTest.Sdk.

## Изменения

1. `tools/GetDocument.ReleaseGate` — консольная программа без сторонних NuGet-пакетов. Разбирает локальные `Core.trx`, `Office.trx`, `Printing.trx`, `Images.trx`, `Revisions.trx`, `Collaboration.trx`, `WopiGateway.trx`; на Windows дополнительно `LegacyPdf.trx`.
2. Каждый проект должен иметь хотя бы один реально прошедший тест, **ноль** тестов в состояниях Failed/Error/Timeout/Aborted и отчёт в каталоге текущей приёмки. Отсутствующий файл блокирует релиз.
3. **Критические тесты должны иметь статус Passed**. Простое существование теста или `Inconclusive` не считается прохождением. Пример: SHA-256 и заморозка PDF, правильный CUPS request-id и безопасный argv, защита от переполнения драйвера; на Windows обязательный тест **несохранённый Office → PDF → PrintFileSnapshot**. На Linux дополнительно mandatory fake-CUPS process tests.
4. Приёмщик пишет локальный `get-document-release-gate.json` с решением `Approved = true/false`, счётчиками по проектам и перечнем не прошедших критических случаев; при ошибке возвращает ненулевой код.
5. Два новых скрипта запускают сначала проверку строго из офлайн NuGet-каталога GD-24, затем MSTest со штатным TRX-reporting. Не используются GitHub Actions, системный PrintDialog или публичные NuGet-серверы.
6. Исправлена устаревшая надпись в мастере: после GD-19 предпросмотр действительно использует выбранный диапазон PDF, а не всегда первые страницы.
7. Консольный инструмент включён в `GetDocument.slnx`; проверяется сборкой вместе с продуктом.

## Команды

Astra Linux / ALT / РЕД (локальный стенд, .NET 10 SDK, закрытая поставка NuGet):

```bash
bash scripts/verify-get-document-release-linux.sh /srv/get/nuget-feed
```

Windows (PowerShell 7, .NET 10 SDK, разрешённый локальный либо съёмный носитель):

```powershell
pwsh -File scripts/verify-get-document-release-windows.ps1 -OfflineNuGetFeed 'D:\GET\NuGetOffline'
```

Результаты сохраняются в пользовательском каталоге:
- Linux: `${XDG_STATE_HOME:-~/.local/state}/GET-Document/ReleaseReports/gd25-linux-...`, права `0700` для каталога.
- Windows: `%LOCALAPPDATA%\GET-Document\ReleaseReports\gd25-windows-...`; унаследованные права профиля.
- Не помещайте TRX с пользовательскими путями в общий git checkout, не отправляйте их во внешние системы.

## Основание для способа получения TRX

Репозиторий использует `global.json` с `Microsoft.Testing.Platform` и `MSTest.Sdk/4.5.1`. Для MTP используются **`--report-trx`** и `--report-trx-filename`, а не VSTest `--logger trx`. Опция `--results-directory` задаёт место отчёта. MSTest.Sdk default profile включает TRX extension.

Официальная документация:
- https://learn.microsoft.com/en-us/dotnet/core/testing/migrating-vstest-microsoft-testing-platform
- https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-platform-extensions-test-reports

## Критерии допуска к выпуску

- В Linux все обязательные CUPS-тесты действительно **Passed**, а не Inconclusive.
- В Windows реальный LibreOffice доступен, интеграционный тест с несохранёнными изменениями **Passed**, legacy GetPdf tests прошли.
- Нет Failed/Error/Timeout/Aborted ни в одном из требуемых наборов.
- `GetDocument.ReleaseGate` возвращает 0 и создаёт JSON с `Approved=true`.
- Отдельно: пройдены **физические испытания** A4/A3, 1/2/4-up, дуплекс, очередь Windows и CUPS, качество шрифтов кириллицы, цветность, лотки, бумага, WORM-оригиналы и аварийное завершение.
- Документация с результатами и подписью приёмки сохраняется внутри закрытого контура, не в GitHub Actions.

## Статус работ

Скрипты и проверяющая программа сохранены в новой ветке, но в текущем исполнении **нет dotnet 10, pwsh, CUPS и физического МФУ**. Поэтому ни сборка, ни тестовый TRX, ни успешный JSON **не получены и не заявляются как пройденные**. Это новый автоматический инструмент для будущей локальной проверки, не разрешение сливать PR и не подтверждение готовности релиза.

Серверная ветка Euro-Office PR №42 остаётся отдельной от этого локального release gate до проверки GUP-2 прав, версии документа и доверенного PDF-экспорта.
