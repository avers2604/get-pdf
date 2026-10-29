# GD-31J.34 — межмодульная согласованность нативных Save As квитанций v1/v2

**Дата:** 29 октября 2026. **База:** Draft PR #105, GD-31J.33. **Статус:** подготовлены исправления исходников, статические контракты и регрессионные тесты. Сборка, MSTest, Python tests, Qt/CEF и реальные лабораторные проверки **не выполнялись** по ранее согласованному порядку.

## Обнаружены два дефекта протокола

**1. Поздний повторный CEF Save As callback после назначения пути.** В GD-31J.28 нативная вложенная квитанция `getdoc-native-saveas-private-lab/1` формировала `saveAsRequestSeen` как смесь факта первого принятого запроса и **конечного критерия одиночного запроса** (`requestCount==1 && !rejected`). Если первый диалог был валидным и получил путь (`saveAsTargetAccepted=true`), а позднее пришёл второй callback, native host выводил `saveAsRequestSeen=false` при `saveAsTargetAccepted=true`. `.NET` v1-парсер **справедливо** запрещал такую комбинацию, поэтому вместо точного `DuplicateDialog` возникала общая ошибка `InvalidDataException`.

**Исправление:** нативная квитанция v1 хранит отдельный **исторический факт** `eventObserver.saveAsRequestSeen` (хотя бы один *валидный* typed dialog), который не аннулируется поздними повторами. Повторы отражаются отдельно в v2 `dialogCount`, `rejectedSaveAsRequest` и `failureCode=duplicate_dialog`. **Важно:** это не разрешение на сохранение — `privateDestinationValid` всё ещё требует `requestCount==1`, отсутствие `rejectedSaveAsRequest`, `saveAsLifecycleAborted`, отмены и прочие GD27–33 guards. При втором callback `candidateWritten=false`; новый карантинный файл или WORM-редакция не публикуются.

**2. Недостаточные перекрёстные проверки явного native failureCode.** `EuroOfficeNativeSaveAsEventReceipt.Parse` проверял allowlist кодов и некоторые их соотношения со счётчиками, но принимал диагностически противоречивые пары, например `failureCode=not_started` при `dialogCount=1` и `saveAsTargetAccepted=true`, `target_not_assigned` при назначенном target или `candidate_missing` вообще без `OnSave`. Хотя такие записи и не могли дать права публикации, UI получал бы **недостоверный статус**.

**Исправление:** строгий parser v2 дополнительно отвергает:
- `not_started` при любой активности диалога/OnSave/назначенном target или иной `phase`;
- `target_not_assigned` вместе с `saveAsTargetAccepted=true`;
- `candidate_missing` без зарегистрированного `SaveNotification`;
- `invalid_dialog` без событий, а также при ровно одном **валидном и уже назначенном** dialog.

Обязательные ранее введённые проверки `candidate_untrusted`, порядок `OnSave` после dialog, лимиты JSON/counters, неизменная SHA и `documentVerified/saveVerified/pdfVerified/printApproved=false` сохраняются.

## Новые регрессионные сценарии (написаны, не исполнялись)

- `EuroOfficeNativeSaveAsEventReceiptTests.LateDuplicateAfterAssignedTargetRemainsParseableAsUnsafeFailure`: первая валидная typed Save As квитанция и принятый target, **два** dialog события, ноль OnSave, без candidate — должна классифицироваться как `DuplicateDialog`, все полномочия false.
- `InvalidDialogAfterAcceptedRequestStillCannotAuthoriseCandidate`: повтор не даёт `CandidateWritten`, а сфальсифицированное `candidate_untrusted` с `dialogCount=2` отвергается.
- `RejectsFailureReasonThatContradictsObservedNativePhase`: невозможные combinations не должны доходить до отображения UI.
- `EuroOfficeNativeSaveAsLabTests.LateDuplicateAfterAcceptedPathReturnsExactDiagnosticWithoutOutput`: реальная связка методов .NET `ReviewCandidate → NativeEvidence → Lifecycle` на синтетическом fixture без создания quarantine.
- `test_native_saveas_event_receipt_contract.py`: source-only проверка сохранения исторического факта запроса и независимого strict `privateDestinationValid` с одиночным count; проверка новых валидаций failureCode и наличия v2.
- `scripts/office_acceptance.py` добавляет ручной этап `eurooffice-gd31j34-cef-duplicate-after-target-v2-receipt-manual-vm`, `test_office_acceptance.py` проверяет его наличие. GitHub Actions не включаются.

## Ограничения

Это **только синтетический** native Save As и redacted diagnostic receipt. Не доказано, что реальные SDK callbacks приходят в указанном порядке. Не запускались приложения, .NET/Python тесты и отключения питания. Протокол не подписан, остаётся локальной телеметрией и не даёт полномочий GET-system, GUP-2, Nextcloud, WORM или вывода о совместимости Word/Excel/PowerPoint.

Сохранение исходного признака `saveAsRequestSeen=true` **никогда не должно** считаться подтверждением, что новая редакция безопасна: только строгий валидатор candidate, private output и ровно один callback. Требуются отдельные Qt/CEF/Wayland VM-проверки с поздним дублирующим событием **после** typed reply и при отсутствии/наличии `OnSave`.

**Следующий GD-31J.35:** расширить доказательное static review взаимодействия CEF/NET, стабилизировать синтетические тестовые данные и завершить пакет исправлений, прежде чем разрешать локальную сборку. Изменения только Draft, `main` не затронут.
