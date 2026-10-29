# GD-31J.27 — защита жизненного цикла EuroOffice Save As и устранение ошибок GD-31J.26

**Дата:** 29 октября 2026. **База:** Draft PR #98 (`feature/gd31j26-guarded-native-saveas-lab`). **Статус:** исходники + MSTest + Python source contracts написаны, **но не собраны и не запущены**. Все операции остаются в отдельной синтетической офлайн-лаборатории.

## Обнаруженные дефекты, исправления

### 1. Критическая ошибка маршрутизации CLI GD-31J.26

- `--open-staged-guarded-saveas-lab` принимает `argc == 7` (имя исполняемого файла, команда, staging, fixture, sessionId, tabId, format).
- В GD-31J.26 базовые `readyLab`, `conversionLab`, `bindingLab` были последовательно вычислены так, что `conversionLab` и `bindingLab` разрешались **только при `argc == 6`**. Это означало ранний отказ `if (!ordinaryLab && !osrLab && !streamLab && !bindingLab) return 2` для нового Save As. Поскольку сборка и тесты отложены, дефект не был выявлен ранее.
- Теперь каждый дочерний режим наследует уже проверенный родительский независимо от `argc`; `saveAsLab` требует одновременно `bindingLab`, `conversionLab`, `readyLab`. Классические шестипараметрные лаборатории по-прежнему ограничены `argc == 6`.
- Дополнительный явный guard `if (saveAsLab && app.platformName() != "wayland") return 9`; на неподходящих платформах нет никакой автоматической подмены Linux/X11 или запусков через production launcher.

### 2. Потенциальная блокировка SDK при ответе на Save As

- GD-31J.26 выполнял `editor->IsSaveLocked()` для всего цикла, включая отправку ответа `LOCALFILE_SAVE_PATH` на уже полученный запрос. Если native converter устанавливает save lock после запроса, дальнейший ответ мог не быть отправлен.
- Теперь `IsSaveLocked` проверяется **до** разового инициирования команды Save As. На уже полученный типизированный callback (от ожидаемого view ID и правильного формата) ответ допускается независимо от временного lock, но только пока не произошло никакого отказа/конфликта.

### 3. Повторные, незапрошенные, запоздавшие CEF-события

- По умолчанию `eventObserver.acceptSaveAsDialog=false` — CEF `LOCALFILE_SAVE` до ручного запуска точной статической команды отклоняется.
- Только непосредственно перед `ExecuteInAllFrames` разрешается **один** запрос от ожидаемого native view. После назначения единственного пути флаг немедленно сбрасывается.
- При втором диалоге, отклонённом sender/format, отменённом `OnSave` или занятом `QTemporaryDir`-назначении Qt owner-thread выставляет `saveAsLifecycleAborted` / `saveAsTargetConflict`. После этого **никакие дальнейшие события не могут активировать выдачу назначения или карантинной копии**.
- Признак `saveAsLifetimeTimeout` устанавливается, если за ограниченное время native session не получила `OnSave`. Итоговая проверка кандидата требует отсутствия обоих отказов и timeout.
- Конфликт имени конечного карантинного файла перед `QFile::copy` останавливает запись. Операция принципиально не удаляет старый файл и не выполняет overwrite.

### 4. Независимая проверка .NET до запуска native процесса

- `EuroOfficeNativeSaveAsLab.RunAsync` помимо прежней `EuroOfficeOsrStreamProcessLab.CreateStartInfo` (SHA-pinned native executable, проверка staging/fixture и платформы) теперь **до процесса** проверяет наличие файла или директории с ожидаемым session-bound именем карантинного кандидата. Если они уже существуют, вызывается `IOException`, native процесс не запускается.
- Исходный fixture хешируется независимо **до** и **после** CEF. Нативная Save As-квитанция строго сравнивается с исходной, а OOXML-архив проверяется посредством `EuroOfficeNativeArtifactCandidate.InspectRevision`. Публикация и сохранение WORM по-прежнему запрещены.
- Добавлен `EuroOfficeNativeSaveAsLifecycle`: fail-closed диагностическая классификация видимых признаков `DialogNotObserved`, `TargetNotAccepted`, `DuplicateDialog`, `TargetConflict`, `SaveCancelled`, `SaveNotAcknowledged`, `CandidateMissing`, `CandidateInvalid`, `Timeout` и `SavedCandidateValidatedButUntrusted`.
- Классификатор различает **имеющиеся в квитанции** признаки. Коды повторного диалога, конфликта и timeout могут задаваться из доверенного уровня лабораторной обвязки, но в текущем JSON-протоколе GD-31J.26 **нет отдельного сериализованного failureCode**. Поэтому операторский UI не должен утверждать точную причину, если она не выведена из доступных receipt-флагов.
- Даже успешный локальный structural OOXML review является только кандидатом. `SaveCompleted`, `DocumentVerified`, `AuthorizedForPublication` всегда false.

## Какие тесты подготовлены (но НЕ выполнялись)

- `EuroOfficeNativeSaveAsLifecycleTests.cs`: положительная, но **непривилегированная** диагностическая классификация; незапрошенный callback, отмена, отсутствие `OnSave`, отсутствие/ошибка кандидата, дубликат, конфликт, timeout и фальшивая сессия.
- `test_native_saveas_lifecycle_contract.py`: `argc==7` обязан проходить все подготовленные зависимости; реальный SDK callback, запрет спонтанного запроса, прекращение попытки при дубликате/отмене/конфликте, раздельная обработка `IsSaveLocked` перед запросом и пути назначения после.
- `scripts/office_acceptance.py`: новый offline source gate `cef-native-saveas-lifecycle-source-tests` до restore и отдельный обязательный ручной E2E `eurooffice-saveas-seven-argument-route-duplicate-conflict-timeout-e2e`.
- В автономной Linux/Wayland VM потребуется отработать: живой DOCX/XLSX/PPTX, отсутствие modified, позднее modified, `SaveAs` без callback, вторая CEF-нотификация, подмена sender ID/format, блокировка конвертера во время диалога, существующая цель и карантинная копия, `OnSave` после timeout, усечение/обрыв процесса, несовпадение SHA, corruption OOXML и проверка отсутствия доступа к пользовательскому/WORM-хранилищу.

## Ограничения и дальнейшие условия

**Реальная функциональность EuroOffice Save As, редактирование OOXML, совместимость Word/Excel/PowerPoint и безопасный импорт остаются неподтверждёнными** без физического стенда и настоящих прогонов. Срабатывание native `OnSave` не означает, что данные правильно сохранены. Структурная проверка ZIP/XML не даёт полной совместимости. Native CEF может быть несандбоксирован; sandbox, сеть, файловые разрешения и AGPL compliance должны быть отдельно проверены перед производственным использованием.

Настоящие пользовательские файлы, Main launcher, WORM/CAS, GUP-2, мастер печати, Windows/Astra desktop и GitHub Actions не изменялись. **Draft, не сливать в `main` без испытаний и безопасности.**

Следующий возможный GD-31J.28: отдельный проверяемый протокол причин native failure с точной корреляцией события/времени (без абсолютизации `OnSave` как proof), а после стенда — транзакционный пользовательский Save As.
