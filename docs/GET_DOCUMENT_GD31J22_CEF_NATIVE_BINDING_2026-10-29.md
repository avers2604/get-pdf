# GD-31J.22 — реальная SDK-привязка нативного EuroOffice к синтетическому файлу

**Дата:** 29.10.2026. **База:** GD-31J.21 Draft PR #93. **Статус:** исходники подготовлены, но **сборка/Qt/CEF GUI/.NET/MSTest/Python unittest не запускались** по ранее утверждённому порядку (проверки позже). `main`, GitHub Actions, файловые ассоциации и рабочий лаунчер не изменены.

## Цель этапа

Пройти следующий доказуемый шаг после одних CEF `OnLoaded` и `OnPaint`: получить от **реального pinned SDK** информацию, к какому локальному документу привязана `CCefViewEditor`, и связать её с конкретными лабораторными `sessionId` / `tabId`. Это **не** подтверждает успешный импорт OOXML, отображение страниц, корректность Word/Excel/PowerPoint, сохранение или готовность редактора для пользователя.

Проверено по исходникам `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`: `ChromiumBasedEditors/lib/include/cefview.h` объявляет `CCefViewEditor::OpenLocalFile(...)`, `CCefViewEditor::GetFileFormat(...)` и `std::wstring CCefViewEditor::GetLocalFilePath()`. Это реальные API, а не выдуманный `OnDocumentReady` или `DocumentLoaded` callback.

## Реализация

1. **Отдельный CMake gate `GETDOC_GD31J22_ENABLE_BINDING_LAB=OFF`** по умолчанию; требует GD-31J.6, Linux, точного pinned source tree, заранее подготовленных локальных Qt/CEF библиотек и явного подтверждения изоляции. Не включается автоматически через OSR/input gate.
2. Нативный CLI `--open-staged-binding-lab <staging> <fixture> <sessionN> <tabN>` допускает только три синтетических имени из существующего manifest; нет общего выбора пути, URL или рабочих документов. Проверка manifest и файла уже выполняется `VerifySyntheticFixture`.
3. После настоящего `OpenLocalFile` таймер Qt-owner-thread опрашивает `editor->GetLocalFilePath()` **только** если `QCefView::OnLoaded` наблюдался, `QPointer` жив и всё ещё содержит тот же `CCefViewEditor`; сравниваются канонические пути с проверенным синтетическим fixture. Сам путь **не выводится**. Для этого отдельного режима запускается verified CEF message-loop pump (10 мс); без него CEF callback мог бы не наступить.
4. После 15-секундного эксперимента процесс выводит ровно одну ограниченную JSON-квитанцию `getdoc-native-binding-lab/1` с именем из белого списка, session/tab, наблюдаемыми `cefViewLoaded` / `editorPathMatched` / `bindingObserved`. Флаги `documentVerified`, `saveVerified`, `pdfVerified` и `printApproved` **жёстко равны false**, `laboratoryOnly=true`.
5. `EuroOfficeNativeBindingReceipt.Parse` строго проверяет структуру JSON, отсутствие повторных/чужих полей, типы и session/tab/fixture, запрещает ложные утверждения об импорте/сохранении/печати. `EuroOfficeNativeBindingProbeLab` повторно использует attested binary SHA и локальную политику от GD-31J.13, читает ровно одну строку с пределом 2048 символов, ограничивает время процесса и выполняет kill/reap при ошибке.
6. `MainWindow.StartNativeBindingLabAsync` доступен только внутреннему, явно одобренному лабораторному вызову; показывает сведения в существующей **диагностической** вкладке с указанием, что файл ещё не подтверждён. Обычный `OpenFromLaunch`/`PreviewFactory` не используют этот эксперимент. При закрытии вкладки процесс отменяется и очищается прежде UI.

## Подготовленные проверки

- `EuroOfficeNativeBindingReceiptTests.cs`: положительные и отрицательные receipts, чужая session/tab, другой fixture, дубликат поля, неизвестный ключ, ложный успех, невозможная комбинация CEF/path.
- `EuroOfficeNativeBindingProbeLabTests.cs`: ровно одна ограниченная stdout-квитанция, truncated/duplicate/oversized/посторонний stdout, default-deny при отсутствии разрешения.
- `test_native_binding_contract.py`: структурный source gate для pinned SDK-контракта, привязки CEF/Qt к синтетическому файлу, двойного platform gate, отсутствия производственных функций.
- `scripts/office_acceptance.py`: `cef-native-binding-source-contract-tests` перед restore и отдельный ручной выпускной gate `eurooffice-native-sdk-fixture-binding-and-import-e2e`.

**Все эти тесты написаны, но не запускались.** Неоднократная успешная SDK-привязка в будущем не означает E2E доказательство открытого и редактируемого файла.

## Границы безопасности

- У upstream CEF возможна работа без OS sandbox; выполнять только внутри отдельно утверждённой отключённой от сети disposable VM без монтирования служебных хранилищ.
- `GetLocalFilePath` может быть установлен ещё до полного импорта; сочетание с `OnLoaded` доказывает только привязку, не качество импорта или сохранения.
- Никаких `SetParent(HWND)`, `XReparentWindow`, произвольного CEF JavaScript, clipboard, WORM/CAS, SSO и обхода мастера печати ГЭТ.
- Windows 10/11 и Astra/X11 продолжают использовать прежний локальный LibreOffice/UNO fallback. EuroOffice Desktop не объявляется основным рабочим движком до настоящего безошибочного round-trip DOCX/XLSX/PPTX.
- Нативные отчёты не считаются подписанными или защищёнными от произвольного поддельного внешнего процесса; доверие зависит от аттестации бинарника, локального isolation policy и контрольного session/tab. Это не корпоративная аутентификация.

## Следующее обязательное функциональное препятствие

GD-31J.23: доказуемый **document import/ready** сигнал от реального upstream редакторного SDK (не подмена `OnLoaded`), затем изолированная проверка read-only/изменённой синтетической рабочей копии с валидным OOXML round-trip и `Save As` без записи в эталон. Если upstream такого API не предоставляет, нужно использовать и аттестовать официальный механизм состояния редактора/файлового конвертера — не писать фиктивный статус. Только после этого можно подходить к стандартному лаунчеру ГЭТ.

**Рекомендованный путь проверки**: локально `python -m unittest discover -s probes/eurooffice-desktop/cef_lab -p test_native_binding_contract.py`, затем офлайн CMake/.NET через штатный runner и ручная VM-проверка реального `GetLocalFilePath`; никаких автоматических CI-запусков.
