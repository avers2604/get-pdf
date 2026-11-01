# GD-31J.27e — предохранительный автомат событий native Save As v2

**Дата:** 2026-11-01. **База:** Draft PR #117 (GD-31J.27d), через #116 → #115 → #114 → #113 → #112. **Статус:** исходный код, тесты и матрица подготовлены; реальный Qt/CEF SDK, CMake, .NET, Python и Linux/Wayland VM **не запускались** по согласованному порядку. Подтверждений функциональной готовности нет.

## Факты по аудиту исходного кода PR #117

До этой правки `SyntheticEventObserver.nativeSaveLabEnabled=true` устанавливался до `OpenLocalFile`. В v2-потоке `CEF_LOCALFILE_SAVE` мог получить pending path даже *до* `onContentReady/modified/bindingSeen/conversionObserved`, если callback пришёл от нужного view-ID. Проверка на Qt таймере откладывала выдачу пути, но **не делала преждевременное событие терминальным**: по мере появления ready/modified оно могло быть принято задним числом. Это потенциальный loophole жизненного цикла.

Помимо этого, второй `OnSave` блокировал публикацию из-за счётчика `!=1`, но native output не различал `duplicate_on_save` от `candidate_missing`. При срабатывании общего 90-секундного таймера `app.quit()` процесс мог содержать положительные флаги callback, при этом v2-публикация ещё проверяла только `exitCode==0`. Тайм-аут **не был явным терминальным основанием** для отказа после подтверждённого `OnSave`.

## Исправленная v2-модель (старый /1 не меняется)

Новый `native_pinned_saveas_state_model.py` — **чистая Python спецификация** атомарной части жизненного цикла. Не заменяет native SDK, Qt event loop, lock-free доказательство C++ или OS sandbox. В C++ включается **только** при `GETDOC_GD31J27C_NATIVE_EVENT_V2_LAB` и только новой команде `--open-staged-native-saveas-event-v2-lab`.

1. **Не вооружён**: `v2DialogArmed=false` по умолчанию. Любой `CEF_LOCALFILE_SAVE` до вооружения учитывается, помечается `invalidDialogObserved` и переводит автомат в `v2TerminalAbort=true` — даже если после этого появятся ready/binding.
2. **Разовое вооружение**: на Qt owner-thread таймере, только если правильный editor/view, `cefViewLoaded`, `onContentReady`, `modified`, `bindingSeen` и `conversionObserved` подтверждены, а событий SaveAs/OnSave ещё не было. Это readiness-gate; **не является доказательством явного волеизъявления пользователя или корректного открытия Office-файла**.
3. **Единственный запрос**: первым `LOCALFILE_SAVE` атомарно потребляется `v2DialogArmed.exchange(false)`, счётчики serial/dialog записываются для ожидаемого sender-ID; неправильный nested ID, отсутствие данных, callback до готовности и второй dialog переводят в терминальный отказ. Счётчик валидных запросов остаётся для совместимости вложенной /1 квитанции.
4. **Единственный ответ**: Qt owner-thread применяет typed `LOCALFILE_SAVE_PATH` только если ещё нет abort, `dialogCount==1`, `onSaveCount==0`, первый serial равен 1, `v2DialogArmed=false`, совпадают requested/file/output formats, не заняты заранее одобренные private/final destinations и выполнены старые GD31J26 readiness guards. Несоответствие формата помечается как `invalid_dialog`, после этого ответ запрещён.
5. **Единственный OnSave**: первый правильный callback после отправленного ответа получает sequence 2; ранний/пустой callback → `unexpected_on_save`, повторное подтверждение → `duplicate_on_save`, отмена → `cancelled`. Все ошибки — **липкие**, повторными событиями их нельзя отменить.
6. **Тайм-аут**: 90-секундный Qt timer выставляет `v2TimedOut=true` и `v2TerminalAbort=true` *перед* закрытием event loop. Публикация синтетической копии запрещена даже при ранее пришедшем OnSave. Если callback пришёл уже после timeout, новый отказ `unexpected_on_save` имеет диагностический приоритет.
7. **Завершение**: только после закрытия CEF/Qt, при отсутствии отказа/timeout, ровно (dialog,save,first-dialog,first-save) = (1,1,1,2), прежних SHA/source/content guards и правильного disposable output допускается *исключительно лабораторная* `NativeSaveAsPublish`. Реальной пользовательской операции нет.

### Уточнённые коды отказа

Новая диагностическая строка `duplicate_on_save` добавлена в **те же** allowlist проверяющие компоненты Python и C#: `native_pinned_saveas_event_v2.py` и `EuroOfficePinnedNativeSaveAsEventV2.Parse`. Кроме того, `timeout` разрешается для непривилегированного v2-результата не только вместо `save_not_acknowledged`, но и вместо `candidate_missing` — в случае, когда OnSave пришёл до завершения срока, однако timer всё равно завершил эксперимент. **Схема JSON не меняется**: `getdoc-native-saveas-pinned-event-lab/2` с вложенной `getdoc-native-saveas-candidate-lab/1`.

`timeout` или `duplicate_on_save` при `publishedToDisposableOutput=true` по-прежнему невозможны. Positive receipt остаётся только `candidate_untrusted`, `nativeSaveProven/documentVerified/saveVerified/approvedForProduction` — всегда `false`.

## Негативная матрица (отложена)

| Сценарий | Ожидаемая диагностика | Публикация |
|---|---|---|
| Один ready→dialog→answer→OnSave | `candidate_untrusted` *только при успешной проверки synthetic output* | Исключительно disposable lab |
| Dialog до вооружения ready | `invalid_dialog` | Нет |
| OnSave до ответа | `unexpected_on_save` | Нет |
| Повторный dialog до/после ответа | `duplicate_dialog` | Нет |
| Повторный OnSave | `duplicate_on_save` | Нет |
| Неверный запрошенный формат/вложенный ID | `invalid_dialog` | Нет |
| OnSave с отменой | `cancelled` | Нет |
| Исходник изменился | `source_modified` | Нет |
| Timeout до или после OnSave | `timeout` (или `unexpected_on_save` при позднем callback) | Нет |
| Callback после завершения/закрытия | Терминальный отказ при повторной обработке; реальная гонка teardown требует VM проверки | Нет |
| Шторм из 130 callback | Счётчики ограничиваются в модели; в native атомарные счётчики необходимо проверить в отдельной bounded hardening E2E | Нет |
| CEF exit non-zero/child crash или неверный binary/runtime SHA | Отказ внешнего PR #117 supervisor | Нет |

`test_native_pinned_saveas_state_model.py` перебирает все перестановки `arm/dialog/answer/on_save` и отрицательные ветви, производит synthetic v2 receipts и проверяет их существующим **строгим** Python v2 parser. `test_native_pinned_saveas_state_source_contract.py` закрепляет presence C++ arm/terminal/timeout guards и неизменность старой /1 ветки. Дополнительные тесты Python и MSTest проверяют `duplicate_on_save`, `timeout` *после* OnSave и запрет forged success.

## Границы доверия / реальные блокеры

- C++ `std::atomic` улучшает согласованность флагов, но **не** заменяет доказательство callback ordering, sender authenticity, owner-thread lifecycle и корректной синхронизации перед teardown; первые последовательные номера — самоотчёт нативного процесса. Reentrancy и событие прямо во время `Apply` требуют реальной Linux/Wayland VM.
- Модель Python (в том числе ограниченный MAX_EVENTS=64) не доказывает, что native callbacks физически ограничены: реальные C++ счётчики `fetch_add` пока могут увеличиваться сверх budget. Отдельно проверить/ограничить under malicious callback storm перед допуском стенда.
- В v2 отсутствует автоматическая команда инициирования SaveAs: оператор должен отдельно вызвать действие в изолированном UI. Готовность редактора не является согласием пользователя, а `OnSave` — подтверждением долговечного сохранения. Общий 90-секундный таймер требует ручного завершения эксперимента **раньше тайм-аута**, иначе даже чистый `OnSave` будет отвергнут. Это безопасное, но неудобное лабораторное ограничение.
- Никакая часть GD31J27e не запускает, не завершает, не перезаписывает пользовательский PDF/DOCX/XLSX/PPTX, сетевые share, WORM или GET-system. Старые `/1` и `guarded /2` из другой ветки не смешиваются. Отдельные pinned process+library SHA (PR #117) и OOXML canary (PR #116) остаются обязательными.
- **Ни один тест не запускался.** Нужны офлайн-линтер/сборка C++ с pinned SDK, Python/MSTest, негативная callback-матрица и реальный VM E2E. Не включать PR в `main`/релиз по одним исходникам.

**Следующий GD-31J.27f:** bounded/overflow-sensitive native callback counters, post-teardown synchronization, отмена застрявших ответов и доработка лабораторного UI инициирования Save As. После — полный реальный стенд и единый финальный вариант протокола.
