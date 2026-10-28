# GD-31J.17 — локальная IME-композиция, ограниченный финальный CEF commit и запрет системного clipboard

**29.10.2026. Статус: только исходники и подготовленные тесты, никаких успешных лабораторных прогонов.** База: GD-31J.16 PR #86. Только отдельная VM Linux/Wayland, синтетические OOXML из GD-31J.6; без записи рабочих файлов, GUP-2/CAS/WORM, сетевых сервисов и печати. `main` и GitHub Actions не менять.

## Подтверждённый upstream

Закреплённый `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51` содержит `CCefView::SendKeyEvent(type,key,modifiers,character)` и Wayland `QCefView::inputMethodEvent`, который отправляет **только уже подтверждённые символы** через `SendKeyEvent(0/3/2, 0,0,...)`. Отдельного `CCefView::ImeSetComposition/ImeCommitText/ImeCancelComposition` в доступном публичном wrapper нет. Поэтому **настоящую CEF IME-preedit/candidate UI нельзя считать готовой**, и GD-31J.17 не выдаёт её за реализованную.

## Сделано

1. `EuroOfficeImeLabComposition.cs`: локальный жизненный цикл `Begin/Update/CommitAsync/Cancel/Dispose` для предварительного текста. Одна композиция за раз, максимум 16 BMP кодовых единиц, 32 обновления. Форматные, управляющие, bidi, surrogate-символы отклоняются. Черновик хранится только в массиве памяти **владеющей диагностической вкладки**, не передаётся через IPC; очищается на отмене, невалидном обновлении, коммите (включая ошибку), закрытии или переключении вкладки. Нельзя утверждать, что строка, переданная внешним фреймворком, целиком удалена из управляемой памяти: очистка охватывает только собственный буфер.
2. `EuroOfficeOsrLabInputChannel.SendImeCommittedTextAsync`: отдельное `imeCommitLabApproved=false`, фиксированные session/tab, обязательные ранее разрешённые keyboard/click/focus и отсутствие удержания мыши. IPC `command=ime-commit` содержит строгое **числовое** `codePoints: [int,...]` из 1–16 BMP-символов; нет произвольной текстовой команды, пути, JS, файла, HTML или preedit. Максимум 64 подтверждённых символа за весь лабораторный сеанс. При ошибке частичной записи канал отзывается и его процесс отменяется.
3. `osr_input.cpp`: дополнительная независимая native capability `m_imeCommitApproved`, строгая JSON-схема, проверка всей последовательности **до первого** CEF вызова, те же BMP/format-ограничения и суммарный лимит 64. Затем последовательно отправляются проверенные `SendKeyEvent(0/3/2)` с `VK=0`. Это имитация committed-символов на базе существующего SDK, **не** нативный CEF ImeSetComposition и не подтверждение успешного редактирования.
4. Новый отдельный `--open-staged-wayland-ime-commit-lab` + `GETDOC_GD31J17_ENABLE_IME_COMMIT_LAB=OFF`, доступен лишь вместе с GD31J16 и всеми предыдущими CMake opt-in. SHA-pinned `EuroOfficeOsrStreamProcessLab.CreateImeCommitStartInfo` и `MainWindow.StartNativeCefOsrLabStreamAsync(...,imeCommitLabApproved=false)` не включаются штатным launcher, нет сетевого доступа и документов из рабочего окружения.
5. `NativeOfficeDiagnosticPane` содержит отдельное **internal**, не подключённое к пользовательскому `TextInput`, API `BeginImeLabComposition/UpdateImeLabComposition/CommitImeLabCompositionAsync/CancelImeLabComposition`. Это точка для будущего проверенного `ITextInputMethodClient` или соответствующего адаптера ОС. Нельзя считать событие Avalonia `TextInput` с несколькими символами доказанным IME commit: оно может быть вставкой из clipboard или автозаменой, поэтому новый путь **не вызывается автоматически**. При переключении вкладки локальная композиция аннулируется.
6. `EuroOfficeLabClipboardGate`: `RequireOsReadApproval/RequireOsWriteApproval/RequireHtmlOrRichTextApproval` безусловно отклоняют действия. Системный clipboard **не читается, не изменяется и не отправляется**, так же не допускаются RTF/HTML/изображения/файлы. Это политика запрета, **не готовый модуль обмена**.

## Тесты написаны, НО не запускались

- `EuroOfficeImeLabCompositionTests`: preedit не выходит на провод до commit, кириллица, session mismatch, cancel/clear, ограничения Unicode и 16/64 символов, отдельный допуск к CEF, clipboard denial.
- `EuroOfficeOsrStreamProcessPolicyTests`: отдельная CLI `--open-staged-wayland-ime-commit-lab`, прежняя keyboard CLI сохраняется.
- `test_osr_ime_commit_contract.py`: CMake OFF, native validations **до** SendKeyEvent, запрет системного clipboard и отдельный lifecycle композиции.
- `scripts/office_acceptance.py`: `cef-osr-ime-commit-source-contract-tests` перед локальным offline restore.

**Ни Python unittest, ни .NET/MSTest, ни CMake/Qt/CEF, ни Windows/Astra GUI, ни проверка IME или буфера обмена не запускались.** Статическая сверка файла и будущие тестовые сценарии не означают успешной компиляции, а разрешённый native CEF всё ещё может быть без sandbox. Нужна отдельная одобренная VM без рабочих документов.

## Платформы и дальнейшие требования

Текущий upstream только на Linux/Wayland использует CEF OSR; Windows/X11 — `SetAsChild` и остаются на LibreOffice/UNO. **Не** делать `HWND/XID` reparenting без поддерживаемого нативного API. Для GD-31J.18 требуется настоящий OS-driven IME adapter с событиями composition start/update/cancel/commit, корректным candidate/caret rectangle, обработкой многобайтовых символов и проверкой input focus/DPI; Windows и X11 требуют отдельно доказанного OSR варианта или совместимого редакторного fallback. Clipboard может быть включён только как новый **отдельно утверждённый** типизированный обмен с user gesture, DLP и лимитами, не через IME-поток. GUP-2/WORM/CAS, Nextcloud и собственный мастер печати не активированы.
