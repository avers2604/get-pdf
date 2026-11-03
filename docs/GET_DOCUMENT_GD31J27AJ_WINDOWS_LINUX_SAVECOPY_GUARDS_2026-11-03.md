# GD-31J.27aj — общий Save As (новая копия) для Windows и Linux: защитные проверки

**Статус:** отдельная интеграционная Draft-ветка поверх GD-31J.27ai. Никакого разрешения на native SaveAs, WORM, корпоративный revision sink, EuroOffice production, релиз или merge.

## Что уже было

`src/GetDocument.Office/LibreOffice/OfficeCopyPublisher.cs` уже существовал в общем .NET 10 модуле для Windows/Linux и публиковал новую копию из `.getdoc-copy-<GUID>` в папке назначения вызовом `File.Move(..., overwrite: false)`. Не нужно создавать два независимых движка копирования. Существующий редакторский экспорт остаётся callback `nativeExport`, а не интегрированным успешным EuroOffice native SaveAs.

## Доработки

- `PublishAsync(destination, nativeExport, CancellationToken)`: отмена до запуска нативного экспорта, после экспорта и непосредственно перед финальным переименованием; отменённая копия не появляется как завершённая.
- Портативные имена: недопустимы альтернативные потоки NTFS (`:`), зарезервированные имена Windows (`CON`, `NUL`, `COM1`, `LPT9` и т. д.), управляющие/запрещённые символы и завершающие пробел/точка. Проверка одинакова на Windows и Linux, чтобы название было пригодно при переносе.
- По существующему пути назначения не перезаписываем документ или каталог. Если другой процесс создал файл пока экспортер записывал staging, `File.Move(overwrite:false)` должен завершиться отказом, сохранив файл другого пользователя.
- Рекурсивно проверяем родительские каталоги на `FileAttributes.ReparsePoint`/`LinkTarget` перед экспортом и повторно непосредственно перед публикацией; символические ссылки/junctions на пути назначения не допускаются. Не принимаем пустой файл или staging-символическую ссылку. При неудаче удаляется только временный файл/ссылка — **никогда не целевой документ и не каталог рекурсивно**.
- Новые локальные MSTest: отмена до/после экспорта; символьная ссылка staging; символьная ссылка папки назначения; гонка имени; запрет Windows ADS/устройств; разрешение обычных кириллических имён.

## Ограничения безопасности

Эта доработка **не даёт защиты от злонамеренного редактора**: он всё ещё получает путь staging и может устроить TOCTOU гонку/подмену или создать hardlink. Валидация не означает полноценное sandboxing, верификацию OOXML, ACL, SMB CAS, отсутствие links на уровне всех native OS handles или WORM writeback. Для враждебного экспорта нужны отдельный низкопривилегированный процесс, OS-handle-bound publication и независимая проверка содержимого. Пока единственный поддерживаемый сценарий — **создание новой пользовательской копии через предварительно доверенный редактор**. Текущая интеграция EuroOffice native SaveAs и Windows trusted IPC по-прежнему не аттестована.

## Локальная проверка — команды, не отчёт о прогоне

Windows (PowerShell, локальный одобренный .NET 10 и офлайн NuGet):
```powershell
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj --no-restore --filter "FullyQualifiedName~OfficeCopyPublisher" --logger "trx;LogFileName=gd31j27aj-windows.trx"
```

Linux (включая Astra после установки одобренного SDK):
```sh
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj --no-restore --filter 'FullyQualifiedName~OfficeCopyPublisher' --logger 'trx;LogFileName=gd31j27aj-linux.trx'
```

Перед `--no-restore` выполнить только разрешённый **локальный/offline restore** согласно существующему `scripts/office_acceptance.py`; тесты с symbolic link должны проводиться при наличии необходимого Windows privilege/developer mode. Принудительное отключение этих тестов недопустимо. Далее: весь `GetDocument.slnx`, проверка копии на NTFS, SMB, ext4 и реальном пользователе, interruption/crash и проверка открываемости OOXML.

**Факт этого изменения:** исходники + тесты + документация добавлены в GitHub. В доступном инструментальном окружении нет .NET 10 SDK и Windows/Astra стенда: не утверждаем успешную сборку, исполнение MSTest или Windows/Linux release.

**Следующий GD-31J.27ak:** после локальных тестов протоколировать Windows NTFS/SMB и Linux ext4 операции. Отдельно проектировать Windows authenticated named-pipe/handle-transfer и Linux SCM_RIGHTS в одном интерфейсе, не смешивая наблюдение transport с полномочиями на запись.
