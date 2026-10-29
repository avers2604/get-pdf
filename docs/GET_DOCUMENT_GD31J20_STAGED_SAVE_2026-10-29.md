# GD-31J.20 — первый локальный Save As для EuroOffice Desktop

**29.10.2026 — Draft над GD-31J.19 / PR #89.** Только исходники и отложенные тесты; никаких сборок, Qt/CEF запуска, корпоративных записей, online CI или изменения `main`.

## Назначение

Сохранение из EuroOffice Desktop всё ещё нельзя назвать работающим: pinned upstream SDK `CCefViewEditor` предоставляет `OpenLocalFile`, но просмотренный заголовок `cefview.h` **не содержит проверенного метода Save As**, а `on-save` является лишь событием. Продуктовая интеграция отсутствует.

В этом этапе реализована следующая независимая **локальная граница**: если когда-либо отдельно проверенный CEF-хост создаст временный OOXML-файл, GET сможет безопасно проверить его, сравнить с сохранённым снимком исходной рабочей копии и опубликовать **другой** файл в локальном staging **без перезаписи исходника**. Это ещё не сохранение из редактора и **не запись WORM/CAS**.

## Реализация

- `EuroOfficeStagedSaveTransaction.Prepare`: необходимы явный `labApproved=true`, непустые session/tab GUID и три **разных** локальных пути (source, candidate, destination) внутри существующего staging. Используются ранее созданные `CreateOpenRequest` / `CreateSaveRequest` с запретом network/share, path traversal, symlink/reparse и существующей цели. Фиксируется SHA-256 исходника.
- `PublishAsync`: точная сессия/вкладка, **ровно одна попытка**; проверка source SHA-256; осмотр `EuroOfficeNativeArtifactCandidate.InspectRevision` (ZIP entries, macro/ActiveX/EXE запреты, XML DTD/внешние связи); копирование не более 128 MiB в созданный исключительно этой транзакцией `.getdoc-save-<guid>.docx|xlsx|pptx` с проверкой digest и принудительной файловой синхронизацией; повторная проверка скопированного OOXML и source; `File.Move(..., overwrite:false)`; возврат digest/формата/пути.
- `finally` удаляет только временный файл конкретной попытки; source, candidate и существующий destination не удаляются.
- Результат **всегда** содержит `WormCommitted=false` и `AuthorizedForCorporatePublication=false`.
- `EuroOfficeNativeDocumentWorkflow.PrepareLocalStagedSave` связывает новую операцию с прежним однократным intention-gate и не изменяет `SaveCompleted=false` / `DocumentVerified=false`.

## Регрессионные сценарии (написаны, не запускались)

1. Корректная синтетическая DOCX создаёт отдельную копию, исходник остаётся побайтно неизменным.
2. Изменение исходника после `Prepare` запрещает публикацию.
3. Внедрение `vbaProject.bin` в кандидат блокирует операцию.
4. Гонка: другой процесс занял destination после `Prepare` — никакой перезаписи.
5. Подмена session/tab отклоняется, не расходуя правильный билет.
6. Повтор той же транзакции, одинаковые candidate/destination, запрет `labApproved`, отмена по токену.
7. Workflow не выдаёт лабораторный staging за доказанный native save или разрешение WORM.

## Границы гарантии

1. На небезопасной ОС/общем каталоге между проверками путей возможен TOCTOU: код **не создаёт доверенную песочницу ОС и не должен работать с документами ГЭТ** до отдельной локальной верификации изоляции и прав каталога.
2. `Flush(true)` и локальный move не обеспечивают серверный CAS, синхронизацию каталога, юридическую подпись, подтверждение CEF Save и надёжность на SMB. При аварийном завершении `*.tmp.docx` может сохраниться; удалять чужие файлы автоматически не разрешено.
3. Локальный результат не прикрепляется к GUP-2, Nextcloud или WORM, не запускает печать и не делает `DocumentVerified/SaveCompleted` истинными. Корпоративный writeback должен подключаться отдельным контролируемым сервисом после тестирования.

## Следующий реальный этап GD-31J.21

Подтвердить механизм upstream native Save/Export через pinned DesktopEditors application manager + CEF events без прямого выполнения произвольного JavaScript, разработать **изолированный CEF save-to-staging адаптер** с точным session/tab и сообщением об окончании процесса, затем использовать эту транзакцию как конечную локальную проверку. Для Windows/X11 до официального рендерингового API сохраняется LibreOffice/UNO fallback.

Запускать проверки **позже, локально**:
```bash
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore
```
Отдельно выполнить `scripts/office_acceptance.py` согласно закреплённому offline-feed; без санкции на реальные документы и корпоративные записи.
