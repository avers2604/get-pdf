# GD-31J.27c — отдельный pinned native SaveAs Event-v2 для лаборатории

**Дата:** 2026-11-01. **Основа:** GD-31J.27b (PR #115) → GD-31J.27a (#114) → GD-31J.27 (#113) → GD-31J.26 (#112).
**Статус:** подготовлены изменения исходников, модуль проверки и тесты; Python/MSTest, CMake, Qt/CEF, реальный SaveAs и offline VM **не запускались**. Не включать в production.

## Архитектурное решение

Две ветки имеют несовместимые native протоколы. В текущую pinned ветку **не переносится** старый `--open-staged-guarded-saveas-lab` с `argc==7` и не встраивается `getdoc-native-saveas-event-lab/2` от PR #100. Вместо этого добавлен **отдельный, новый и явно помеченный** режим:

- **Старый:** `--open-staged-native-saveas-lab STAGING FIXTURE SESSION TAB` → прежняя одна строка `getdoc-native-saveas-candidate-lab/1`; stdout и права не изменены.
- **Новый:** `--open-staged-native-saveas-event-v2-lab STAGING FIXTURE SESSION TAB` → `getdoc-native-saveas-pinned-event-lab/2`; содержит ту же **вложенную** `receipt` версии `candidate-lab/1` без расширения её полей.

Оба требуют только 3 синтетические фикстуры GD31J6, SHA и manifest, Linux/Wayland, точные SDK SHA, раздельный disposable output и существующую защиту GD31J26. Новая CMake опция `GETDOC_GD31J27C_ENABLE_NATIVE_EVENT_V2_LAB=OFF` требует **дополнительно** `GETDOC_GD31J27C_EVENT_V2_ISOLATION_CONFIRMED` и включённую `GETDOC_GD31J26_ENABLE_NATIVE_SAVEAS_LAB`. Опции по умолчанию выключены. Фактическая ОС-изоляция не гарантируется CMake-флагом.

## Нативные изменения

- `SyntheticEventObserver` для нового режима считает обращения `CEF_LOCALFILE_SAVE` и `CEF_ONSAVE` **только от ожидаемого CEF sender-ID**, независимо от ошибок тела события; атомарные `saveDialogCount`, `saveOnSaveCount` и `saveEventSerial` фиксируют первые порядковые номера `firstDialogSequence`, `firstOnSaveSequence`. Порядковые номера — не timestamp и не независимая подпись.
- `invalidDialogObserved` фиксирует запрос с отсутствующим/чужим вложенным ID. `unexpectedOnSaveObserved` — событие OnSave до передачи typed path или с отсутствующими данными. Никаких подробных путей и текста события не публикуется.
- Ответ `editor->Apply(LOCALFILE_SAVE_PATH)` остаётся на Qt owner thread; допускается только при ровно **одном валидном** запросе и остальных ready/binding/format/private-output guards. Счётчик корректных запросов сохранён для **обоих** режимов.
- Для **нового** режима выдача disposable output допускается только при `saveDialogCount==1`, `saveOnSaveCount==1`, `firstDialogSequence==1`, `firstOnSaveSequence==2`, отсутствии `invalidDialogObserved` и `unexpectedOnSaveObserved`, остальных callback/source SHA guards; прежний атомарный no-replace `link()` и отдельная OOXML проверка сохранены.
- В `event-v2` диагностике `failureCode` выбирается из `invalid_dialog`, `duplicate_dialog`, `unexpected_on_save`, `cancelled`, `source_modified`, `dialog_missing`, `target_not_assigned`, `timeout`, `save_not_acknowledged`, `candidate_missing`, `candidate_untrusted`. `phase`: `not_started`, `dialog_observed`, `target_assigned`, `on_save_observed`, `published_disposable`. Это только самоотчёт.
- **Важный нюанс:** duplicate callback, полученный *после назначения пути*, способен сделать некоторые флаги старой вложенной /1 квитанции противоречивыми. Для отрицательных /2 проверяется отдельное соответствие счётчиков/флагов, а старый строгий /1 кандидат валидируется только при положительном результате. Это НЕ разрешает downgrading или salvage неуспешного SaveAs.

## Читающие проверки

- `native_pinned_saveas_event_v2.py` — только read-only разбор **точной** схемы /2 (13 полей + 18 вложенных /1), GUID32, трёх fixtures, типов, повторов/unknown JSON полей, лимита 4096 ASCII, счётчиков до 64, последовательностей до 128, фаз, причины отказа, SHA и неизменных отрицательных полномочий.
- `EuroOfficePinnedNativeSaveAsEventV2.Parse` — строгий .NET эквивалент; отдельный тип без предоставления полномочий SaveAs, WORM или печати. Нельзя подать старую guarded `event-lab/2` или текущую `candidate-lab/1` как новый v2.
- `native_pinned_event_ooxml_join.py` — независимый **READ-ONLY** второй этап. Для единственного положительного синтетического v2 кандидата вызывает существующий `native_ooxml_join.correlate` и `synthetic_roundtrip.verify`: фактические output/source SHA, ZIP CRC, структура OOXML, единственная synthetic canary и повторное чтение. Отказы v2 **не превращаются** в подтверждённый OOXML. Не выполняет CEF, CLI команды и не записывает документы.
- Результат join выдаёт `getdoc-native-pinned-v2-ooxml-joined-lab/1`: лишь диагностическую корреляцию. Во всех формах **`nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `approvedForProduction=false`**.

## Пример будущего ручного E2E, не исполнять на корпоративной машине

Только в заранее одобренной disposable VM с отсутствующими сетевыми маршрутами/корпоративными SMB, WORM и пользовательскими файлами, отдельными pinned SDK/CEF библиотеками и юридическим подтверждением:

```sh
# Только после local CMake build, SHA pin и VM isolation.
# В новой лаборатории один сеанс, без уже существующего output.
./getdoc-eurooffice-cef-staged-open-lab \
  --open-staged-native-saveas-event-v2-lab \
  /ABSOLUTE/ISOLATED/STAGING gd31j6_writer.docx \
  102030405060708090a0b0c0d0e0f010 \
  f010e0d0c0b0a0908070605040302010
```

Сохранить отдельную **единственную** строку native stdout только средствами одобренной лаборатории; отдельно проверить exit status и binary SHA, затем через stdin:

```sh
python3 -B probes/eurooffice-desktop/cef_lab/native_pinned_event_ooxml_join.py \
  --staging /ABSOLUTE/ISOLATED/STAGING \
  --fixture gd31j6_writer.docx \
  --session 102030405060708090a0b0c0d0e0f010 \
  --tab f010e0d0c0b0a0908070605040302010 \
  < /ABSOLUTE/APPROVED/ONE-LINE-V2-NATIVE-RECEIPT.jsonl
```

Сам скрипт join **не удостоверяет**, что был запущен правильный нативный бинарник. Происхождение native процесса, библиотеки, CEF и изоляцию необходимо проверять отдельно. PR #114 supervisor сейчас работает **только с прежней /1**, и не должен автоматически принимать /2. Любое такое расширение требует собственного нового pinned process controller + tests.

## Отложенные тесты и блокеры

1. `test_native_pinned_saveas_event_v2.py` и `EuroOfficePinnedNativeSaveAsEventV2Tests.cs`: positive-untrusted, double-dialog, premature/duplicate OnSave, cancel/timeout, invalid sender, corrupt nested v1, forged privilege, cross-session, wrong type, unknown keys.
2. `test_native_pinned_saveas_event_v2_contract.py`: источник C++ и CMake opt-in, проверка, что штатный v1 остаётся отдельным, запрет production.
3. `test_native_pinned_event_ooxml_join.py`: независимое чтение DOCX/XLSX/PPTX и SHA/canary, forged output hash, missing native event, malformed/multiline stdout.
4. `scripts/office_acceptance.py`: отдельные offline Python gates до restore, отдельная обязательная ручная `eurooffice-pinned-native-v2-callback-counts-and-ooxml-join-e2e`.
5. Реальные Linux/Wayland CEF/SDK E2E, OS sandbox и filesystem containment, юридическая оценка лицензии AGPLv3 с дополнительными условиями, проверка бинарников, Microsoft Office reopen/format fidelity и восстановления сбоев остаются неподтверждёнными.

**Нет изменений:** штатный launcher и UI, GUP-2, GET-system, SMB/WORM, Nextcloud, печать, существующий LibreOffice fallback, GitHub Actions или `main`. Merge возможен только после лабораторной проверки.
