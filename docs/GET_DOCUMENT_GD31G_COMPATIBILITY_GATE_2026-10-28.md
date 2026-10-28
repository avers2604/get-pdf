# GD-31G — проверка совместимости офисных команд и защита аварийного восстановления

**Дата:** 28.10.2026. **Статус:** изменения исходников, автоматизированные тесты, статическая проверка. **Не production; настоящая сборка, локальный .NET, UNO и EuroOffice/SSO не тестировались.**

Наследует GD-31F (#65), GD-31E (#64), GD-31D (#63), GD-31C (#62), GD-31A (#60). Исходный issue #59.

## 1. Прозрачный выбор движка: никакой «фальшивой совместимости»

`OfficeFeatureCatalog` теперь содержит отдельный тип `OfficeFeatureProvider`:

| Провайдер | Собственные команды ГЭТ из UNO каталога |
| --- | --- |
| `LibreOfficeUno` | 18 зарегистрированных, только по совместимому типу DOCX/XLSX/PPTX и правам read-only |
| `EuroOfficeDocumentServer` | **0**, до проверки официального Docs API и реального адаптера |
| `OnlyOfficeLegacy` | **0**, никаких скрытых fallback |
| `CollaboraWopi` | **0**, это другой протокол |
| Неизвестный провайдер | **0**, fail-closed |

`ForProvider(kind, provider)` показывает доступные команды. `Require(kind, featureId, readOnly, provider)` отклоняет любую попытку переслать локальный UNO feature ID другому движку. Исходные `ForKind` и стандартные `Require` сохранены для совместимости существующего локального кода, но **`EditorDocument` и `OfficeEditorPane` теперь явно указывают `LibreOfficeUno`**.

Сторона `GetDocument.Collaboration` по-прежнему использует подписанную EuroOffice Docs API-конфигурацию и ограничение `immutableRevisionSinkReady=false`; готовность серверной сессии **не** считается подтверждением поддержки офлайн-операций. Тест на границу проверяет даже разрешённый подписанный `edit` launch — он не открывает ни одной UNO команды. **Этот этап не включает интеграцию полноценного веб-редактора EuroOffice в фирменную панель.**

## 2. Устойчивость локальных аварийных копий

`OfficeRecoveryStore.EnsureValidPackage` дополнен недорогой проверкой ZIP-оболочки перед публикацией snapshot и при повторном открытии:

- максимум **128 MiB** исходного файла и отдельной распакованной части, **512 MiB** суммарно распакованных частей;
- не более **4096** ZIP-частей, без повторяющихся имён (регистр игнорируется), опасных путей, Unix-симлинков, встроенных VBA/ActiveX;
- степень сжатия больших частей не может превышать **500:1**;
- обязательные `[Content_Types].xml` и документная часть соответствующего OOXML типа;
- если встречен неподдерживаемый ZIP-кодек, UI не оставляет необработанное исключение в фоновом checkpoint.

Это **не** полная антивирусная проверка, **не** полноценная ZIP-декомпрессия/CRC-проверка и **не** гарантия, что MS Office сумеет открыть копию. На серверной стороне уже есть более строгий `OfficePackageGuard`, и он **не дублируется как зависимость** в настольном приложении.

Аварийные snapshot продолжают храниться отдельно от исходных файлов и WORM. Поведение удаления только после успешного сохранения оригинала сохраняется.

## 3. Дополнения в тестах

- `OfficeFeatureCatalogTests`: проверка разрешения только локального провайдера, отказ от сервера/legacy/WOPI и неизвестных ID, точное двустороннее соответствие Python whitelist и C# каталога (не только проверка «найден в Python»).
- `EuroOfficeFeatureBoundaryTests`: в тестовом проекте Collaboration добавлена **только тестовая** ссылка на `GetDocument.Office` и регрессия, что подписанный EuroOffice launch (view/edit) не может автоматически активировать команды UNO.
- `OfficeRecoveryStoreTests`: отказ при повторяющихся ZIP-частях, внутреннем `../` и подозрительной степени сжатия. Оригинал и WORM не перезаписываются.

## 4. Что реально проверено

Статическая сверка исходников через GitHub: **18/18** ID зарегистрированы в C# каталоге, Python whitelist и Avalonia панели. Присутствуют явное ограничение по провайдеру, защита ZIP и тесты для них.

**Не запускались**: .NET 10 build, MSTest, реальный Windows LibreOffice, настоящий EuroOffice DocumentServer, SSO и UI/печатные E2E. Не следует использовать количество тестов или совпадение ID как доказательство работоспособности.

Для локальной Windows приёмки (GitHub Actions не включать):

```powershell
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeFeatureCatalogTests|FullyQualifiedName~OfficeRecoveryStoreTests|FullyQualifiedName~OfficeChoiceListTests|FullyQualifiedName~CalcChartAndValidationTests|FullyQualifiedName~CalcPivotAndChoiceListTests"
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~EuroOfficeFeatureBoundaryTests|FullyQualifiedName~EuroOfficePrimaryEngineTests"
```

Полная ручная проверка: DOCX-оглавления и кириллица, XLSX списки/сводные/диаграммы/формулы и Excel round-trip, PPTX дублирование слайда; сохранение исходной версии/несохранённых изменений, восстановления после падения, read-only, обычные локальные и SMB-файлы, A4/A3 и свой мастер печати. Проверить поведение разных версий LibreOffice (без SDK monkey-patch).

Для серверного EuroOffice отдельно: закреплённая версия + подтверждённый Docs API, LAN 2/5/10 сотрудников, SSO, запрет редактирования WORM, живой callback/immutable CAS, лицензионные требования, без доступа в Интернет. **До этой приёмки** `ForProvider(..., EuroOfficeDocumentServer)` остаётся пустым; собственные кнопки сервера не имитируются.

## 5. Дальше по совместимости

1. Стабилизировать существующие stacked Draft PR локальной компиляцией и тестами.
2. Разобрать не прошедшие UNO/OOXML сценарии, особенно `DataPilot` → редактируемый Excel PivotTable, проверку выбора и сохранения списков.
3. Только после подтверждения документированной API поверхности EuroOffice проектировать отдельно подписанный серверный `GetOfficeCapabilities` / команды собственной панели, без доступа к неразрешённым операциям.
4. Не выполнять merge без результатов и не включать GitHub Actions.
