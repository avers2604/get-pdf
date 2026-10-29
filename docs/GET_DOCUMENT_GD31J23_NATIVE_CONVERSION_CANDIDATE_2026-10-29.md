# GD-31J.23 — подтверждение кандидата конвертации EuroOffice Desktop (НЕ готовность редактора)

**Дата:** 29.10.2026. **Исходная база:** Draft PR #94 GD-31J.22. **Статус:** код + MSTest + Python source contracts написаны, но не запущены; нативная компиляция Qt/CEF, офлайн VM и физические проверки отложены. `main` и GitHub Actions не изменялись.

## Исходный SDK: что обнаружено по настоящему коду

Закреплённый `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`, без модификации upstream:

- `ChromiumBasedEditors/lib/src/cefwrapper/client_renderer_wrapper.cpp` принимает JS `onDocumentContentReady` и посылает CEF process message `on_document_content_ready`.
- В `ChromiumBasedEditors/lib/src/cefview.cpp` обработчик `on_document_content_ready` выполняет внутреннюю логику (при определённых условиях вызывает `LocalFile_Start()`) и возвращает `true`. **Он не выдаёт публичное событие CAscCefMenuEventListener о готовом офисном документе.**
- В том же исходнике `CCefView_Private::LocalFile_End()` после этапа внутренней конвертации отправляет `onlocaldocument_loadend` только в рендерер, а `CCefViewEditor::GetRecoveryDir()` возвращает папку внутренней рабочей копии. `GetLocalFilePath()` возвращает источник, только если установлен внутренний признак saved; этот путь сам по себе не означает успешный импорт.
- Для локального файла SDK готовит внутреннюю папку в `m_oSettings.recover_path`; программа конвертации использует `Editor.bin`. Это **собственный бинарный формат движка**, не OOXML, не документ для последующего копирования и не подписанная квитанция.

Из этого следует: нельзя безопасно и честно выставлять `DocumentVerified=true` лишь по `OnLoaded`, `GetLocalFilePath`, внутреннему `onDocumentContentReady` или наличию `Editor.bin`. Для доказуемого `ready` нужен утверждённый адаптер/SDK callback с согласованным lifecycle, проверка состояния документа и последующий полноценный round-trip.

## Новый диагностический функционал GD-31J.23

1. Новый CMake-флаг `GETDOC_GD31J23_ENABLE_CONVERSION_EVIDENCE_LAB=OFF` требует GD-31J.22, Linux/Wayland, pinned SDK, локальную офлайн-сборку и отдельное разрешение на лабораторию.
2. CLI `--open-staged-conversion-evidence-lab` принимает только синтетические имена `gd31j6_writer.docx`, `gd31j6_calc.xlsx`, `gd31j6_impress.pptx` из прежней локальной fixture+manifest; работа с корпоративными/сетевыми документами отсутствует.
3. После `OpenLocalFile`, CEF `OnLoaded` и проверки соответствия `GetLocalFilePath` родному fixture вызывается `GetRecoveryDir`. Допускается только **одна непосредственная дочерняя папка** внутри `<private QTemporaryDir>/recovery`; символьные ссылки и выход за границы отбрасываются.
4. В разрешённой папке только для чтения открывается `Editor.bin` размером **от 1 байта до 8 МиБ**, считается SHA-256 ограниченным буфером. Для кандидата требуются два последовательных одинаковых наблюдения. На CEF/Qt UI-thread чтение ограничено намеренно небольшим объёмом; большой или меняющийся файл даёт отрицательный результат, а не ложное подтверждение.
5. В stdout выдаётся **одна** квитанция с session/tab, именем из белого списка, `cefViewLoaded`, `editorPathMatched`, `conversionArtifactObserved`, размером и SHA-256 внутреннего бинарного артефакта **без передачи абсолютных путей, документа или его содержимого**. Во всех ответах `documentVerified/saveVerified/pdfVerified/printApproved=false`, `laboratoryOnly=true`.
6. `EuroOfficeNativeConversionCandidate.Parse` отвергает чужой сеанс, неизвестные и дублирующиеся поля, несовпадающий fixture, неправильный SHA, невозможную комбинацию view/path/artifact, размер больше 8 МиБ, отсутствие обязательных полей или заявление о правах.
7. `EuroOfficeNativeConversionProbeLab` использует прежний SHA-approved native host и **общий** ограниченный жизненный цикл процесса/приём stdout от GD-31J.22, без новой возможности запускать произвольные команды. В `MainWindow` результат доступен только из отдельного внутреннего диагностического режима, не через рабочий лаунчер.
8. UI показывает именно **«кандидат конвертации»**, не «Документ открыт», не «Готов к сохранению», не «Проверен».

## Сценарии тестирования (подготовлены, НЕ запущены)

- `EuroOfficeNativeConversionCandidateTests.cs`: корректный кандидат остаётся полностью неавторизованным; чужая session/tab; другой fixture; повторные/чужие поля; превышение лимита; короткий или неверный SHA; ложный успех и внутренне противоречивые результаты.
- `test_native_conversion_evidence_contract.py`: независимый default-OFF CMake gate, белый список, `GetRecoveryDir()`, `Editor.bin`, confined staging, SHA-256, два наблюдения и отсутствие готовности редактора.
- `scripts/office_acceptance.py`: обязательный локальный source gate `cef-native-conversion-evidence-source-tests` и отдельный ручной release gate `eurooffice-native-converter-private-artifact-candidate-e2e`. В `scripts/tests/test_office_acceptance.py` закреплено наличие обоих.
- Для лаборатории: три fixture в автономной VM, поддельный исполняемый файл (неверный SHA), изменённый manifest, симлинк в recovery, пустой/растущий/неправильный `Editor.bin`, отсутствие Wayland, принудительное завершение, stale session/tab, корректный kill/reap, отсутствие пользовательских документов в process output.

## Чего всё ещё нет

- **Реального публичного события import-ready** — внутренняя CEF `on_document_content_ready` не транслируется наружу закреплённым SDK.
- Редактирования и обратного OOXML экспорта через native EuroOffice (ни одной разрешённой записи в настоящие файлы), совместимости Microsoft Office, гарантированного полного синхронного сохранения и файлового CAS/WORM.
- Подтверждённой функциональной работы на Windows/Astra Linux X11, полноценного IME/preedit, системного буфера обмена и физической печати.
- Реальной сборки и прогонов тестов, которые выполняются отдельно после завершения подготовки кодовых блоков.

**GD-31J.24:** определить поддерживаемый upstream lifecycle/event-adapter и способ read-only проверки, что документ действительно импортирован редактором; если нужен форк SDK, он обязан получить новый, явно утверждённый pin + лицензионную и безопасностную проверку, а не обход существующего запрета на dirty/upstream checkout. До такого подтверждения не подключать нативный редактор к пользовательским DOCX/XLSX/PPTX и WORM.
