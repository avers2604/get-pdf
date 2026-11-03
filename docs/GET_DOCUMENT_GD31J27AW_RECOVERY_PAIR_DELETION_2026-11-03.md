# GD-31J.27aw — сохранность recovery-пар при частичном отказе удаления

**Статус:** stacked Draft поверх GD-31J.27av / PR #161. Реальные C# изменения, написанные регрессии и source-only проверки, без сборки, без запуска тестов и без стенда. Исходники в `avers2604/get-pdf`. Не меняются `main`, GitHub Actions, GUP-2, EuroOffice-native SaveAs, WORM и исходные документы.

## Выявленная проблема

В `OfficeRecoveryStore.Discard` (после подтверждённого сохранения) и `OfficeRecoveryStore.Prune` (лимит трёх снимков) до этого этапа порядок удаления был:

```csharp
TryDelete(manifest);
TryDelete(copy);
```

Метод `TryDelete` подавляет обычные `IOException` / `UnauthorizedAccessException` и не сообщает об отказе. Если удалить JSON-манифест удалось, а физическая OOXML-копия осталась из-за ACL, SMB-локировки, ошибки носителя или антивируса, снимок превращался в **осиротевший файл без манифеста**. Он больше не обнаруживается штатным `FindLatest`. Это особенно опасно, если отказывающая файловая система восстановится позднее.

## Исправление: сначала проверенная копия, затем её манифест

Оба пути используют одну внутреннюю функцию:

```csharp
internal static bool DeleteVerifiedPair(
    string copyPath, string manifestPath, Func<string, bool> tryDelete)
{
    ArgumentNullException.ThrowIfNull(tryDelete);
    if (!tryDelete(copyPath)) return false;
    return tryDelete(manifestPath);
}
```

`Discard` и `Prune` перед вызовом уже проверяют пару через `ReadValidCopy` (ограничения OOXML ZIP/XML, отсутствие символьной ссылки, SHA-256, сверка источника). `Prune` применяет это только к снимкам сверх лимита. Порядок теперь общий: **копия → манифест**.

Для реальных файлов `TryDeleteVerifiedPart` повторно запрашивает метаданные и отказывается удалять отсутствующий объект, ссылку, reparse point, каталог или устройство. При `IOException`, `UnauthorizedAccessException` или `SecurityException` возвращает `false`. `true` допускается только после успешного вызова удаления и отсутствия файла по исходному пути. При `false` на первом шаге манифест **вообще не трогается**.

| Отказ | Ожидаемое поведение |
|---|---|
| Копия не удалена: ACL/lock/IO error | Оставить копию **и манифест** — валидный recovery можно попытаться прочитать снова |
| Копия удалена, манифест удалить не удалось | Остаётся **манифест-сирота**; `FindLatest` не будет считать его восстановимым |
| Оба удаления успешны | Recovery-пара удалена |
| Ссылка / каталог / неподтверждённый файл | Не удалять автоматически |
| Лимит трёх превышен из-за отказа | Временно допускается больше трёх снимков; **не** удалять манифест принудительно |
| Отказ после успешной публикации нового снимка | Не откатывать новый commit, если prune не смог удалить старые данные |

Предыдущий `TryDelete` **сохранён отдельно** для best-effort cleanup временных объектов после ошибок `CaptureAsync`: его поведение нельзя механически заменить новым helper'ом без учёта уже существующей политики по symlink временной копии.

**Ограничение:** это `FileInfo`/path-based повторная проверка и обычный `File.Delete`, а не атомарная OS-handle-bound операция. Подмена между проверкой и удалением, а также ситуации, когда файл исчез непосредственно перед `File.Delete`, остаются потенциальными TOCTOU-гонками. Строго доказать идентичность удаляемого объекта без дескрипторов файлов и изолированных ACL нельзя. При сбое удаления манифеста после успешного удаления копии сирота JSON может остаться для ручного разбора. Автоматической уборки неизвестных объектов здесь нет.

## Написанные детерминированные тесты без реальных отказов NTFS/SMB

Создан `tests/GetDocument.Office.Tests/OfficeRecoveryDeletionOrderTests.cs` — **4 MSTest** на внутренний алгоритм и инъекцию результата удаления:

1. `CopyDeletionDeniedKeepsManifestAndNeverCallsManifestDelete`: имитация `false` для удаления копии; манифест не затрагивается.
2. `ManifestDeletionDeniedOnlyAfterSuccessfulCopyDeletion`: удаление копии `true`, манифест `false`; проверяется порядок.
3. `BothDeletionsSucceedInCopyFirstOrder`: оба `true`, строгий порядок copy → JSON.
4. `FaultingCopyDeletionDoesNotAttemptManifestDeletion`: искусственный `IOException` прерывает helper без вызова удаления манифеста (production callback сам переводит штатные IO-ошибки в `false`).

`GetDocument.Office.csproj` уже содержит `InternalsVisibleTo Include="GetDocument.Office.Tests"`, так что новой публичной операции удаления не добавлено.

В `scripts/tests/test_recovery_chain_consistency.py` добавлены **3 source-only Python теста**: оба вызывающих метода используют общий порядок, helper перепроверяет тип файлов и безопасно трактует ошибки, все 4 C#-регрессии присутствуют. Старый source-only контракт из GD-31J.27av обновлён на `DeleteVerifiedPair`, чтобы не требовал удалённый `TryDelete(manifest)`.

Ручные OS-пункты добавлены в `scripts/office_acceptance.py` и `scripts/tests/test_office_acceptance.py`:

- `gd31j27aw-recovery-partial-delete-windows-ntfs-manual-e2e`
- `gd31j27aw-recovery-partial-delete-astra-smb-manual-e2e`

Оба `not-run`, `releaseReady=false`. Сборка, реальный MSTest, Python unittest, Windows/Astra/SMB, замеры производительности и запуск GitHub Actions **не выполнялись**. Статические сверки исходников не считаются доказательством поведения исполняемого приложения.

## Приёмка после снятия ограничения на сборку

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_chain_consistency.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeRecovery"
```

Ручная проверка: синтетический валидный DOCX/XLSX/PPTX + отдельный recovery-каталог; отказ удалить **только копию**, отказ удалить **только манифест**, задержанные SMB-операции, read-only/ACL, изменение файла между validation и delete, сохранение оригинала/WORM неизменными. Отсутствие файлов после неудачного удаления — не повод утверждать, что причина отказа установлена.

## Следующий этап

GD-31J.27ax — анализ жизненного цикла `TryDelete` при исключениях и устойчивости `FindLatest` к частично опубликованным/повреждённым JSON. До реальной сборки новые функции очистки не вводить.
