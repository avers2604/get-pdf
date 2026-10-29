# GD-31J.37 — сквозная сверка CEF → .NET → quarantine → manifest → journal

**Дата:** 29 октября 2026. **База:** Draft PR #108 (GD-31J.36). **Статус:** исходники и регрессионные тесты подготовлены; **сборка, запуск тестов, Qt/CEF/VM, физическая проверка ФС не выполнялись**. Изменения относятся исключительно к синтетической лаборатории, не к пользовательскому Save As или производственному WORM.

## Проверенные участки

1. Нативный Qt/CEF формирует `getdoc-native-saveas-event-lab/2` с вложенной `getdoc-native-saveas-private-lab/1`. Условия синтетического результата требуют допустимые callback, неизменный исходник и проверенный карантин.
2. Строгий транспорт .NET GD-31J.36 ограничивает stdout 4096 символами только для nested v2 (прежний binding v1 — 2048), запрещает лишние строки, CR/NUL/управляющие символы и отрицательный код завершения native процесса.
3. `EuroOfficeNativeSaveAsEventReceipt.Parse` проверяет allowlist фаз, счётчики, порядок callback и nested receipt; `EuroOfficeNativeSaveAsLab.ReviewCandidate` проверяет файловый OOXML ZIP и SHA-256 без публикации документа.
4. `EuroOfficeSyntheticReviewedSnapshot.Commit` допускает только явно одобренную synthetic lab и корректное CEF v2 evidence; GD-31J.35 проверяет манифест трёх локальных fixtures, после чего GD29–33 создают synthetic pending/hardlink и GD30 журнал. GD31 read-only диагностика не разрешает восстановление или удаление.

## Установленные дефекты и изменения

### 1. Native timeout после OnSave неправильно считался невозможным

В реальном C++ блоке `failureCode` нативный `saveAsLifetimeTimeout` может породить `timeout` **после** регистрации `onSaveCount > 0`, но до создания подтверждённой карантинной копии. Отдельный `nativePhase` в этой ситуации будет `on_save_observed`. Прежняя проверка .NET `code == "timeout" && onSaveCount != 0` выбрасывала `InvalidDataException` и теряла точную причину отказа, хотя сама нативная квитанция соответствовала своим правилам.

**Исправлено:** `EuroOfficeNativeSaveAsEventReceipt.Parse` допускает `timeout` после `OnSave`, когда nested receipt и фазовые счётчики согласованы, но по-прежнему отвергает `timeout` в `not_started`. Допустимый диагностический отказ **не становится успешным сохранением**: `CandidateWritten=false`, `EuroOfficeNativeSaveAsLifecycle.State.Timeout`, `SaveCompleted=false`, `AuthorizedForPublication=false`. Никаких прав на WORM, регистрацию редакции в ГЭТ-system, печать или изменение исходника.

**Написаны тесты (не запускались):**
- `EuroOfficeNativeSaveAsEventReceiptTests.TimeoutAfterObservedOnSaveRemainsAValidFailureNotASave`: синтетическая nested v2 квитанция после OnSave распознаётся как Timeout без сохранённого результата;
- `EuroOfficeNativeSaveAsLabTests.PostOnSaveTimeoutRetainsFailureThroughCandidateReviewWithoutWriting`: `ReviewCandidate` возвращает пустого кандидата и диагностический Timeout, не создавая quarantine.

### 2. Повторные/чужие поля корневого объекта манифеста не отвергались

GD-31J.35 ужесточил проверку **трёх вложенных files entries**, но корень JSON продолжал читаться через `GetProperty("schema")`, `GetProperty("purpose")`, `GetProperty("files")` без строгого перечня полей. Невалидные синтетические манифесты с повторной `schema`, `purpose`, `files` или сторонним полем могли проходить проверку, если последняя версия значения совпадала с ожидаемой.

**Исправлено:** `RequireManifest` отказывает для любого корня, не являющегося JSON-объектом, а также для любого повторного, отсутствующего или постороннего поля. Разрешены строго `schema`, `purpose`, `files` с корректными типами и значениями. Далее сохраняется проверка **всех трёх реальных fixtures**, SHA-256 и длины каждого. Проверка корня выполняется **до** создания staging pending, JSONL журнала или hardlink.

**Написан тест (не запускался):** `EuroOfficeSyntheticManifestIntegrityTests.DuplicateOrUnknownManifestRootFieldsMustFailBeforeAnyJournal` — четыре вредоносных варианта: двойная schema/purpose/files и неизвестное поле. Ни один не должен создавать запись в каталоге reviewed.

## Локальная приёмка

Новый offline source-only `probes/eurooffice-desktop/cef_lab/test_synthetic_terminal_manifest_contract.py` фиксирует:
- соответствие native `timeout`/OnSave и разрешённого .NET parser;
- отсутствие разрешений SaveCompleted/AuthorizedForPublication;
- строгое корневое перечисление manifest и порядок `RequireManifest` до `Journal.Begin`;
- наличие всех регрессионных сценариев.

`scripts/office_acceptance.py` добавляет `cef-synthetic-terminal-manifest-contract-tests` до restore и отдельный ручной gate `eurooffice-gd31j37-post-onsave-timeout-and-strict-manifest-vm`. Тесты acceptance обновлены. **Ни source tests, ни MSTest не запускались**.

## Остаточные риски

Текущие исправления убирают исходные логические противоречия, но не доказывают правильность native callback в Wayland CEF, корректность синтетических OPC файлов в Microsoft Office или устойчивость журналирования после kill/power-loss. Существуют гонки замены файлов между SHA и hardlink (TOCTOU), directory metadata fsync не обеспечен, а synthetic manifest без независимой подписи не защищает от одновременной подмены байтов и манифеста. Нельзя использовать эти компоненты как trusted audit/WORM/recovery.

**Следующий этап GD-31J.38:** окончательная консолидация списка blockers и подготовка воспроизводимого локального build/test пакета без выполнения; после отдельного разрешения — полноценная локальная сборка и проверка уже накопленных Draft PR. `main`, GitHub Actions, launcher, GUP-2, Nextcloud, WORM и пользовательские документы не изменялись.
