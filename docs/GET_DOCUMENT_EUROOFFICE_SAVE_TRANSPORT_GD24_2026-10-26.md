# GD-24 — серверная отправка изменённого Office-файла в GUP-2

26.10.2026. **Draft; реальное подключение к Euro-Office callback по умолчанию запрещено.**

Связанный сервер GUP-2: [GD-24 branch](https://github.com/avers2604/Gup-2/tree/feature/get-document-eurooffice-save-transport-gd24), stacked на GD-23 PR #179.

## Новый C# компонент

`GetDocument.Collaboration/Gup2EuroOfficeSaveTransportClient.cs`:
- `CreateSignedRequest` принимает исключительно заранее подтверждённый корпоративным broker **server-only** 256-битный save-grant (64 lowercase hex), GUP-2 actor UUID, точный `document.key` редактируемой версии, SHA подписанного Euro-Office callback и уже проверенные байты DOCX/XLSX (максимум 64 МиБ). Не принимает произвольный URL к файлу Document Server и не загружает по callback URL.
- Проверяет доверенный HTTPS origin GUP-2 с фиксированным путём `/documents/`. Единственная конечная точка: `POST /documents/getdocument/eurooffice/save/`.
- HMAC-SHA256 вычисляется отдельным корпоративным ключом `GETDOCUMENT_EUROOFFICE_SAVE_KEY` (не outbox JWT, не read grant HMAC, не WOPI). Подписанные поля: `POST\ngetdocument/eurooffice/save\n{grant}\n{actor}\n{UTC epoch seconds}\n{SHA256 of bytes}\n{callback fingerprint}\n{byte length}`. Протокол строго LF, без завершающего перевода строки.
- Тело — только `application/octet-stream`, заголовки: `X-Get-Eurooffice-Save-Code`, `X-Get-Eurooffice-User`, `X-Get-Eurooffice-Timestamp`, `X-Get-Eurooffice-Document-Key`, `X-Get-Eurooffice-File-Sha256`, `X-Get-Eurooffice-Callback-Fingerprint`, `X-Get-Eurooffice-Signature`. Ни grant, ни секрет, ни приватный S3 URL не находятся в URL.
- `SubmitAsync` использует HTTPS с системной проверкой сертификатов, без браузерных cookie, без системных учётных данных, без automatic redirects, с лимитом ответа 4096 байт. При ошибке GUP-2 выдаёт отказ, не завершает callback как сохранённый.
- `ParseReceipt` строго принимает **201** + `working_copy_saved` либо **200** + `already_committed`, позитивную целую редакцию, **совпадающий SHA-256** с отправленными байтами; лишние поля, внешние URL, 409 и неправильный JSON отвергаются.

`Gup2EuroOfficeSaveTransportTests` содержит тестовые векторы канонического HMAC, запрет посторонних origin и слабых ключей, подтверждение CAS версии/SHA и отказы неправильным HTTP кодам/полям.

## Граница ответственности

C# helper **не даёт сам по себе полномочий**: произвольный код приложения не должен конструировать доверенный GUP-2 save-grant. Он должен приходить только из корпоративного session broker, выпущенный по SSO/ACL и привязанный к конкретной рабочей версии. GUP-2 хранит лишь SHA-256 save-code и **имеет отдельный off-by-default серверный маршрут**; адреса доверенных TCP peer должны быть зафиксированы, на reverse proxy нужен корпоративный mTLS.

Реальный `GetDocument.EuroOfficeHost` продолжает использовать `DenyAllEuroOfficeCallbackSessionStore` / `DenyAllEuroOfficeCallbackRevisionSink`. Подключить его безопасно можно лишь после полного завершения:
1. Автоматизированного GUP-2 issuer при реальном SSO-доступе к co-edit session, секретной передачи save-code в ASP.NET broker (не в JS).
2. Тестированной на конкретном Euro-Office релизе outbox JWT схемы/заголовка, проверки callback eventId/fingerprint/idempotency.
3. SSRF-safe HTTPS загрузки результата от доверенного Document Server: socket-level IP allowlist, корпоративный CA, отсутствие redirect и DNS rebinding, ограничение size и повторная SHA/OOXML проверка.
4. Гарантированного полного CAS/revision receipt и только после этого ответа `{"error":0}` редактору.
5. Локальных E2E Windows/Astra Linux с 2/5/10 редакторами, долгой сессией, параллельными force-save/status2, отзывом прав, отказом S3/AV/DB и восстановлением сети.

**Ни один production-флаг не включён, WORM и мастер печати не изменены.** Исходники написаны, но .NET SDK и реальный Euro-Office на доступной машине отсутствуют; `dotnet build`/`dotnet test` не выполнялись.

Для локальной проверки:

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

`document.key` должен вычисляться согласованно с текущей GUP-2 рабочей редакцией по схеме `SHA256(UTF8(document UUID + LF + edit_version))`. Иной `document.key` отклоняется даже с верной межсерверной подписью; код выданного grant остаётся только в доверенной памяти сервера.
