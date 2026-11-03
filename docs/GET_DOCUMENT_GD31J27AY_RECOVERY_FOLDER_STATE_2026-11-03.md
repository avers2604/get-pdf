# GD-31J.27ay — «копий нет» ≠ «каталог недоступен»

**Репозиторий:** `avers2604/get-pdf`; **ветка:** `feature/gd31j27ay-recovery-folder-state`, stacked Draft над GD-31J.27ax / PR #163. **Режим:** без сборки, запуска тестов и стенда. `main`, GitHub Actions, EuroOffice native SaveAs, GUP-2, WORM-оригиналы и пользовательские документы не изменялись.

## Обнаруженная ошибка

В `OfficeRecoveryStore` три пути (`InspectIncomplete`, `FindLatest`, `Discard`) использовали:

```csharp
if (!Directory.Exists(folder)) return /* empty or null */;
```

`.NET Directory.Exists` возвращает `false` и в некоторых ситуациях, когда OS/SMB не может определить существование каталога из-за отказов доступа или других ошибок. В итоге потенциально недоступный recovery-каталог мог быть представлен как **пустой**, а `FindLatest` — вернуть `null` вместо ошибки. Для аварийного восстановления это недопустимое смешение «нет артефактов» и «не получилось проверить».

## Изменения в рабочем коде

### Явная проверка каталога

Добавлена внутренняя функция:

```csharp
internal static bool RecoveryFolderExists(
    string folder, Func<string, FileAttributes>? getAttributes = null)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(folder);
    try
    {
        FileAttributes attributes = getAttributes is null
            ? File.GetAttributes(folder)
            : getAttributes(folder);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "Путь аварийного восстановления не является обычным каталогом.");
        return true;
    }
    catch (FileNotFoundException) { return false; }
    catch (DirectoryNotFoundException) { return false; }
}
```

Она вызывается **после** уже существовавшего `RejectLinkedDirectories(folder)`. Состояние `false` допускается только для OS-исключений «файл/каталог не найден». `UnauthorizedAccessException`, `SecurityException`, прочие `IOException`, не-каталоги и reparse points — **не превращаются в `false`**. Для них остаются исключения; вызывающий слой должен отображать статус неизвестности/ошибки, а не утверждать отсутствие копий.

Три пути используют одинаковое правило:
- `InspectIncomplete` возвращает пустой список только при подтверждённо отсутствующей папке или при успешном пустом перечислении.
- `FindLatest` возвращает `null` только при отсутствующей папке или после успешно завершённого перебора без подходящих валидных снимков.
- `Discard` при ошибке доступа прекращает обработку через исключение, не принимая недоступную папку за пустую и не запуская неизвестную очистку.

**Fail closed:** `Directory.EnumerateFiles` и `Directory.EnumerateFileSystemEntries` используют ленивые итераторы. Если папка исчезла **после начального `GetAttributes`** или `MoveNext()` выбросил `DirectoryNotFoundException` / `IOException` / отказ по ACL, ошибка **передаётся**, а `FindLatest`/инспектор не возвращают частичный результат как окончательный. Ни `DirectoryNotFoundException` во время перечисления, ни `UnauthorizedAccessException` во время перечисления не переводятся в «каталог пуст».

Для детерминированных тестов, **без изменения внешнего публичного API**, добавлены два только внутренних overload/seam-метода:
- `FindLatestWithEnumerator(sourcePath, enumerateManifests)`;
- `InspectIncompleteWithEnumerator(sourcePath, maxEntries, cancellationToken, enumerateEntries)`.

Рабочие публичные методы передают им обычные `Directory.EnumerateFiles` / `EnumerateFileSystemEntries` с прежними масками и без рекурсии. Тестовые делегаты позволяют симулировать отказ **уже во время** lazy iteration, без прав администратора и реального обрыва SMB.

### UI и безопасность

`RecoveryDiagnosticsPane` уже обрабатывает `IOException`, `UnauthorizedAccessException` и `SecurityException` с текстом «Не удалось проверить временные файлы» / «Доступ или безопасность каталога не подтверждены». Дополнительная кнопка удаления, восстановления или техническая автоматическая очистка **не добавлялась**. Пользовательские документы и WORM остаются недоступными для изменения из этой диагностики.

## Тестовые сценарии, записанные в репозиторий

`tests/GetDocument.Office.Tests/OfficeRecoveryFolderStateTests.cs` — **8 новых MSTest** на:

1. Отсутствующий корень recovery возвращает пустой набор без создания каталога.
2. Существующий, доступный пустой каталог возвращает пустой набор.
3. Тестовый `FileNotFoundException` / `DirectoryNotFoundException` как отсутствие.
4. `UnauthorizedAccessException`, `SecurityException` и обычная `IOException` при проверке атрибутов **не** означают отсутствие папки.
5. Неправильный тип объекта и reparse-point запрещены.
6. `FindLatest`: lazy iterator выдаёт один непригодный путь и затем исключение «каталог исчез» — нельзя вернуть `null`.
7. `InspectIncomplete`: аналогичный отказ после начала перечисления — нельзя вернуть чистый список.
8. Отказ перечисления по ACL/policy при наличии каталога остаётся исключением.

`scripts/tests/test_recovery_folder_state_contract.py` — **8 source-only unittest-методов**; новый gate `recovery-folder-state-source-tests` зарегистрирован до offline NuGet restore. В `test_recovery_chain_consistency.py` проверяются уникальность/порядок регистрации и ручных проверок. В `scripts/tests/test_office_acceptance.py` добавлены `not-run` проверки для двух платформ:

- `gd31j27ay-recovery-folder-state-windows-ntfs-manual-e2e`;
- `gd31j27ay-recovery-folder-state-astra-smb-manual-e2e`.

`releaseReady=false`.

## Неразрешённая неоднозначность ОС / SMB

**Нельзя обещать абсолютное различение «каталог отсутствует» и «сетевой ресурс недоступен» только по `File.GetAttributes`.** Если драйвер, SMB-сервер или сеть возвращает `DirectoryNotFoundException` для реально существующего, но временно недоступного пути, API не содержит доказательства обратного. Такое состояние всё ещё может трактоваться как отсутствие **на этапе первой проверки**. После успешной проверки каталога отказ во время перечисления всегда остаётся ошибкой.

Для более строгой гарантии в будущем нужны явный health/connection-state SMB или политика доверенного root + журнал последних подтверждённых каталогов. Здесь этих служб нет; не вводим непроверенный сервер и не утверждаем, что он уже подключён.

## Границы проверки

**Фактически:** записи сделаны в GitHub, исходники считываются обратно и сверяются статически. **Не выполнялись:** компиляция .NET, запуск MSTest, Python unittest, настоящие fault-injection Windows/Astra/SMB/NTFS, тесты GUI или реальных документов. Ввиду прямого требования «без сборки» всё остаётся Draft и не подлежит публикации в production.

После снятия ограничения рекомендованы:
```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_folder_state_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_recovery_chain_consistency.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~OfficeRecoveryFolderStateTests
```

Далее — ручная проверка реальных исключений NTFS/SMB, сетевого отключения перед `GetAttributes` и после него, исчезновения каталога во время `MoveNext`, неизменности исходного файла и корректного UI-состояния «не удалось проверить».

## Следующий этап

GD-31J.27az: изучить необработанные исключения переферийных файловых API в `ReadManifest` / `FindLatest` и оценить лимиты/стоимость многократной SHA+ZIP-валидации на слабых ПК — без сборки и без расширения автоматически удаляемых объектов.
