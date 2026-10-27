# GD-13 — единый рабочий стол: GUP-2 Office return + PDF navigation

Дата: 25.10.2026. Ветка: `feature/get-document-gd13-integrated-office-return-pdf`. **Draft / не релиз.**

## Происхождение кода и объединение

- База — GD-12 / PR #33, содержащая GD-10/31, защищённый WOPI host, OOXML preflight и встроенный WebView. ГЭТ PDF WPF и «ГЭТ Сканирование» не удаляются.
- Пять исходных файлов GD-11 из PR #32 (`OfficeReturnPolicy`, `OfficeReturnClient`, `OfficeReturnPane`, MSTest, документация) перенесены **без собственной второй реализации upload endpoint**.
- `MainWindow` изменён точечно, сохранил GD-12 `CollaborationEditorPane` и добавил вкладку «Возврат GUP-2» только если `OfficeReturnPolicy.LoadLocal().Enabled`.
- При закрытии возврата (× / Ctrl+W / закрытие главного окна) очищается введённый одноразовый код, отменяется активная клиентская отправка и освобождаются ресурсы. Отмена на клиенте **не является доказательством отката уже принятой сервером операции**: проверяйте карточку GUP-2.
- Возврат — **только явный пользовательский upload уже сохранённой DOCX/XLSX рабочей копии** по одноразовому коду. Не автоматический Save, не полноценное WOPI coauthoring. Требует GUP-2 PR #162 и административную политику, оба отключены по умолчанию. Корпоративная PKI/AV/ACL и тесты ещё не приняты.

## Реальные изменения PDF-просмотра

- Встроенные кнопки первой/предыдущей/следующей/последней страницы в существующей PDF-вкладке, поле номера, «Перейти», Enter и Ctrl+G.
- `PdfPageNavigation` в Core проверяет строго десятичные номера 1…N, неизвестное N=0, границы в 1000 страниц, обрезает относительные переходы на краях файла. Некорректный номер **не передаётся PDFium**.
- Добавлены MSTest: первая/последняя, недопустимые строки, выход за диапазон, проверка переполнения/смещения.
- PDF preview остаётся **растровым отображением по одной странице**. Это **не** полноценный PDF editor: перенос поиска, выделения, печати, форм, подписей, редактирования страниц и OCR из WPF/готовых движков — отдельный P0 release gate.

## Проверки

- Статически проверено, что новая ветка наследует PR #33 и добавляет только GD-11 интеграцию и PDF навигацию.
- Исходники MSTest подготовлены, но не запущены: на исполнителе нет .NET 10 SDK, а пакетный registry GitHub недоступен для `dotnet restore`.
- Перед merge:
  ```sh
  dotnet restore GetDocument.slnx
  dotnet build GetDocument.slnx -c Release --no-restore
  dotnet test tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj -c Release --no-restore
  dotnet test tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj -c Release --no-restore
  dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
  ```
- Проверить вручную Ctrl+G/Enter/границы 1/1000, закрытие PDF во время рендера, окно возврата/снятие флага, отмену долгого upload, права GUP-2, запрет повторного одноразового кода, WORM, Windows/Linux UI, WebView, сетевые принтеры.
- GitHub Actions не включены. В `main` не сливать, пока не проверены все gate из общего плана `GET_DOCUMENT_TRANSITION_RELEASE_GATE_2026-10-24.md`.

## Очередность завершения проекта

1. Сборка и исправление реально обнаруженных ошибок компиляции/интеграционных тестов GD-13.
2. Кроссплатформенный PDF-паритет (печать/поиск/выделение/формы/аннотации) **в той же вкладке**, без запуска отдельного офисного окна.
3. Полноценный Office editing и roundtrip эталонных MS Office файлов, проверка контрактов совместного редактора с корпоративной лицензией и фирменным UI.
4. Безопасные native launch / trusted GUP-2 WOPI store / пользователи AD/SSO / CAS/audit, реальные 2/5/10 одновременных редакторов.
5. Финальная приёмка локальных офлайн установщиков с rollback и только после — merge/release.
