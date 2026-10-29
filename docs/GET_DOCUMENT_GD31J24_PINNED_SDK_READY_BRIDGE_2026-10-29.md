# GD-31J.24 — отдельный диагностический bridge ContentReady EuroOffice Desktop

**Дата:** 29.10.2026. **База:** GD-31J.23 / Draft PR #95. **Статус:** исходники и тесты написаны, но ни локальная сборка, ни тесты .NET/Python, ни CEF/Qt в виртуальной машине не запускались по принятому ранее порядку. Состояние **НЕ означает завершение нативного EuroOffice Desktop**.

## Найденный ограничитель (точный pinned upstream)

Для `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`:

- `ChromiumBasedEditors/lib/src/cefwrapper/client_renderer_wrapper.cpp`: JavaScript-метод `onDocumentContentReady` посылает IPC-сообщение `on_document_content_ready`.
- `ChromiumBasedEditors/lib/src/cefview.cpp`: оно попадает в обработчик браузерного процесса, который при определённых условиях запускает внутреннюю `LocalFile_Start()`, но **не выдаёт внешний callback** в `CAscCefMenuEventListener`.
- `ChromiumBasedEditors/lib/include/applicationmanager_events.h`: доступен механизм типизированных CEF menu events. **У upstream нет публичного типа «document import verified»**.
- Поэтому `QCefView::OnLoaded`, `GetLocalFilePath()`, `GetRecoveryDir()` и `Editor.bin` не подтверждают успешно загруженный, доступный для редактирования OOXML-документ. Даже исходный JS `onDocumentContentReady` — **внутренняя нотификация, не криптографически удостоверенный импорт**.

## Реализованные исходники

1. `probes/eurooffice-desktop/cef_lab/ready_bridge_overlay.py`: **отдельный ручной overlay только двух файлов** в явно обозначенной копии закреплённого SDK. Проверяет точные уникальные upstream-якоря, исходный Git HEAD, отсутствие чужих tracked-изменений, явный файл `.getdoc-gd31j24-disposable-lab` с точным содержимым `GETDOC_DISPOSABLE_OFFLINE_LAB_ONLY\n`. По умолчанию ничего не изменяет. `--apply` дополнительно требует `--ack-disposable-lab-copy`. В случае ошибки при применении восстанавливает обе исходные копии.
2. Overlay объявляет `ASC_MENU_EVENT_TYPE_GETDOC_DOCUMENT_CONTENT_READY` = 9102 и `GETDOC_GD31J24_READY_EVENT_BRIDGE=1` в **копии** `applicationmanager_events.h`; внутри ровно одного существующего CEF-обработчика `on_document_content_ready` создаётся одно типизированное событие и доставляется в `pListener->OnEvent` с ID отправившего редактора. Доступ к произвольному JavaScript, пользовательскому документу и печати не добавляется.
3. `CMakeLists.txt`: `GETDOC_GD31J24_ENABLE_READY_EVENT_LAB=OFF` по умолчанию. Требует GD-31J.23, старые все синтетические/закрытые офлайн-гейты, Linux/Wayland, точную двухфайловую overlay-проверку и **отдельные SHA-256 pins для ДВУХ собранных нативных библиотек** `GETDOC_GD31J24_APPROVED_ASC_DOCUMENTSCORE_SHA256` и `GETDOC_GD31J24_APPROVED_ASC_QT_SCORE_SHA256`. Обычный режим продолжает запрещать любой dirty SDK. Хеши исполняемого файла дополнительно проверяет прежний `EuroOfficeNativeLabHostPolicy`.
4. `cef_staged_open.cpp`: строго отдельная CLI-команда `--open-staged-ready-event-lab` для трёх допустимых синтетических OOXML fixtures. Компиляция с прежним SDK **преднамеренно останавливается** на `#error`. `SyntheticEventObserver` принимает новое событие только при **совпадении ID** фактического `CCefViewEditor`; отдельный атомарный флаг `onContentReady`. В тот же сеанс попадают `OnLoaded`, `GetLocalFilePath` и две стабильные SHA-256 выборки ограниченного `Editor.bin`. В stdout выводится только одна короткая session/tab/fixture квитанция, без абсолютных путей, JS-кода, паролей и данных документа.
5. `EuroOfficeNativeReadyCandidate` — строгий fail-closed C# parser: схема `getdoc-native-ready-candidate-lab/1`, точные поля/типы, отсутствие дубликатов, связь session/tab/fixture, bounded SHA конвертера. `ReadyAndConversionCorrelated` сообщает **лишь диагностическую корреляцию**; `DocumentVerified`, `EditReady`, `SaveCompleted`, `PdfReady`, `ApprovedForProduction` всегда `false`.
6. `EuroOfficeNativeReadyProbeLab` повторно использует прежний SHA-approved запуск и лимитированный stdout+kill/reap без сетевых запросов, shell, свободных аргументов, пользовательских путей. `MainWindow.StartNativeReadyEventLabAsync` и `NativeOfficeDiagnosticPane.SetNativeReadyCandidate` показывают итог только во **внутренней лабораторной вкладке**. Никаких новых команд рабочего launcher/PreviewFactory/печати/хранилища.

## Только для изолированной VM: порядок будущей проверки

- Копировать закреплённый SDK в **отдельный disposable офлайн checkout** вне рабочего source tree/установщика. Не монтировать рабочие документы/SMB/WORM/доменные ключи.
- Создать явный marker-файл `.getdoc-gd31j24-disposable-lab` с однострочным содержимым `GETDOC_DISPOSABLE_OFFLINE_LAB_ONLY`; запустить `ready_bridge_overlay.py --apply <SDK_COPY> --ack-disposable-lab-copy`, затем `--verify <SDK_COPY>`. **Не применять overlay к основному исходному дереву.**
- Пересобрать необходимые нативные зависимости **из согласованной изменённой копии** SDK; независимо закрепить SHA-256 каждого собранного бинарника и host executable после анализа соответствия и цепочки сборки. Без этих pins CMake не позволит включить GD-31J.24.
- Запустить кодовый offline runner локально, без NuGet-интернета и GitHub Actions. Затем провести лабораторную проверку трёх synthetic fixtures, негативных сценариев: новая/старая версия SDK, неверная подпись/хеш бинарника, чужой tab/session, CEF crash, неуспешная конвертация, дубликаты/подмена JSON, отсутствие full ready, отсутствие Wayland и отсутствующие два approved SHA.
- Проверить, что после нативного завершения `DocumentVerified/EditReady/SaveCompleted/PdfReady` всё ещё false, WORM/SMB оригиналы не затронуты; не использовать UI-симуляцию как подтверждение конвертации.

## Подготовленные тесты (не исполнялись)

- `test_ready_bridge_overlay.py`: точные уникальные upstream-якоря, запрет неполного/повторного overlay, clean pinned checkout + marker, отказ от чужих tracked-файлов и неподтверждённой записи.
- `test_native_ready_bridge_contract.py`: отдельный default-OFF gate, два native library SHA pins, защищённый callback от реального SDK, строгая session-bound квитанция и отсутствие производственных прав.
- `EuroOfficeNativeReadyCandidateTests.cs`: корреляция событий не выдаёт права, callback без конвертера, конвертер без callback, spoofed save, чужие ID, размер и digest, дубли JSON.
- `scripts/office_acceptance.py`: два новых offline source gates до restore и **ручной** `eurooffice-content-ready-patched-sdk-runtime-e2e`.

## Не устранено на этом этапе

Этот адаптер — **уведомление о готовности из renderer JS**, не полноценное формальное доказательство парсинга OOXML и не подтверждение записи. Сохранение EuroOffice → DOCX/XLSX/PPTX, реальное редактирование/IME/буфер, Windows/Astra X11, мастер печати, CAS/WORM и совместное редактирование не завершены. Текущий рабочий LibreOffice/UNO fallback оставлен неизменным. Следующий этап GD-31J.25: полноценный **synthetic-only save-as round-trip** в отдельный новый файл, с подтверждённой native callback и послесохранительной OOXML-проверкой; только после реальной автономной лаборатории и детального анализа SDK.

### Лицензирование

Заголовок pinned SDK содержит **GNU AGPL v3 и дополнительные условия**. Внутреннее/некоммерческое использование не отменяет автоматически лицензионных требований. До распространения/развёртывания изменённых бинарников нужна отдельная юридическая проверка зависимости, исходников и уведомлений. Эти изменения в GET repo сами по себе не включают никакого изменённого SDK-бинарника.

**Никакого merge в `main`, GitHub Actions или производственного включения на данном этапе.**
