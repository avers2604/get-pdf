# GD-31J.27au — отказ от удаления чужих файлов при конфликте имён recovery

**Статус:** код в отдельной stacked Draft-ветке поверх GD-31J.27at / PR #159. Основной движок EuroOffice, GUP-2, WORM, native SaveAs и пользовательские документы не затрагиваются. Компиляция, MSTest, Python unittest и реальные Windows/Astra/SMB испытания **не выполнялись**.

## Обнаруженный дефект

До изменения `OfficeRecoveryStore.CaptureAsync()` удалял файлы в `catch` без доказательства, что данная операция их создала:

```csharp
TryDelete(temporaryCopy);
TryDelete(temporaryManifest);
if (!File.Exists(finalManifest)) TryDelete(finalCopy);
```

Имя операции — случайный GUID в каталоге восстановления. Экспортёр получает путь `<guid>.part.docx` / XLSX / PPTX и может узнать все соседние имена. Если другой компонент или ошибочный экспортёр создаст `<guid>.docx` **до** публикации, `File.Move(temporaryCopy, finalCopy)` завершится исключением `IOException` (не перезаписывает существующий файл). После этого старый `catch` мог **удалить уже существовавший чужой `<guid>.docx`**.

Второй случай: `<guid>.json.tmp` уже существует. После GD-31J.27an `FileStream(FileMode.CreateNew)` корректно отказывается его перезаписать. Но старый `catch` всё равно вызывает `TryDelete(temporaryManifest)` и **удаляет чужой занятый файл**. В тесте с symlink удаляется сама ссылка, а не её цель; это всё равно нарушение принципа не удалять не принадлежащие операции записи.

Третий случай: предсуществующий `<guid>.json` блокирует `File.Move(temporaryManifest, finalManifest)`. Прежняя проверка `File.Exists(finalManifest)` ошибочно трактует факт существования манифеста как доказательство принадлежности снимка и оставляет собственную новую `<guid>.docx` без правильного манифеста. Она также не доказывает, что существующий манифест был создан именно данным capture.

## Исправление — явное владение артефактами

Добавлены локальные флаги:

- `publishedCopy`: `true` **только после успешного** `File.Move(temporaryCopy, finalCopy)`.
- `createdTemporaryManifest`: `true` **только после успешного** `new FileStream(temporaryManifest, FileMode.CreateNew, ...)`.
- `publishedManifest`: `true` **только после успешного** `File.Move(temporaryManifest, finalManifest)`; с этого момента `createdTemporaryManifest=false`, потому что исходное временное имя перестало принадлежать операции.

При исключении:

```csharp
TryDelete(temporaryCopy);
if (createdTemporaryManifest) TryDelete(temporaryManifest);
if (publishedCopy && !publishedManifest) TryDelete(finalCopy);
throw;
```

Таким образом, сторонние файлы, заранее занявшие **финальное имя OOXML**, **временное имя манифеста** или **финальное имя манифеста**, не удаляются из-за одного только совпадения имени. Если опубликованный снимок уже имеет собственный успешно опубликованный манифест, возможный отказ `Prune()` не откатывает валидную пару.

Важно: `temporaryCopy` создаётся делегатом `exportCopy`, а не непосредственно `OfficeRecoveryStore`. Его очистка по назначенному операции пути оставлена для совместимости с существующими проверками (в частности, чтобы не сохранять symlink на оригинал после отказа). **Это не полная защита от злонамеренного конкурентного процесса:** path-based TOCTOU и подмена файла по тому же имени требуют OS-handle-bound механизма; не заявлять абсолютную гарантию владения для временной OOXML-копии. Работа с symlink, hardlink и ACL проверяется отдельно на ОС.

## Написанные регрессионные MSTest

Расширен `tests/GetDocument.Office.Tests/OfficeRecoveryStoreLinkSafetyTests.cs` — **3 новых теста**, всего 9:

1. `PreexistingFinalCopyCollisionMustNotDeleteUnrelatedFile`: экспортёр создаёт конкурирующий `<guid>.docx`; `File.Move` не должен перезаписывать или удалять его. Проверяется неизменность байт.
2. `PreexistingRegularTemporaryManifestCollisionMustNotDeleteIt`: после эксклюзивного `CreateNew` отказа чужой `<guid>.json.tmp` остаётся нетронутым.
3. `PreexistingFinalManifestCollisionDoesNotPublishOrDeleteIt`: чужой `<guid>.json` остаётся нетронутым; собственная незафиксированная новая `<guid>.docx` не должна остаться после ошибки.

Все тесты используют синтетический валидный минимальный ZIP/OOXML из существующей fixture. Нет доступа к пользовательским документам или сетевым оригиналам.

Существующий `scripts/tests/test_recovery_chain_consistency.py` дополнен двумя source-only проверками явного владения и наличия трёх MSTest. Никакой сборки или выполнение тестов в этом этапе не проводились; `scripts/office_acceptance.py` сохраняет сам контракт на раннем этапе и регистрирует обязательные ручные E2E:

- `gd31j27au-recovery-name-collision-windows-ntfs-manual-e2e`;
- `gd31j27au-recovery-name-collision-linux-astra-manual-e2e`.

Состояние: `not-run`, `releaseReady=false`. Поведение на настоящем NTFS/ext4/SMB не аттестовано.

## Последующая проверка после снятия запрета на сборку

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_chain_consistency.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~OfficeRecoveryStoreLinkSafetyTests
```

Только на изолированном стенде: физические collision tests под Windows 10/11 NTFS, Astra Linux ext4 и SMB; ошибки и аварийные завершения в каждом промежуточном шаге; доказательство, что сторонний файл не меняется и что исходный документ/WORM не затронуты.

## Решение для следующей итерации

До сборки и OS-стенда приоритет — дальнейшие **точечные исправления подтверждённых ошибок**, а не расширение функций восстановления или автоматическая уборка файлов. Принцип прежний: `ГЭТ PDF/Документ` никогда не должен удалять пользовательские исходники и не должен считать неизвестный временный файл принадлежащим данной операции без подтверждения.
