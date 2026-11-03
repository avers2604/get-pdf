# GD-31J.27am — исправления Windows IPC и имён сохраняемых файлов (без стенда)

**Статус:** отдельная Draft-ветка поверх GD-31J.27al / PR #151. Не включены реальные документы, корпоративные хранилища, WORM, запуск сервера, Linux privileged IPC, нативная публикация SaveAs и GitHub Actions. `main` не меняется.

## 1. Исправление Windows NamedPipe клиента

В `WindowsNamedPipeDenialLab.ReceiveDenialOnceAsync` ранее выполнялось `pipe.ReadMode = PipeTransmissionMode.Message` после `NamedPipeClientStream.ConnectAsync`.

По подтверждённой проблеме dotnet/runtime [#83072](https://github.com/dotnet/runtime/issues/83072), стандартный клиентский канал .NET Core+ на Windows может не иметь `FILE_WRITE_ATTRIBUTES`, необходимого для такого переключения. Поэтому работающий Windows IPC мог падать с AccessDenied ДО проверки deny-only ответа.

**Изменено:** клиент сохраняет начальный *byte-read mode* и вызывает `ReadDenialToEofAsync(Stream, CancellationToken)`. Метод:

1. Читает не более `Denied.Length + 1` байт;
2. Требует чистого EOF после ровно `Denied.Length` байт (правильный префикс без закрытия канала **не** считается успехом);
3. Отвергает усечённый или удлинённый ответ;
4. Использует существующий 30-секундный связанный `CancellationToken`; при зависшем peer — отказ;
5. Передаёт точное содержимое в существующий `RequireExactDenial` (без `state=saved`, разрешения записи, подтверждения релиза).

**Сервер** остаётся в `PipeTransmissionMode.Message`, по-прежнему требует `CurrentUserOnly|FirstPipeInstance`, PID фактического клиента через `GetNamedPipeClientProcessId`, точный запрос и итог `denied`. Процессы `client/server` по-прежнему должны запускаться вручную только на утверждённой полностью изолированной offline Windows VM; **оба всегда exit 2**.

**Источники платформенного поведения:** https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions ; https://learn.microsoft.com/dotnet/api/system.io.pipes.pipestream.ismessagecomplete ; https://github.com/dotnet/runtime/issues/83072 .

## 2. Файловые имена Windows и Linux

`OfficeCopyPublisher.ValidatePortableDestinationFileName` блокировал `COM1..9`/`LPT1..9`, но пропускал `COM¹`, `COM²`, `COM³`, `LPT¹`, `LPT²`, `LPT³` (надстрочные цифры ISO 8859-1). По документации Windows они также являются **зарезервированными именами устройств**, включая случай с расширением: https://learn.microsoft.com/windows/win32/fileio/naming-a-file .

Добавлена одинаковая для Windows и Linux проверка `"123456789¹²³".Contains(stem[3])`. Предпроверка перед `nativeExport` не позволяет движку начинать экспорт в недопустимую цель. Правило `File.Move(staging,target,overwrite:false)` и запрет записи в WORM не менялись.

## 3. Тесты без системных компонентов

- `tests/GetDocument.Office.Tests/WindowsNamedPipeDenialByteReadTests.cs`: **7 MSTest** на обычных `MemoryStream`/синтетическом `Stream` — точный ответ до EOF, чтение по одному байту, усечённый ответ, лишний байт, подмена содержимого, отмена, правильный префикс без EOF (ожидаем тайм-аут/отмену). Реальный Windows pipe для этих тестов **не нужен**.
- `tests/GetDocument.Office.Tests/OfficeCopyPublisherPlatformSafetyTests.cs`: дополнительные `DataRow` для шести зарезервированных Unicode-имён и отдельный тест, что перед отклонением не вызывается `nativeExport`.
- `probes/windows-saveas-denial/test_windows_named_pipe_denial_source.py`: регрессия, запрещающая повторное появление `pipe.ReadMode =`, и проверка bounded EOF-пути. Запускается штатной локальной программой приёмки ДО восстановления NuGet на обеих ОС.

Для имеющего репозиторий разработчика (без стенда):

```sh
python3 -m unittest discover -s probes/windows-saveas-denial -p test_windows_named_pipe_denial_source.py -v
```

Для сборки и C# unit тестов **после установки одобренного .NET 10 SDK и локального офлайн restore**, без реального Windows IPC:

```sh
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WindowsNamedPipeDenialByteReadTests|FullyQualifiedName~OfficeCopyPublisherPlatformSafetyTests'
```

`OfficeCopyPublisherPlatformSafetyTests` содержит **filesystem tests** с временными каталогами и symlinks, которые на Windows могут требовать специальных прав. Полностью in-memory — только `WindowsNamedPipeDenialByteReadTests`; они могут исполняться на Linux при доступном .NET 10.

## 4. Реально проверено и ограничения

- Файлы прочитаны обратно из GitHub; выполнены **16 статических утверждений** о наличии защиты, отсечении лишних байтов, EOF, cancellation, всех именах и отсутствии новых разрешений на сохранение.
- **Не запускались**: C# компиляция, MSTest, Windows named-pipe IPC, реальные NTFS/SMB/ext4, EuroOffice, GUP-2, продуктивные документы или системная приёмка. В текущем локальном исполнителе нет `dotnet`/Windows VM, а загрузка репозитория через сеть невозможна.
- Работа с низкопривилегированным Windows broker, проверенным handle-bound SaveAs, Linux 3-UID IPC и WORM/CAS остаётся отдельной. Клиентский байтовый парсер **не аутентифицирует серверный процесс**. Указанный `denied` не даёт полномочий на сохранение.

**Дальше:** при наличии approved .NET SDK прогнать локальные unit tests без Windows стенда, проверить сборку `GetDocument.Office`; потом в отдельно санкционированных VM завершить ручной Windows/Astra/SMB E2E. Все ручные гейты остаются `not-run`, `releaseReady=false`.
