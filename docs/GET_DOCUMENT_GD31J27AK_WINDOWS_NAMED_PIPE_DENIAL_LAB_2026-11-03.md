# GD-31J.27ak — Windows IPC deny-only, параллельно Linux SCM_RIGHTS

**Состояние:** проектный лабораторный код в stacked Draft поверх GD-31J.27aj / PR #149. **Не производственный Save As и не доказательство защищённой изоляции.** Никаких пользовательских DOCX/XLSX/PPTX/PDF, WORM/SMB, GUP-2, Nextcloud, реального publication или автоматического запуска служб.

## Задача и отличие от Linux

Linux GD-31J.27ah/ai использует Linux AF_UNIX/SOCK_SEQPACKET/SCM_RIGHTS и ручные три роли с **разными UID**; ручной E2E в Astra VM ещё не подтверждён. На Windows нужен *отдельный* интерфейс OS IPC. Этот этап реализует минимальный **двухпроцессный** Windows named-pipe deny-only smoke test, не пытаясь перенести Unix FD или выдавать аналогию привилегированных брокеров.

Общий кроссплатформенный алгоритм создания **новой копии** через доверенный редактор уже существует в `OfficeCopyPublisher` (GD-31J.27aj). Он не подключён к этому лабораторному IPC и не превращает native SaveAs в production.

## Исходники и наблюдения

- `src/GetDocument.Office/EuroOfficeDesktop/WindowsNamedPipeDenialLab.cs`: фиксированное имя `getdoc-gd31j27ak-denial-<guid N>`, жёстко заданный синтетический ASCII request и JSON denial (все разрешения строго `false`); ручной Windows-only runtime. Запрос не содержит пути, файла, HANDLE или документа.
- Server создаёт **один** `NamedPipeServerStream` с `PipeOptions.CurrentUserOnly|FirstPipeInstance|Asynchronous`, message mode, таймаутом 30 секунд; сверяет `GetNamedPipeClientProcessId(SafePipeHandle)` с введённым отдельно `pinnedClientPid` и PID самого сервера. При нарушении — отказ без ответа об успешном сохранении.
- Client создаёт `NamedPipeClientStream(".", ..., CurrentUserOnly|Asynchronous)`, подключается к тому же Windows-пользователю и уровню elevation, отправляет только фиксированный запрос и принимает **только** точный denial response.
- `probes/windows-saveas-denial/Program.cs`: отдельно запущенные **вручную**, не дочерние процессы. Только `client` и `server`, точный `GETDOC_GD31J27AK_VM=OFFLINE_DISPOSABLE_NO_DOCS` и ручная фраза `I_APPROVE_DISPOSABLE_WINDOWS_VM_NO_DOCUMENTS_DENIAL_ONLY`. При подтверждённом отказе печатает `*_NOT_ATTESTED`; **любой исход возвращает exit 2**. Флаги — лишь защита от случайного запуска, а не проверка изоляции.
- `tests/GetDocument.Office.Tests/WindowsNamedPipeDenialLabTests.cs`: чистые unit tests протокола, PID-ограничения, отрицательные случаи повреждённых и длинных пакетов, точный denial; не открывают реального named pipe.
- `probes/windows-saveas-denial/test_windows_named_pipe_denial_source.py`: source-only audit без OS-операций. `scripts/office_acceptance.py` выполняет этот тест до восстановления пакетов и на Windows добавляет offline restore/build отдельного проекта. Новая ручная матрица: `eurooffice-gd31j27ak-windows-currentuser-pid-denial-manual-e2e`.

## Ручной OFFLINE Windows VM запуск — *не производственный*

1. **Независимо** подготовить disposable Windows 10/11 VM, убедиться в отсутствии доступа к корпоративным дискам, пользователям, VPN, SMB, документам, общим папкам и сети. Установить одобренный локальный .NET 10 SDK/рантайм и только разрешённый офлайн NuGet feed. Нельзя запускать данный probe на рабочей станции.
2. В VM локально: `dotnet restore probes/windows-saveas-denial/GetDocWindowsPipeDenialLab.csproj --source <approved-offline-feed> -p:NuGetAudit=false`, затем `dotnet build probes/windows-saveas-denial/GetDocWindowsPipeDenialLab.csproj -c Release --no-restore`. Проверить локально хэши исходников и артефактов; это ещё не независимая аттестация Windows image.
3. В **терминале 1** (отдельный Windows процесс, тот же непривилегированный Windows пользователь/elevation):
   ```powershell
   $env:GETDOC_GD31J27AK_VM='OFFLINE_DISPOSABLE_NO_DOCS'
   $run='632a91807f1149f0b77db8aa1fb00417'
   dotnet .\probes\windows-saveas-denial\bin\Release\net10.0\GetDocWindowsPipeDenialLab.dll client $run I_APPROVE_DISPOSABLE_WINDOWS_VM_NO_DOCUMENTS_DENIAL_ONLY
   ```
   Считать **PID, фактически напечатанный** как `GD31J27AK_CLIENT_PID=...`. Client будет ждать server не более 30 секунд.
4. В **терминале 2**, запущенном отдельно в том же Windows пользователе/уровне elevation (не запускайте один процесс другим):
   ```powershell
   $env:GETDOC_GD31J27AK_VM='OFFLINE_DISPOSABLE_NO_DOCS'
   $run='632a91807f1149f0b77db8aa1fb00417'
   $pidFromClient=12345  # заменить точным значением из терминала 1
   dotnet .\probes\windows-saveas-denial\bin\Release\net10.0\GetDocWindowsPipeDenialLab.dll server $run $pidFromClient I_APPROVE_DISPOSABLE_WINDOWS_VM_NO_DOCUMENTS_DENIAL_ONLY
   ```
5. Наблюдаемые маркеры при exact deny exchange: `GD31J27AK_CLIENT_DENIAL_OBSERVED_NOT_ATTESTED`, `GD31J27AK_SERVER_DENIAL_SENT_NOT_ATTESTED`, **exit 2 у обоих**. При wrong PID, invalid packet, чужом пользователе, другом elevation, timeout, уже существующем pipe или отсутствии gate маркеры не должны подтверждать обмен. Каждый тест — новый runId/VM; нет заявленной межсеансовой anti-replay гарантии.
6. Unit tests `dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WindowsNamedPipeDenialLabTests`, затем все local acceptance шаги. Источник `python -m unittest discover -s probes/windows-saveas-denial -p test_windows_named_pipe_denial_source.py` можно отдельно проверить и на Linux.

## Ограничения

- **Не сравнивать** `PipeOptions.CurrentUserOnly` с Linux-брокером под отдельным UID: это тот же пользователь Windows и уровень elevation, а не изоляция по privilege boundary. `pinnedClientPid` вводится оператором: kernel PID доказует только connected PID, но не exe-hash, code identity, подписанную политику, AppContainer, Job Object или доверенную VM.
- На Windows **не реализована** передача безопасного document HANDLE между разными привилегиями, OS token restricted broker/worker, проверка реального нативного EuroOffice SaveAs, журнал CAS, защита от race/TOCTOU или SDK/WORM publication.
- Windows binary не включён в производство/установщик или startup. `main` не меняется; никакие CMake publication-gates Linux не снимаются.
- **Тесты и сборки в текущем чате фактически НЕ запускались**: локальная доступная среда Linux не содержит .NET SDK и Windows VM. Присутствие тестов и регистрационных маркеров не является пройденной Windows лабораторией.

**Далее:** отдельная локальная сборка Windows/VM E2E, проверка неправильного PID и токена, NTFS/SMB/временных файлов для общей новой копии; затем проектирование audited Windows broker с отдельной идентичностью и handle-bound transport без расширения прав сохранения до независимой приёмки.
