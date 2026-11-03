# GD-31J.27ao — read-only инвентаризация незавершённых резервных копий

**Статус:** код в отдельной Draft-ветке поверх GD-31J.27an (PR #153), без сборки и стенда. Изменение только для существующего локального `OfficeRecoveryStore`, не часть native EuroOffice SaveAs, GUP-2, WORM или сервера.

## Зачем

Аварийное завершение процесса между созданием `.part.docx/.part.xlsx/.part.pptx` и окончательной публикацией `.json` может оставить временные имена. Автоматическое удаление таких файлов рискованно: они могут относиться к активной операции или содержать последние несохранённые изменения. Поэтому первым этапом создаём **только наблюдение**, без автоматического cleanup и без решений об «устаревании».

## Публичное API

`OfficeRecoveryStore.InspectIncomplete(string sourcePath, int maxEntries = 256, CancellationToken cancellationToken = default)`

Возвращает `OfficeRecoveryIncompleteInventory(Candidates, Truncated)`.

Запись `OfficeRecoveryIncompleteCandidate` содержит **только имя файла**, тип (`StagedOfficeCopy` или `TemporaryManifest`), статус и **необязательные** размер/дату изменения. Ни абсолютного пути, ни содержимого, ни содержимого JSON, ни SHA-256, ни информации о пользователях извне папки не возвращает.

Статусы:

- `RegularUnverified` — обнаружен обычный файл; не означает, что он закрыт, завершён, безопасен или восстановим;
- `UntrustedLinkOrNonFile` — ссылка, junction, directory, device/reparse point; метаданные цели не запрашиваются;
- `MetadataUnavailable` — элемент изменился или недоступен во время осмотра; **не** трактуется как отсутствие проблемы.

## Ограничения по безопасности

1. Вычисляется **тот же каталог по SHA-256 пути одного исходного файла**, что и для `CaptureAsync`. Сканирования всех пользователей, всех папок recovery или документов не происходит. Исходный документ не открывается; путь может быть известен даже после его удаления.
2. Все предки recovery-каталога проходят существующую `RejectLinkedDirectories`. Symlink/junction вместо корня — исключение, не пустой результат. Сам просмотр допускается лишь при доверенном настроенном корне.
3. `Directory.EnumerateFileSystemEntries(..., SearchOption.TopDirectoryOnly)`, без рекурсии. Проверка **всех** имён ограничена `maxEntries` от 1 до 1024, по умолчанию 256. Если лимит превышен, `Truncated=true`, и результат нельзя считать исчерпывающим. Перед началом и на каждой итерации проверяется `CancellationToken`; отмена не возвращает «пусто».
4. Учитываются только имена `<GUID:N>.part<extension>` и `<GUID:N>.json.tmp`; extension берётся из DOCX/XLSX/PPTX исходного файла. Никаких префиксных совпадений с пользовательскими файлами, чужими форматами, уже опубликованными `.json`/OOXML.
5. Проверяются только метаданные через `FileInfo.LinkTarget/Attributes/Length/LastWriteTimeUtc`. При обнаружении ссылки **размер и время не читаются**; при исключениях чтения метаданных возвращается `MetadataUnavailable`. Контент/ZIP/JSON не открывается.
6. Этот метод **никогда** не вызывает `File.Delete`, `Directory.Delete`, `TryDelete`, `Discard`, `Prune`, `File.Move`, `CaptureAsync` и не создаёт папку. Нельзя автоматически привязывать результаты к UI-кнопке «Очистить».
7. Проверки путей, метаданных и ссылок не дают защиты от злонамеренной TOCTOU-гонки и не являются подтверждением ACL/SMB/NTFS/ext4. Даже статус `RegularUnverified` — лишь наблюдение на момент осмотра.

## Добавлено в тесты

`tests/GetDocument.Office.Tests/OfficeRecoveryIncompleteInventoryTests.cs`: **10 MSTest** с синтетическими локальными файлами без настоящих документов. Проверяют пустой каталог без его создания, точные имена, сохранность стороннего файла, symlink на сторонний файл, каталог с именем кандидата, ограничение количества элементов, изоляцию двух источников, отказ на ссылочный корень, dangling symlink, отмену и неверные параметры. Исходные `OfficeRecoveryStoreTests` и `OfficeRecoveryStoreLinkSafetyTests` сохраняются.

`scripts/tests/test_recovery_readonly_inventory_contract.py`: source-only тест, запрещающий появление разрушительных операций внутри нового метода. Интегрирован в `scripts/office_acceptance.py` **до** offline NuGet restore на Windows и Linux.

Команды для разрешённой локальной разработки (в текущем чате **не выполнялись**):

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_readonly_inventory_contract.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~OfficeRecoveryIncompleteInventoryTests|FullyQualifiedName~OfficeRecoveryStoreLinkSafetyTests|FullyQualifiedName~OfficeRecoveryStoreTests'
```

Для `dotnet test` необходимы одобренный .NET 10 SDK и предварительный restore только из локального NuGet feed. В Windows некоторые тесты symlink требуют соответствующих прав разработчика, но **не должны тихо пропускаться**.

## Фактический уровень проверки

Файлы прочитаны обратно из GitHub; 25 статических структурных утверждений выполнены. **Ни C# сборка, ни MSTest, ни сами Python unit tests не запускались в данной сессии.** Нет Windows/Astra/SMB стенда и запуска реального EuroOffice. В приёмке отдельно зарегистрированы `gd31j27ao-recovery-inventory-windows-ntfs-manual-e2e` и `gd31j27ao-recovery-inventory-linux-astra-manual-e2e`; обе проверки **not-run**, `releaseReady=false`. Изменения не объединены в main, GitHub Actions не включались.

## Следующий GD-31J.27ap

Без стенда: подготовить пользовательский **read-only экран диагностики восстановления** в дизайне ГЭТ 4.1 с явными статусами `RegularUnverified` / `UntrustedLinkOrNonFile` / `MetadataUnavailable` / `Truncated`. Не добавлять удаление, автоматическое восстановление или публикацию до реальной приёмки.
