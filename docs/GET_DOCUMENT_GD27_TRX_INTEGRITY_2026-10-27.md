# GD-27 — целостность доказательств локальной приёмки TRX (27.10.2026)

## Контекст
Зависит от GD-26 (PR #51), GD-25 (PR #50) и остальной цепочки мастера печати. **Это не готовность к релизу**: все проверки выполняются локально в изолированной сети, GitHub Actions остаются выключенными. PR не включает независимую ветку Euro-Office (PR #42 / #52).

## Найденные случаи ложноположительной проверки GD-26
1. `ResultSummary outcome=""`, `Inconclusive`, `Cancelled` или произвольное значение не считалось ошибкой при полностью прошедших отдельных `UnitTestResult`.
2. `Counters executed="0"` при `passed="4"` не проверялось. Не сверялось присутствующее `notExecuted`.
3. Один и тот же валидный TRX можно было разложить по файлам нескольких suite без обязательных поименованных тестов (например Images и Revisions) и получить ложные независимые подтверждения.

## GD-27
- Принимаем как потенциально успешный итог TRX только `ResultSummary outcome=Completed` или `Passed`, регистр безразличен. Все остальные результаты, включая отсутствие outcome, блокируют gate.
- Проверяем `Counters.total`, `passed`, `failed`, `executed`: `passed + failed <= executed <= total`; если `notExecuted` есть, оно должно совпадать с `total - executed`. Результаты каждого теста по-прежнему проходят проверку на известные статусы и обязательные имена.
- У каждого `TestRun` должен быть ненулевой GUID `id`. GUID всех suite в пределах одной проверки Windows/Linux должны отличаться — нельзя считать копии одного запуска разными тестовыми проектами.
- В `get-document-release-gate.json` выводятся `RunId` каждого suite и `IndependentRuns`. Поле `Approved` может быть `true` только при независимых успешных отчётах. Нет передачи результатов в сеть.
- В `ReleaseGateSelfTests --self-test` добавлены 8 регрессионных сценариев, в том числе ложный summary, противоречивые счётчики, неправильный GUID и копирование TRX между наборами тестов.

## Локальная проверка (обязательна перед merge)

Linux/Astra:
```bash
bash scripts/verify-get-document-release-linux.sh /srv/get/nuget-feed
```

Windows:
```powershell
pwsh -File scripts/verify-get-document-release-windows.ps1 -OfflineNuGetFeed 'D:\\GET\\NuGetOffline'
```

Отдельный этап проверки gate после offline restore/build:
```bash
dotnet run --project tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj -c Release --no-build --no-restore -- --self-test
```

Проверить действительный MTP TRX (.NET 10, MSTest.Sdk), значения `ResultSummary`/счётчиков и разные `TestRun id` у каждого проекта. Затем провести аппаратную приёмку Windows и Astra на A4/A3/duplex/лотках/1–4 up, Office с несохранёнными правками, ACL/WORM/GUP-2. Даже успешный программный gate не свидетельствует о физической печати.

## Непроведённые испытания
В среде разработки отсутствуют `dotnet`, `csc`, `pwsh`, `mono`, локальная служба CUPS, МФУ. Статическое чтение исходников и публикация изменений **не означают** успешный build/self-test/MSTest; PR оставлять Draft до реальной лабораторной проверки.
