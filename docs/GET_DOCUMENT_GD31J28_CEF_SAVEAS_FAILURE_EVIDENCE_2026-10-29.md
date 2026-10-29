# GD-31J.28 — версия 2 протокола событий нативного EuroOffice Save As

**Дата:** 29 октября 2026. **База:** Draft PR #99 (GD-31J.27). **Статус:** код и тесты подготовлены; Qt/CEF, .NET и Python тесты, сборка, VM и MS Office round-trip **не запускались** по ранее установленному порядку. Этот протокол строго лабораторный и не предоставляет производственных прав.

## Задача и найденный недостаток

GD-31J.27 уже различает отмену, конфликт, дубликат и отсутствие `OnSave` *в нативном процессе*. Но исходная квитанция `getdoc-native-saveas-private-lab/1` содержит только флаги `saveAsRequestSeen/saveAsTargetAccepted/saveNotification/saveCancelled/candidateWritten`. Вне CEF невозможно достоверно различить `duplicate_dialog`, `target_conflict`, `timeout` и иные причины. UI поэтому либо показывал слишком общий результат, либо мог предполагать причину без соответствующего свидетельства.

## Реализованный протокол

Новая *обязательная для реального лабораторного запуска* схема `getdoc-native-saveas-event-lab/2` — строго ограниченный JSON-объект:

- `schema`: точная версия `/2`;
- `sessionId`, `tabId`, `fixtureName`: корреляция с вызывающим процессом;
- `failureCode`: **одно из фиксированных диагностических значений**: `not_started`, `dialog_missing`, `invalid_dialog`, `duplicate_dialog`, `target_conflict`, `cancelled`, `timeout`, `target_not_assigned`, `save_not_acknowledged`, `source_modified`, `private_output_invalid`, `candidate_missing`, `candidate_untrusted`;
- `phase`: `not_started`, `request_sent`, `dialog_observed`, `target_assigned`, `on_save_observed`, `quarantined`;
- `dialogCount`, `onSaveCount`: ограниченные счётчики callback ожидаемого CEF editor;
- `dialogSequence`, `onSaveSequence`: индексы первых соответствующих CEF callback в общем атомарном счётчике, **не время и не достоверный audit-trail**;
- `receipt`: вложенная неизменная по форме квитанция GD-31J.26 `getdoc-native-saveas-private-lab/1` с bounded SHA и флагами **`documentVerified/saveVerified/pdfVerified/printApproved=false`**.

Поля `failureCode` — **утверждение лабораторного native host об увиденном событии**, а не подписанный журнал, OS flush или доказательство корректного импорта/экспорта Office.

### Нативный код

`SyntheticEventObserver` сохраняет `saveAsEventSerial` и фиксирует последовательные номера первого `ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE` и первого `ASC_MENU_EVENT_TYPE_CEF_ONSAVE` только для ожидаемого editor sender-ID, с подсчётом повторов. При выдаче карантинной копии GD-31J.28 дополнительно проверяет:

- `dialogCount == 1`, `onSaveCount == 1`;
- первый запрос пути зафиксирован;
- `onSaveSequence > dialogSequence` — обратный порядок запрещён;
- предыдущие обязательные preconditions GD-31J.27: `saveAsTargetAssigned`, отсутствие `saveAsLifecycleAborted`, отмены, конфликтов, timeout; неизменный SHA эталона, иной SHA private output и запрет перезаписи.

После остановки CEF нативный host помещает вложенный `receipt` в новый `receiptV2`, добавляет код/стадию и счётчики, выводит одну JSON-строку. Не записывает никаких путей, содержимого документа, паролей или личных данных; не открывает сетевых ресурсов.

### .NET

- Новый `EuroOfficeNativeSaveAsEventReceipt.Parse` отказывает при неподдерживаемых полях и схемах, дублированных ключах, неверных session/tab/fixture, необоснованной успешности, отсутствии nested /1 receipt, невозможных последовательностях событий и несогласованных фазах.
- Возврат старой `/1` квитанции из **нового нативного процесса** запрещён: `EuroOfficeNativeSaveAsLab.RunAsync` принимает только /2. Старые `EuroOfficeNativeSaveAsReceipt.Parse` и `ReviewCandidate` сохранены для существующих unit-тестов, не для реальной нативной E2E-сессии.
- `EuroOfficeNativeSaveAsLab.Review` включает `NativeEvidence` и возвращает диагностический `Lifecycle` из `failureCode` только после валидации. Всё ещё выполняется независимая SHA-проверка исходного файла **до и после** CEF, затем `EuroOfficeNativeArtifactCandidate.InspectRevision` (bounded OOXML ZIP/XML); не используется произвольный путь или новый output вне синтетического staging.
- `EuroOfficeNativeSaveAsLifecycle.Classify(EuroOfficeNativeSaveAsEventReceipt, ...)` преобразует допустимый код в отображаемую причину. Статус `SavedCandidateValidatedButUntrusted` возможен только при совпадении callback-порядка и структурной проверке ZIP; `SaveCompleted/DocumentVerified/AuthorizedForPublication` при этом **всегда false**.

## Подготовленные тесты, пока НЕ выполнялись

- `EuroOfficeNativeSaveAsEventReceiptTests.cs`: положительный, но недоверенный кандидат, duplicate/timeout, нарушенный порядок callback, чужие ID, неизвестный код/фаза, переполнение счётчиков, ложные привилегии, дубликаты JSON, отказ от v1 в новом запуске.
- `test_native_saveas_event_receipt_contract.py`: наличие реальных CEF sequence counters, exact v2 nested JSON, новых guard и `RunAsync` с принудительным /2, запрет production authority.
- `scripts/office_acceptance.py`: offline source gate `cef-native-saveas-v2-event-receipt-source-tests` до restore; ручной `eurooffice-saveas-v2-native-event-order-failure-codes-e2e`.
- Для ручной автономной Linux/Wayland VM: запрос `Save As` до выдачи разрешения, правильный запрос, двойной запрос из разных iframe, OnSave до/после typed reply, cancel, аварийное завершение, сохранение в существующую цель, неполный OOXML/модификация эталона, процессный timeout, отсутствие доступного native SDK. Все 3 синтетических DOCX/XLSX/PPTX. Без рабочих файлов, секретов, сети и SMB/WORM.

## Ограничения

Проверка полей **не** аттестует достоверность самих callback. Порядковые счётчики — лишь локальная телеметрия. Native CEF может быть unsandboxed; вся цепочка допустима только в disposable изолированной VM. Разрешение на реальное редактирование, публикацию и транзакционное сохранение в GET System/GUP-2/Nextcloud/WORM по-прежнему **не включено**. Лицензирование AGPL и цепочку сборки следует отдельно проверить.

**PR остаётся Draft над #99, никаких изменений `main`, GitHub Actions, обычного лаунчера ГЭТ Документ или рабочего LibreOffice/UNO fallback.**

Следующий GD-31J.29: после реального лабораторного подтверждения — отдельная регламентированная проверка `fsync`/atomic publish для синтетических файлов с отказом от перезаписи и независимым открытием результата в редакторах; до этого ни один native receipt не может приводить к публикации WORM.
