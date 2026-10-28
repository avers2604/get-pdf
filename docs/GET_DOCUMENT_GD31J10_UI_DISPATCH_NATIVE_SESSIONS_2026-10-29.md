# GD-31J.10 — безопасная доставка событий Qt IPC в Avalonia и жизненный цикл диагностической вкладки

**29.10.2026. Статус: код подготовлен, сборка и проверки отложены по решению пользователя.** Этот этап продолжает GD-31J.9 PR №78. Не менять `main`, не включать GitHub Actions, не сливать stacked Draft PR без итоговой локальной лаборатории.

## Цель и граница

В GD-31J.8 настоящий отдельный **Qt-only диагностический** процесс умеет `hello / attach-tab / resize / close`, но **не содержит** настоящего CEF, `OpenLocalFile`, PDF-экспорта, native window embedding или операций с файлами. В GD-31J.9 `MainWindow` получил **пассивную диагностическую** вкладку, но раньше она не управляла дочерним процессом. GD-31J.10 соединяет эти **две конкретные части**, не выдавая транспортный ACK за загруженный DOCX/XLSX/PPTX.

## Архитектура

```text
    GET MainWindow [один существующий Avalonia TabControl]
              ↑ UI Dispatcher
    AvaloniaNativeSessionView  (только диагностические UI callbacks)
              ↑  IEuroOfficeNativeSessionView
    EuroOfficeNativeSessionCoordinator
              ↓ IEuroOfficeNativeSessionTransport
    EuroOfficeNativeIpcProcessSession  (approved SHA-256, Process)
              ⇅ stdin/stdout JSONL
    Qt-only --ipc-probe [НЕ CEF, НЕ редактор, НЕТ доступа к файлам]
```

- `EuroOfficeNativeSessionCoordinator.cs`: отдельный слой без ссылок на Avalonia. Внедряется абстракция `IEuroOfficeNativeSessionView` и `IEuroOfficeNativeSessionTransport`. Начало только с явным `labApproved=true`; перед UI-показом требуется успешный `AttachAsync` диагностического Qt-процесса. Каждый идентификатор `tabId/sessionId` проверяется при resize, close, сообщениях о потере процесса. Повторное использование старого сеанса запрещается.
- `WatchAsync` ожидает **реального завершения OS-процесса** через `WaitForExitAsync`. Если хост завершился вне обычного закрытия или IPC потерян, вкладка остаётся видимой с ошибкой. НЕТ автоматического перезапуска, выдачи «открыто/сохранено» или передачи данных в GUP-2. Поздний сигнал от уже закрытого сеанса игнорируется.
- `CloseAsync` проверяет `HasUnverifiedChangesAsync`; при наличии уведомления о правках требуется явный `confirmedDiscard`. **Сначала** `CloseAsync` нативного транспорта, его `DisposeAsync` и завершение дочернего процесса, **после этого** `RemoveAsync` UI. Если процесс не закрылся или подтверждение не получено, нельзя тихо удалить вкладку и заявить успех. Принудительный cleanup не подтверждает сохранение редакции.
- `EuroOfficeNativeIpcProcessSession` теперь реализует `IEuroOfficeNativeSessionTransport`, предоставляет `WaitForExitAsync` и сохраняет политику локального закреплённого бинарника, bounded timeout и kill/reap из GD-31J.8.
- `AvaloniaNativeSessionView.cs`: **все** изменения `MainWindow` доставляются через `Dispatcher.UIThread.CheckAccess/Post` с `TaskCompletionSource`, отменой и пробросом исключений. Нельзя менять Avalonia контролы из process monitoring thread.
- `MainWindow` добавляет только **internal** `StartNativeLabSessionAsync` с явной проверкой `labApproved` и `EuroOfficeNativeLabHostPolicy`. Обычный пользовательский launcher, создание документа, PDF, LibreOffice, печатный мастер и установщик **не вызывают** этот метод. Подключённый диагностический Qt-сеанс ведёт статус в `NativeOfficeDiagnosticPane`: «Qt IPC подключён, CEF/документ не подтверждены» или «Qt IPC потерян».
- Закрытие диагностической вкладки через кнопку/Ctrl+W по-прежнему спрашивает о возможной потере неподтверждённых правок. При наличии координатора оно **сначала** инициирует закрытие процесса; обычная запись в `DocumentWorkspace` не выполняется. `Closed` приложения инициирует асинхронное завершение диагностических процессов (это не замена финальной проверки отсутствия зависших subprocess).
- `NativeOfficeDiagnosticPane` отличает транспортную связь от загрузки самого офисного документа. UI не показывает фиктивный успех и не активирует печать. Статусная вкладка по-прежнему использует **один** `TabControl` ГЭТ.

## Системные границы безопасности

Этот API **не** подключает в production EuroOffice DesktopEditors, не создаёт Qt view в Avalonia, не даёт доступ к документам, не открывает/сохраняет DOCX/XLSX/PPTX, не печатает и не обновляет WORM/CAS. `AuthorizeDocumentOperations()` безусловно запрещён. Если будет добавлен отдельно изолированный CEF-процесс, его документационное/сетевое подключение и OS sandbox должны проходить независимую проверку. В закреплённых исходниках EuroOffice CEF по умолчанию может иметь `no_sandbox=true`, если не выставлено `CEF_USE_SANDBOX`.

Утрата связи с Qt-only process отмечается как сбой **IPC**. Она не доказывает нарушение целостности служебного документа и не разрешает автоматическое применение подготовленного `EuroOfficeNativeReceipt`. Для будущего настоящего CEF события сохранения через IPC нужны аутентификация, проверка документной ревизии, file provenance, отдельный CAS и аудит.

## Тесты написаны, не выполнены

- `EuroOfficeNativeSessionCoordinatorTests`: корректная последовательность attach/resize/close, отказ без `labApproved`, потеря процесса, stale session, две вкладки, запрет удаления при неподтверждённых правках, неавтоматический рестарт и disposal при ошибке.
- `scripts/tests/test_native_session_dispatch_contract.py`: статические ограничения на UI dispatcher, закрытие процесса до удаления вкладки, отсутствие офисных команд, недоступность production автозапуска.
- `scripts/office_acceptance.py`: новый шаг `native-session-dispatch-contract-tests` до `dotnet restore`, плюс существующие тесты Office и Qt, всё по-прежнему **только локально**.

Согласно решению пользователя **не запускались** Python unittest, MSTest, .NET build, Qt6/CMake, Windows/Astra E2E и настоящие два монитора. Ни один запуск не отмечен как PASS. Прежние Draft PR не сливались.

## Следующие задачи

**GD-31J.11:** подготовка безопасного отображения нативной поверхности CEF во вкладке ГЭТ с кроссплатформенной стратегией Windows/Astra. Потребуются отдельный подтверждённый API для window/OSR, DPI/focus, lifecycle и реальный isolated native host. Нельзя передавать сырой `QWidget*` между процессами, привязывать произвольный HWND к вкладке или включать CEF без OS containment. Для операционной работы до реальной проверки сохраняется действующий LibreOffice/UNO; GUP-2/CAS/WORM и экспорт PDF в фирменный мастер печати остаются отдельными закрытыми адаптерами.
