# GD-31J.27ca — ограниченный контейнер журнала и native read-only restart review

Дата: 2026-11-12. Статус: **Draft; diagnostic-only; releaseReady=false**.
Основа: GD-31J.27bz / Draft PR #191. Этот этап НЕ разрешает
автоматическое восстановление, очистку, публикацию или перезапись WORM.

## Что добавлено по блокам

### CA-1 — канонический транспортный контейнер (только память)

- `RecoveryCrashJournalWireEnvelope.EncodeSynthetic` и `DecodeCandidate`.
- Байтовая структура: magic `GD31JCA1` (8 байт), версия uint16 little-endian
  (2 байта), reserved flags uint16=0 (2 байта), длина payload uint32
  little-endian (4 байта), SHA-256 payload (32 байта), затем canonical
  JSON-журнал из предшествующего GD-31J.27by.
- Фиксированный заголовок 48 байт, payload от 1 до 8192 байт,
  контейнер не больше 8240 байт, trailing bytes запрещены.
- Проверяется точная длина, зарезервированные флаги, каноничность JSON,
  SHA-256. Сравнение SHA выполняется `FixedTimeEquals`.
- Проверка SHA-256 **обнаруживает повреждение**, но **не аутентифицирует**
  источник. Злоумышленник может заменить payload и SHA; синтетический
  HMAC GD-31J.27bz со сторонним ключом тоже не даёт rollback resistance.

### CA-2 — повторное чтение идентификаторов по ранее открытым дескрипторам

- `RecoveryRestartHeldHandleReadback.Observe` принимает шесть
  `SafeFileHandle` (parent/copy/manifest при начальном наблюдении
  и повторной проверке), принадлежащих вызывающему коду.
- Проверяет отмену, null/closed/invalid, затем использует существующий
  `RecoveryNativeHandleIdProbe.ReadFromExistingHandle`:
  Windows `FILE_ID_INFO`; Linux `statx(AT_EMPTY_PATH)`.
- Сравнивает `RecoveryRestartFileIdSnapshot.HasStableDistinctFileIds`.
  Не закрывает чужие дескрипторы и не открывает пути или файловое
  содержимое. Поддержка иных платформ завершается исключением.
- **Ограничение**: File ID является свойством объекта, а не доказательством,
  что имя в каталоге по-прежнему указывает на этот объект. Для Linux
  mount ID наблюдается внутри statx, но пока не сохраняется в
  `RecoveryNativeFileId`, поэтому bind mount/reparent-аттестация не
  завершена. На Windows текущий File ID probe не доказывает тип каталога
  и отсутствие reparse point. Проверенные на фиктивных файлах тестовые
  идентификаторы нельзя считать подтверждением настоящей директории.

### CA-3 — интеграция с аналитическим перезапуском

- `RecoveryRestartCheckpointReview.InspectEnvelope` сначала декодирует
  контейнер, затем вызывает существующую `Inspect` (anchor, канонический
  журнал, событие/цепочка SHA, наблюдения артефактов, IDs).
- Некорректный контейнер => `InvalidJournal`, а не «пустой журнал».
  Ошибочный ключ HMAC => `AnchorRejected`.
- Отмена прерывает проверку. Нет разрешений `CanRestore`,
  `CanDelete`, `CanResumePublication`, `ReleaseReady`.
- Метод по-прежнему допускает **caller-supplied** синтетические
  утверждения, потому не является «нативной аттестацией». Вызов
  `RecoveryRestartHeldHandleReadback.Observe` остаётся отдельным;
  промышленная интеграция `OfficeRecoveryStore` не меняется.

### CA-4 — тестовые контракты и контроль внедрения

- Созданы 21 MSTest-метод:
  - 11 для контейнера журнала;
  - 6 для сравнения открытых дескрипторов;
  - 4 для связки контейнер → повторный анализ.
- Созданы 13 offline Python `unittest` source-only контрактов.
- Новый шаг `recovery-journal-wire-readback-source-tests` добавлен
  в `scripts/office_acceptance.py` до `solution-restore`.
- Восемь новых **ручных, не пройденных** gate-проверок:
  NTFS/power cut, Astra ext4/torn write, native handle reopen Windows,
  Linux rename race, SMB cross-host lock, bind mount/reparent,
  trusted rollback-resistant anchor, сохранность WORM.
- Итого в реестре: **179 ручных gate**. Это реестр, а не утверждение о
  выполненном тестировании. GitHub Actions не задействованы.

## Чего ещё нет — P0 release blockers

1. Зафиксированного `openat2`/NT native **name-bound** чтения именно
   по доверенному дескриптору каталога и проверки повторной привязки
   имён, не допускающей TOCTOU/reparent.
2. Полноценного native durable backend: preallocation, append и
   однозначного commitment record, `FlushFileBuffers`/`fsync`,
   `FlushFileBuffers` каталога по подтверждённой Windows-семантике,
   `fsync(dirfd)` на Astra Linux, запрета частичной фиксации.
3. Независимого защищённого хранения HMAC-ключа/монотонного поколения;
   MAC, если ключ поступил от вызывающего кода, не предотвращает rollback.
4. Cross-process/cross-host lock, crash/power-loss tests на NTFS,
   Astra/ext4 и SMB с подтверждёнными настройками тома и сервера.
5. Подключения к `OfficeRecoveryStore` и устранения известного P0:
   path-based `File.Move` при capture под активным противником.

Автоматическое удаление повреждённых/осиротевших временных файлов
**не допускается**: карантин и ручная разборка остаются обязательными.
Исходные WORM-документы нельзя изменять при восстановлении.

## Локальный способ проверки (при наличии .NET SDK 10)

```bash
python -m unittest discover -s scripts/tests -p test_recovery_journal_wire_readback_contract.py
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj --no-restore --filter "FullyQualifiedName~RecoveryCrashJournalWireEnvelopeTests|FullyQualifiedName~RecoveryRestartHeldHandleReadbackTests|FullyQualifiedName~RecoveryRestartWireEnvelopeIntegrationTests"
```

Эти команды документированы, **не объявляются выполненными**.
Для native интеграционных тестов необходим отдельный разрешённый стенд.
