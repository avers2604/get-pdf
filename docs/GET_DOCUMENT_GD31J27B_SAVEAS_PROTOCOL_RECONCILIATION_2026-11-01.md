# GD-31J.27b — две несовместимые линии EuroOffice Save As: только диагностика, без повышения доверия

**Дата:** 2026-11-01. **База:** Draft PR #114 (GD-31J.27a), на #113 → #112. Исходники и тесты подготовлены; реальные компиляция, запуск Qt/CEF, Python/MSTest, VM, редактирование и сохранение **не выполнялись**.

## Проверенные источники, не путать

1. **Ранняя линия:** PR #99 (`feature/gd31j27-saveas-lifecycle-hardening`) на #98 — `argc==7`, `--open-staged-guarded-saveas-lab`, explicit format, контроль SaveLock/дубликатов/отмены. PR #100 (`feature/gd31j28-native-saveas-failure-receipt`) поверх #99 — `getdoc-native-saveas-event-lab/2`: `failureCode`, `phase`, `dialogCount`, `onSaveCount`, `dialogSequence`, `onSaveSequence` и вложенная `getdoc-native-saveas-private-lab/1`.
2. **Новая pinned линия:** PR #112 (`feature/gd31j26-synthetic-native-saveas-lab`) — `argc==6`, `--open-staged-native-saveas-lab`, временная копия исходника внутри disposable staging, атомарный no-replace output, `getdoc-native-saveas-candidate-lab/1`. PR #113 добавил независимо проверяемый OOXML/SHA/CRC/canary; PR #114 — hash-pinned process supervisor перед ним.
3. **Статус:** ветки разошлись. Номера GD-31J.27 повторяются, но коды, имена выходных файлов, жизненные циклы и схемы не взаимозаменяемы. PR #100 не является родителем PR #112; нельзя cherry-pick без адаптации и проверки.

## Что фактически добавлено в GD-31J.27b

**Отдельный чисто диагностический read-only анализатор** `probes/eurooffice-desktop/cef_lab/native_saveas_protocol_reconciliation.py`.

Он получает **ровно одну** ограниченную ASCII JSON-строку, строго различает две исходные схемы, проверяет точные поля/типы, отсутствие дублирования ключей, идентичность `sessionId/tabId/fixtureName` и допустимость всех synthetic fixture. Он **не преобразует** `/2` в `/1`, не подключается к текущему `native_ooxml_join.py` или `native_ooxml_supervisor.py`, не читает/сохраняет документы и не запускает Qt/CEF.

- Для **GD31J26 pinned /1** строго проверяет `nativeSaveRequestObserved`, `savePathProvided`, `nativeOnSaveAfterAnswer`, `saveCancelledAfterAnswer`, `sourceUnchanged`, `publishedToDisposableOutput`, формат выходного SHA-256 и все постоянно запрещённые production-флаги. Его fail-code *не сообщается* в исходном native /1: выдаётся лишь `unspecified_failure`, точная причина не выдумывается.
- Для **GD31J28 guarded /2** отдельно строго проверяет 11 полей envelope, 20 полей вложенного `private-lab/1`, допустимые диагностические `failureCode/phase`, ограниченные счётчики и последовательности событий, формат, original SHA before/after, имя `gd31j26-saveas-candidate-<session>.<format>`, длину и digest результата, согласованность отказов и флагов. `nativeEventOrderSelfReported=true` только у позитивного guarded-кандидата с последовательностью 1× запрос → 1× OnSave. Это **самоотчёт**, не криптографическая аттестация.
- Общий **новый, но не привилегированный** envelope `getdoc-native-saveas-protocol-diagnostic-lab/1`: `sourceProtocol` точно `gd31j26-pinned/1` или `gd31j28-guarded/2`, `diagnosticState` равен `synthetic_candidate_untrusted` или `unconfirmed`, `reasonCode`, выходной SHA при кандидате и `nativeEventOrderSelfReported`. Нет путей, пользовательских текстов, ключей или raw событий.
- `EuroOfficeSaveAsProtocolDiagnostic.Parse` в .NET принимает **только этот новый envelope**, проверяет происхождение версии, согласование state/reason/sha/callback, дубликаты, ошибки типов, GUID, запрет ложных прав. Он **не является парсером исходного native output** и не удостоверяет его подлинность.
- Во всех версиях и результатах **`nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `approvedForProduction=false`**. Протоколы, даже совпавшие, не доказывают содержимое OOXML. Для новой линии сохраняется отдельный независимый `GD31J25/27` verifier.

## Строгая карта совместимости

| Правило / механизм | Guarded #99/#100 | Pinned #112–#114 | Политика GD31J27b |
|---|---|---|---|
| Qt/CEF запуск | `argc==7` + format | `argc==6` + auto fixture format | Не объединять маршруты, пока не выполнены real VM E2E |
| Выходной файл | session-bound quarantine | fixed `gd31j25-output` no-replace | Нельзя считать файлы одинаковыми или менять путь из JSON |
| Отмена/повтор callback | подробная native диагностика | ограниченные native boolean-флаги | Guarded reason не переносить на pinned без нового наблюдения |
| Порядок dialog → OnSave | native счётчики и sequence | только флаг after-answer | Различать уровень подробности; оба self-reported |
| Процесс и receipt | Guarded /2 + вложенная private /1 | Pinned candidate /1 + optional supervised envelope | Reject смешение схем / downgrade |
| OOXML integrity | old artifact inspection | independent ZIP CRC + SHA + canary | Сохранить independent verifier новой ветки |
| Реальное сохранение/WORM | Запрещено | Запрещено | Общее правило: deny |

## Тесты и локальная приёмка — пока отложены

- `test_native_saveas_protocol_reconciliation.py`: хорошие отдельные synthetic receipts обеих линий; устаревший protocol mismatch, лишние/повторные поля, подменённая сессия/вкладка, отмена/отсутствие callback, пропущенный output, неверный SHA/формат/размер, повреждённая фазовая последовательность, ложные права, мультистрочный/oversized stdout. Сетевой доступ и реальные процессы не используются.
- `test_native_saveas_protocol_reconciliation_contract.py`: наличие точных схем, отсутствие shell/сохранения и невозможность downgrade текущего supervisor.
- `EuroOfficeSaveAsProtocolDiagnosticTests.cs`: строгий .NET parser, отрицательные сценарии spoofing/mixed lineage, права всегда false.
- `scripts/office_acceptance.py`: обе Python группы до offline restore, отдельная обязательная **ручная** `eurooffice-v1-v2-saveas-protocol-lineage-reconciliation-e2e`.
- На изолированной VM выполнить независимо оба бинарных сценария с правильными pinned SHA и подтверждёнными лицензиями, сделать manual negative event matrix и повторное чтение DOCX/XLSX/PPTX. Только затем решать, какие CEF callback-gates переносить из #100 в текущий pinned native SDK. **Ни unit-тесты, ни локальная сборка, ни E2E здесь не запускались**.

## Следующее изменение: GD-31J.27c

Проверить возможность аккуратно **добавить в новую pinned ветку нативные ограниченные счётчики и reason-коды из старой** с новой версией native квитанции (без ломания /1 и без автоматического downgrade). До фактических Qt/CEF tests не считать `OnSave` доказательством сохранения. Далее — финальная лаборатория, совместимость с Word/Excel/PowerPoint и только после неё отдельная транзакционная операция пользовательского Save As.

**Нет изменений** `main`, GitHub Actions, production launcher, совместного редактирования, GUP-2, GET-system, WORM/SMB, Nextcloud, PDF/печати, LibreOffice/UNO fallback.
