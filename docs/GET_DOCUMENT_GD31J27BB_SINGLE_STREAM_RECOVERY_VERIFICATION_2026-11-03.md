# GD-31J.27bb — единый дескриптор для SHA-256 и ZIP/XML аварийной копии

**Репозиторий:** `avers2604/get-pdf`. **Ветка:** `feature/gd31j27bb-recovery-single-read-handle`, stacked Draft поверх GD-31J.27ba / PR #166.

**Статус:** изменения в исходниках и записанные тесты; компиляция .NET, запуск MSTest/Python unittest, Windows NTFS/Astra/SMB-стенд и бенчмарки **не проводились**. `main`, GitHub Actions, WORM-оригиналы, GUP-2, EuroOffice-native SaveAs и пользовательские документы не изменяются.

## Исходный риск

До этого этапа `OfficeRecoveryStore.ReadValidCopy()` отдельно вызывал:
1. `HashFile(copy)`, который открывал и закрывал `File.OpenRead(copy)`;
2. `EnsureValidPackage(copy, extension)`, который повторно открывал этот путь через `ZipFile.OpenRead(copy)`.

Если путь заменён между двумя вызовами, SHA мог относиться к **одному объекту**, а ZIP — к **другому**. Проверка размера и LastWriteTimeUtc по имени файла из GD-31J.27ba улучшала обнаружение обычных изменений, но не связывала фактические чтения одним дескриптором.

В `CaptureAsync` использовались те же два независимых открытия (ZIP → `HashFileAsync`), поэтому риск применим и к временной копии до публикации манифеста.

## Реализованное изменение

- `OpenRecoveryReadStream(path, options)` проверяет `FileInfo.Exists`, размер не более **128 МиБ** и отсутствие link/reparse-point до открытия. Затем один раз вызывает `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ...)`, повторно проверяет длину и seek/read. Не создаёт и не изменяет файлы.
- В обычном `ReadValidCopy` один `using var input = OpenRecoveryReadStream(copy);` передаётся в `VerifyOpenedPackage(input, extension, manifest.Sha256, ...)`. Этот метод считает SHA-256, при несовпадении быстро отказывает; при совпадении переводит **тот же поток** на начало и вызывает обязательную `EnsureValidPackage(input, extension)`. ZIP и XML распаковываются из уже открытого потока, без второго открытия файлового пути.
- `EnsureValidPackage` теперь принимает `Stream`, использует `new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true)` и прежние ограничения числа ZIP-частей, размеров, коэффициента сжатия, путей, запрет макросов/ActiveX, XML DTD и внешних сущностей. `leaveOpen: true` гарантирует, что обработчик ZIP не забирает владение `FileStream` у вызывающего метода.
- В `CaptureAsync` `VerifyAndHashExportAsync` открывает временную OOXML **один раз**, выполняет ZIP/XML-проверку и затем асинхронный `SHA256.HashDataAsync(input, cancellationToken)` из того же дескриптора. Сохраняются кооперативная отмена и сравнение размера/mtime до и после.
- В `ReadValidCopy` по-прежнему проверяются размер, mtime и отсутствие ссылки **до и после** полной проверки. `FindLatest` не кэширует доверие, а `Prune`/`Discard` не получают новых полномочий удаления.

**Непреодолённые ограничения:** `FileShare.Read` на Windows может блокировать обычную конкурентную запись, но не даёт одинаковых гарантий на POSIX и всех вариантах SMB. Проверка `FileInfo` до `Open` не эквивалентна атомарному `openat(O_NOFOLLOW)`/Windows handle-based reparse policy; атакующий может заменить путь **до открытия**. Изменения байтов уже открытого файла со старой длиной и восстановленным mtime также нельзя абсолютно исключить одним `FileStream`. Возвращаемый `OfficeRecoveryCopy` по-прежнему содержит путь, а не удерживаемый дескриптор; возможен TOCTOU после проверки и до фактического открытия пользователем. Следовательно, реализация решает узкую проблему **двух отдельных чтений**, но не является доказательством защиты от злонамеренного конкурентного процесса.

## Регрессионные сценарии, записанные без выполнения

Новый `tests/GetDocument.Office.Tests/OfficeRecoverySingleStreamTests.cs` содержит **5 MSTest** на синтетическом ZIP в памяти:

1. `SameSeekableStreamVerifiesHashAndZipAndRemainsOpen`: правильный SHA + ZIP с сохранением открытого потока.
2. `WrongHashDoesNotOpenOrTrustInvalidZip`: неверный SHA прекращает работу до ZIP; настоящий хэш повреждённых байтов не заменяет ZIP-проверку.
3. `CorrectHashWithoutRequiredOfficePartIsStillInvalid`: даже совпадающий SHA не делает документ пригодным, если отсутствует `word/document.xml`.
4. `NonSeekableStreamIsRejectedBeforeHashAndZip`: нет случайного чтения не-перематываемого потока.
5. `CorruptedZipAfterHashFailsRatherThanAcceptingOldDigest`: синтетическая обрезка потока между проверками не должна считаться валидной копией.

В существующем `OfficeRecoveryFastSelectionTests.cs` обновлён тест конкурентной записи после хэширования: для Windows допустимо, что `FileShare.Read` запрещает изменение; если изменение разрешено ОС, копия должна быть отклонена. Новый файл `scripts/tests/test_recovery_single_stream_contract.py` содержит **8 Python source-only методов**; старые `test_recovery_fast_selection_contract.py` и `test_recovery_concurrent_selection_contract.py` согласованы с изменившимися границами SHA/ZIP-проверки.

В `scripts/office_acceptance.py` зарегистрирован ранний (до NuGet restore) шаг `recovery-single-stream-source-tests`, а также две обязательные ручные проверки:

- `gd31j27bb-single-stream-windows-ntfs-manual-e2e`;
- `gd31j27bb-single-stream-astra-smb-manual-e2e`.

В `scripts/tests/test_office_acceptance.py` их состояние остаётся `not-run`; `releaseReady=false`. `scripts/tests/test_recovery_chain_consistency.py` следит за уникальностью регистрации/ручных ворот. Компиляция и запуск тестов в рамках этапа **не разрешены**.

## Последующая обязательная проверка на стенде

После снятия запрета:

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_single_stream_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_recovery_fast_selection_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_recovery_concurrent_selection_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeRecovery"
```

Изолированная ручная матрица: Windows 10/11 NTFS, Astra Linux ext4 и SMB; заблокированная конкурентная запись при `FileShare.Read`, переименование пути перед открытием и во время ZIP, параллельные изменения inode, отказ SMB после получения дескриптора, отмена `CaptureAsync`, корректный результат ZIP/SHA и сохранность исходника. При авариях и недоступности диска состояние должно быть ошибкой, а не «копий нет».

**Дальше:** GD-31J.27bc — анализ безопасного открытия пути с no-follow / handle identity на Windows/Astra и способа передавать подтверждённую копию без повторного доверия к имени файла. Не вводить платформенно-специфические API в production до подтверждения лицензий, режимов сети и сборки.
