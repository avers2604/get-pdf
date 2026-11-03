# GD-31J.27bc — проверка подмены пути непосредственно после открытия recovery-файла

**Репозиторий:** `avers2604/get-pdf`. **Ветка:** `feature/gd31j27bc-recovery-preopen-identity-guard`. Отдельный stacked Draft поверх GD-31J.27bb / PR #167.

**Ограничения пользователя:** не собирать, не запускать Python unittest / MSTest, не проводить Windows/Astra/SMB-стенд. `main`, GitHub Actions, WORM-оригиналы, GUP-2, native EuroOffice SaveAs и реальные пользовательские документы не изменяются.

## Уязвимый промежуток

GD-31J.27bb обеспечил единый поток для ZIP/XML и SHA-256. Тем не менее в `OpenRecoveryReadStream` проверка `FileInfo` предшествовала вызову `new FileStream(path, ...)`. Если путь изменится **в этом промежутке**, открытый дескриптор может указывать на другой файл, в том числе на цель символической ссылки. Простой `FileShare.Read` не даёт атомарного no-follow/проверки File ID.

### Подтверждение возможностей стандартной .NET API

- `File.OpenHandle` может вернуть `SafeFileHandle` для пути, но сам по себе не является документированным кроссплатформенным `O_NOFOLLOW` или identity-check.
- В стандартном `FileOptions` для используемой .NET версии нет публичного готового флага `OpenReparsePoint` / `DisallowReparsePoint`; полагаться на неподдерживаемое числовое значение нельзя.
- `FileSystemInfo.LinkTarget` поддерживает символьные ссылки и junctions, но проверяет **имя пути**, не тождественность уже открытого объекта.
- Для полноценной нативной защиты потребовались бы отдельные Windows handle/file-ID и POSIX `openat` + `O_NOFOLLOW`/`fstat` адаптеры, безопасное открытие родительского каталога и документированная работа на SMB. До компиляции и платформенных испытаний **никакие P/Invoke и неподтверждённые нативные флаги не добавляются в production**.

Справочные материалы:
- https://learn.microsoft.com/dotnet/api/system.io.file.openhandle
- https://learn.microsoft.com/dotnet/api/system.io.fileoptions
- https://learn.microsoft.com/dotnet/api/system.io.filesysteminfo.linktarget
- https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/IO/FileOptions.cs.html

## Точечное усиление, реализованное сейчас

`OpenRecoveryReadStream` по-прежнему открывает файл **ровно один раз**:

```csharp
new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ...);
```

Теперь он:

1. До открытия требует существующий обычный файл, ограничивает размер `MaxCopyBytes=128 MiB`, отвергает link/reparse. Запоминает длину и `LastWriteTimeUtc`.
2. Только для детерминированных synthetic MSTest допускает **internal test hook** `beforeOpen` непосредственно после preflight, но до `new FileStream`. Production-вызовы не предоставляют callback.
3. **Сразу после открытия и до чтения любых байтов** заново вызывает `RejectLinkedDirectories(parent)`, `FileInfo.Refresh()` и `IsLinkedFile`; отвергает исчезновение пути, ссылку/reparse-point, каталог/устройство, изменение длины/mtime или несовпадение длины открытого потока с preflight. Результат — `InvalidDataException` (либо системная ошибка доступа).
4. При любой ошибке после открытия немедленно закрывает `input?.Dispose()` в `catch` и передаёт исходное исключение; не оставляет потенциальный дескриптор на неизвестный объект.
5. При успехе возвращает тот же `FileStream`, который дальше используется для SHA-256 и обязательной ZIP/XML-проверки. В existing `CaptureAsync` и `ReadValidCopy` порядок собственных публикаций, исходники и WORM не изменены.

**Существенная граница:** этот код всё ещё открывает *путь* через стандартный `FileStream`. При подмене символьной ссылкой ОС может **уже открыть её внешний target**, прежде чем post-open проверка обнаружит ссылку. Код закрывает дескриптор **без чтения содержимого**, но не даёт гарантии «внешний файл даже не открывался». При замене обычным файлом одинаковой длины и mtime, при контроле inode/hardlink или при атаке на родительские пути, которая возвращает старые атрибуты, доказательств идентичности нет. На Windows кооперативная защита `FileShare.Read` не тождественна Linux/SMB. Называть это полным OS-handle-bound no-follow **нельзя**.

## Написанные тесты (НЕ запускались)

Добавлен `tests/GetDocument.Office.Tests/OfficeRecoveryPreOpenIdentityTests.cs`, **4 C# MSTest**:

1. `UnchangedRegularCopyOpensReadOnlyThroughTheGuard`: неизменённый обычный файл открыт read-only и прочитан.
2. `SymlinkInsertedAfterPreflightIsRejectedEvenWithIdenticalTargetBytes`: после preflight путь меняется на ссылку к независимому файлу **с идентичными байтами**; даже совпадающий SHA не может заменить проверку ссылки; цель не изменяется.
3. `RegularFileRewrittenAfterPreflightIsRejectedWithoutReading`: файл переписан после preflight другим размером и должен быть отклонён до чтения.
4. `FailedBeforeOpenHookLeavesTheOriginalUntouched`: искусственная ошибка сразу после preflight не меняет оригинал и не приводит к автоматической очистке.

Новый `scripts/tests/test_recovery_preopen_guard_contract.py` содержит **8 source-only Python проверок** порядка preflight/открытия/post-open, чтения без записи, освобождения дескриптора на ошибках, отсутствия непроверенных P/Invoke вызовов и статуса ручной приёмки. Он зарегистрирован в `scripts/office_acceptance.py` до `solution-restore` и добавлен в `scripts/tests/test_recovery_chain_consistency.py`.

Ручные гейты:
- `gd31j27bc-recovery-preopen-windows-ntfs-manual-e2e`;
- `gd31j27bc-recovery-preopen-astra-smb-manual-e2e`.

Оба остаются **`not-run`**; `releaseReady=false`.

## Обязательная следующая архитектурная работа

После разрешения тестового стенда проверить native-подходы в отдельных изолированных прототипах, **не внедряя их немедленно в действующий recovery-код**:

| Платформа | Кандидат | Обязательные испытания |
|---|---|---|
| Windows 10/11, NTFS | `CreateFileW`/native handle attributes, reparse-point handling, file ID через `GetFileInformationByHandleEx` | symlink/junction/rename, hardlink, sharing и ACL, открытие родительского каталога |
| Astra Linux / ALT / РЕД, ext4 | `openat` / `openat2` (`RESOLVE_NO_SYMLINKS` при поддержке ядра), `O_NOFOLLOW`, `fstat` | symlink, path traversal, fd-relative open, inode сравнение и rename races |
| SMB (Windows и Linux) | строго ограниченная интеграция native handle + remote identity | обрыв сети, reconnect, изменение file ID и нестабильные атрибуты |

Справку API необходимо сверить с целевыми ОС и версиями ядра, а лицензию/конфигурацию нативной библиотеки — до внедрения. После этого нужна проверка **не только файла**, но и безопасная выдача проверенной копии потребителю: если вернуть лишь `FilePath`, любая последующая команда восстановления снова может открыть другой объект.

**Статус:** PR Draft, выпуск запрещён. Статические исходники могут быть проверены в чате, но это не компиляция и не доказательство Windows/Astra/SMB поведения.
