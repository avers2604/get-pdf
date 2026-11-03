# GD-31J.27av — проверка целостности перед удалением recovery-копий и отмена до commit

**Статус:** отдельная Draft-ветка над GD-31J.27au / PR #160, без сборки и реального стенда. Изменения относятся только к `OfficeRecoveryStore`, синтетическим MSTest и локальным acceptance-контрактам. `main`, GitHub Actions, WORM-оригиналы, GUP-2 и опубликованные пользовательские документы не изменяются.

## Исходный риск

Метод `OfficeRecoveryStore.Discard(sourcePath)` допускается вызывать только после успешного подтверждённого сохранения оригинала. Он удаляет копии из индивидуального recovery-каталога. Ранее достаточно было, чтобы JSON-манифест содержал `SourcePath == source`, после чего удалялись и манифест, и файл `<GUID>.docx` / XLSX / PPTX **без проверки самого файла**.

Но манифест мог пережить внезапное исчезновение copy-файла, его порчу, ручную замену, подмену символьной ссылкой или чужой объект с тем же именем. Наличие корректной строки `SourcePath` в JSON не является подтверждением, что текущий файл — та самая копия, которую нужно удалить.

Дополнительная проблема: после завершения записи `<GUID>.json.tmp` в `CaptureAsync` не проверялся `cancellationToken` непосредственно перед атомарной публикацией в `<GUID>.json`. Если отмена была зафиксирована в этом промежутке, операция могла дойти до commit, хотя данные уже нельзя считать бесспорно ожидаемыми пользователем.

## Реализация

### 1. Консервативный Discard

В `Discard` замена логики:

```csharp
RecoveryManifest? metadata = ReadManifest(manifest);
if (metadata is null || !string.Equals(metadata.SourcePath, source, PathComparison)) continue;
TryDelete(manifest);
TryDelete(Path.Combine(folder, name + extension));
```

на:

```csharp
OfficeRecoveryCopy? verified = ReadValidCopy(source, extension, manifest);
if (verified is null) continue;
TryDelete(manifest);
TryDelete(verified.FilePath);
```

`ReadValidCopy` уже применялся в `FindLatest` и `Prune`. Он заново проверяет безопасное имя GUID-манифеста, исходный документ, тип OOXML, допустимые размеры, ZIP/XML-структуру, отсутствие символической ссылки на файл копии и SHA-256 файла против манифеста. Если любая проверка не проходит, `Discard` **ничего не удаляет для данной пары**. При этом остальные валидные копии могут быть удалены после подтверждённого сохранения.

Повреждённые или неполные recovery-пары не нужно объявлять «мусором» и автоматически очищать: пусть они остаются для технической диагностики.

**Компромисс:** полная проверка ZIP/XML плюс SHA-256 может оказаться заметной на слабых ПК и при большом числе копий. Эту стоимость необходимо измерить на локальном Windows/Astra стенде до выпуска; ограничители размеров в `EnsureValidPackage` остаются. Осмотр не является гарантией безопасности от враждебных TOCTOU-подмен между проверкой и `File.Delete`; для такой гарантии нужны handle-bound механизмы или доверенные ACL.

### 2. Последняя отмена перед commit

В `CaptureAsync` после проверки ссылок на `finalCopy` и `temporaryManifest` добавлен `cancellationToken.ThrowIfCancellationRequested()` **непосредственно перед** `File.Move(temporaryManifest, finalManifest)`.

- Отмена до этой точки → ошибка/отказ, штатный `catch` очищает только принадлежащие операции артефакты согласно GD-31J.27au.
- Успешный перенос манифеста → **commit**, снимок опубликован; возможная отмена после commit не разрешает удалить его задним числом.
- Отмену системного вызова `File.Move` внутри самого вызова этот checkpoint не обеспечивает: операция остаётся атомарной со стороны обычной файловой API, но отмена только кооперативная.

## Написанные регрессионные тесты

`tests/GetDocument.Office.Tests/OfficeRecoveryStoreTests.cs` — 4 новых MSTest:

1. `DiscardRetainsTamperedSnapshotAndManifestForManualInvestigation`: хэш изменён, исходный файл не тронут, подозрительные snapshot + manifest сохранены.
2. `DiscardDoesNotUnlinkSymlinkReplacedRecoveryCopy`: файл копии заменён ссылкой на сторонний OOXML, и ссылка с её целью и манифестом должны остаться нетронутыми.
3. `DiscardPreservesOrphanManifestWhenItsCopyDisappeared`: существующий JSON-манифест без copy-файла не удаляется.
4. `CancelledExporterLeavesNoPublishedCopyOrTemporaryFile`: экспорт синтетического OOXML завершает запись, отменяет токен и метод отказывается публиковать снимок; исходник не изменён.

`tests/GetDocument.Office.Tests/OfficeRecoveryStoreLinkSafetyTests.cs` — 1 дополнительный MSTest: `DiscardPreservesSymlinkedManifestAndValidSiblingCopy`, защищает от очистки пары, если ссылка подменила манифест.

Дополнены 3 offline source-only Python метода в `scripts/tests/test_recovery_chain_consistency.py`: обязательная проверка целостности в `Discard`, наличие сценариев MSTest, расположение точки отмены до публикации манифеста. Старое точное количество методов `OfficeRecoveryStoreLinkSafetyTests` обновлено с 9 до 10. Регистрация source-only контракта **уже существовала** в `scripts/office_acceptance.py` и сохранена.

Добавлены ручные гейты:

- `gd31j27av-recovery-discard-integrity-windows-ntfs-manual-e2e`;
- `gd31j27av-recovery-discard-integrity-astra-smb-manual-e2e`.

Оба остаются `not-run`; `releaseReady=false`. Доработан `scripts/tests/test_office_acceptance.py` для защиты этого статуса.

## Границы доказательств

**В рамках этого этапа не выполнялись:** компиляция .NET, запуск MSTest и Python unittest, настоящий crash/fault-injection, тесты Windows/Astra Linux/SMB, тестирование аппаратных принтеров и GUP-2. Тестовые сценарии только записаны в репозиторий, проверка делается по исходникам; статус готовности к выпуску не меняется.

После снятия запрета на сборку можно запустить:

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_chain_consistency.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeRecoveryStore"
```

Ручная приёмка: валидные копии по-прежнему удаляются только после подтверждённого сохранения; повреждённые, исчезнувшие, подменённые символическими ссылками и не принадлежащие исходнику пары сохраняются; сторонний документ не меняется. На слабых ПК измерить задержку проверки нескольких архивов близко к 128 МиБ и скорректировать дизайн без понижения гарантий сохранности.

## Дальше

GD-31J.27aw: анализировать взаимодействие `Prune`, `Discard` и прав доступа при частичных ошибках удаления, не разрешая автоматическую очистку недоверенных объектов и не вводя её в UI.
