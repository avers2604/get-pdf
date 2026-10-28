# GD-31J.7 — события EuroOffice, кандидат новой редакции и PDF для ГЭТ

**Дата:** 29 октября 2026. **Режим работы:** функциональная разработка; по решению пользователя локальные тесты, сборка и лаборатория **отложены**. Это не релиз и не внедрение в рабочее приложение. Ветка поверх GD-31J.6 (#74); `main` без изменений; GitHub Actions не используются.

## 1. Подтверждённые API upstream

Исследованы закреплённые исходники `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`:

- `applicationmanager.h`: `SetEventListener(CAscCefMenuEventListener*)` и `GetEventListener()`.
- `applicationmanager_events.h`: `CAscCefMenuEventListener::OnEvent` с передачей владения `Release()`; `ASC_MENU_EVENT_TYPE_CEF_MODIFY_CHANGED=1012`, `ONSAVE=3002`, `PRINT_END=5013`. `CAscDocumentOnSaveData::get_IsCancel()` позволяет отличить уведомление об отменённом сохранении.
- `qcefview.h`: `OnLoaded()` относится к CEF view, **не** к доказанному импорту OOXML.
- `cefview.h`: `GetFileFormat` и `OpenLocalFile`, использованные в GD-31J.6, не дают отдельной подтверждённой транзакции публикации новой версии.

**Вывод:** `ONSAVE`, `PRINT_END`, `OnLoaded` нельзя считать документным ACK, финальным PDF, успешным CAS или изменённым WORM-оригиналом.

## 2. Изменения

### Нативная сторона — ограниченный сбор событий

Изменён `probes/eurooffice-desktop/cef_lab/cef_staged_open.cpp`. К существующему открытию **только синтетических тестовых файлов** добавлен `SyntheticEventObserver : CAscCefMenuEventListener`. Он получает только события **конкретного view ID** и сохраняет атомарные флаги: modified, on-save, cancelled, print-end; затем освобождает событие согласно upstream API. После shutdown слушатель отсоединяется.

В stdout после диагностического маркера `GD31J6_...` выводится ограниченный JSON receipt `schema=GD31J7_EVENTS_ONLY`. В него не включаются документ, путь к файлу, содержимое, учётные записи или принтер. Поля `documentVerified`, `saveVerified`, `pdfVerified`, `printApproved` **жёстко равны false**.

### .NET — управление жизненным циклом

- `EuroOfficeNativeDocumentWorkflow`: `Requested` → `ViewLoadedUnverified` → `Closed`, либо `Failed`, с терминальными состояниями. Принимает только заранее известные коды событий, идентификатор сеанса должен совпадать; чужие/поддельные отклоняются. После закрытия повторные команды запрещены.
- `EuroOfficeNativeReceipt`: строгий парсер JSON CEF receipt с полным списком полей, защитой от неизвестных/дублированных/некорректных значений. `ApplyNativeReceipt` переносит только диагностические сведения в правильный .NET-сеанс.
- `CreateRevisionIntent`: вызывает существующий безопасный GD-31J `CreateSaveRequest` только для явно подтверждённой редактируемой локальной **рабочей копии**, но **не сохраняет** документ и не изменяет WORM. Повторный запрос в этом сеансе запрещён.
- `CreatePdfIntent`: формирует единственный запрос `export-pdf` в локальный staging. `AuthorizeDirectPrint` всегда запрещён — печать остаётся за **собственным мастером ГЭТ**.
- `DocumentVerified`, `SaveCompleted`, `PdfReady`, `PrintApproved` всегда false: нет подтверждённого доверенного host/CAS/сервисного PDF.

### Проверка файлов-кандидатов без публикации

`EuroOfficeNativeArtifactCandidate` — отдельный независимый слой предварительного осмотра результатов:
- `InspectRevision`: только DOCX/XLSX/PPTX с совпадающим форматом исходного файла, отдельный локальный путь, максимум 128 MiB, отказ от links/UNC/traversal, проверки ZIP основных частей, повторяющихся имён, ограничений распаковки, XML без DTD/внешних отношений, запрет `vbaProject`, ActiveX, исполняемых ZIP частей.
- `InspectPdf`: отдельный файл `.pdf`, сигнатура `%PDF-` и завершение `%%EOF`, размер/путь/SHA-256. **Не заменяет** настоящую проверку PDFium/подписей/рендеринга и не печатает.
- Возвращаются формат, длина и SHA-256. Поля `AuthorizedForPublication=false`, `WormUpdated=false`. Значение SHA не устраняет TOCTOU и не подтверждает происхождение от доверенного процесса; нельзя использовать для автоматической фиксации в архиве ГЭТ.

Для рабочей системы последующий отдельный адаптер должен: получить выход с проверенного нативного процесса, валидировать полный OOXML/PDF, сопоставить документный ID/ACL/версию, атомарно разместить новый файл по CAS, зафиксировать аудит и WORM, и только затем выдать ACK. **Этот PR не содержит такой адаптер и не заменяет существующий GUP-2/Nextcloud/SЭД.**

## 3. Подготовлены тесты, запуск отложен

- `EuroOfficeNativeDocumentWorkflowTests`: неавторизованные события, чужой session, terminal-state, отмена, запрет прямой печати, pending requests.
- `EuroOfficeNativeReceiptTests`: запрет фальшивых success-флагов, дублированного JSON и невалидных типов.
- `EuroOfficeNativeArtifactCandidateTests`: OOXML/ZIP с макросом или повреждением, валидный PDF-кандидат, выход из staging, неперезапись source.
- `test_native_event_contract.py`: статическая сверка native SDK listener и фиксированного отчёта.
- Общий `scripts/office_acceptance.py` дополнен этапом `native-event-contract-tests`; `.NET Office.Tests` и так включены в общую локальную приёмку.

**Ни один тест не запускался в этом этапе.** Также не выполнены CMake/Qt/CEF/.NET build, реальный импорт XLSX/PPTX, закрытие нативного view и тесты печати — по ранее установленному решению.

## 4. Следующий кодовый этап GD-31J.8

Подготовить доверенный IPC-адаптер между изолированным native host и GET Desktop, включая lifecycle/timeout/cancel с отдельным журналом без личных данных; контракт `new-version` с immutable CAS в GUP-2/GET-system оставлять заглушкой до появления сервера. Подготовить подписанный хешированный временный PDF для передачи **собственному мастеру печати** и его error states; никакого прямого вызова printer APIs из EuroOffice. Не встраивать сырые Qt/CEF указатели в Avalonia.

**Блокеры остаются:** Qt/CEF может быть unsandboxed (зависит от `CEF_USE_SANDBOX` upstream), нет подтверждённого лицензирования/подписанных сборок, нет полноценных лабораторных тестов. Оставить Draft до завершения разработки и утверждённой локальной приёмки.
