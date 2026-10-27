# GD-25.1–25.2 — SSO-связка запуска Euro-Office ↔ GUP-2

Дата: 27.10.2026. Draft, реальный совместный редактор **не активирован**.

Серверный код: GUP-2 [ветка GD-25](https://github.com/avers2604/Gup-2/tree/feature/get-document-eurooffice-sso-session-gd25), поверх GD-24 PR #182.

## Изменения в C#

\`src/GetDocument.Collaboration/Gup2EuroOfficeEditorSessionClient.cs\`:
- \`CreateSignedRequest\` формирует пустой, не содержащий cookie/URL-секретов, межсерверный \`POST /documents/getdocument/eurooffice/session/redeem/\` к pinned HTTPS GUP-2. HMAC-SHA256 назначение **отдельно** от read/metadata и save grant.
- Canonical message = \`POST\ngetdocument/eurooffice/session/redeem\n{launchId}\n{immutableUserUuid}\n{unixUTCseconds}\` (LF-only, без конечной новой строки).
- Идентификатор сотрудника берётся **только** из проверенного корпоративного \`ClaimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)\`, который должен быть каноническим UUID GUP-2. Не доверять произвольному HTTP-заголовку имени, логину ОС или query-параметру.
- \`RedeemAsync\`: только backend HTTP; без cookie/default Windows credentials, запрет redirects, таймаут 20 секунд, ограниченный ответ 4096 байт.
- \`ParseReceipt\` разрешает **ровно 10 документированных полей**. Проверяются SSO UUID, document UUID, auth_version, edit_version, исходный/рабочий SHA, DOCX/XLSX, 64-hex server save grant и сам \`document.key = SHA256(UTF8(document UUID + LF + edit_version))\`.
- Чувствительный объект результата специально **не является record с автогенерируемым ToString**: его \`ToString()\` не раскрывает save grant. Запрещено сериализовать этот объект в WebView или Docs API config.

Тестовый контракт: \`tests/GetDocument.Collaboration.Tests/Gup2EuroOfficeEditorSessionTests.cs\` — точное совпадение HMAC Python↔C#, SSO mismatch, неизвестные JSON поля, noncanonical secret, stale revision/documentKey, низкоэнтропийные ключи, insecure origin.

## Важная граница

Это **не поставщик корпоративной SSO аутентификации** и **не готовый \`ITrustedEuroOfficeLaunchStore\`**. Пока ASP.NET \`GetDocument.EuroOfficeHost/Program.cs\` не содержит валидированного корпоративного SSO middleware, а \`ITrustedEuroOfficeLaunchStore\` и callback revision sink работают по DenyAll. Поэтому никакая правка/скачивание корпоративной копии из WebView не включена.

Далее GD-25.3: корпоративный adapter SSO claims/certificate, хранилище и lifecycle доверенной сессии \`editor + content + callback\`; связывание server-only save grant без выдачи его браузеру; HTTPS gateway выдачи проверенного PIN S3 VersionId; тестированная конкретная сборка Euro-Office (outbox callback JWT), anti-SSRF и real-time E2E.

## Локальное тестирование

\`\`\`sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
\`\`\`

**Код внесён в Draft PR #42; тесты не запускались** из-за отсутствия среды .NET/корпоративного SSO/Euro-Office в текущей сессии. Не включать GitHub Actions, не сливать без ИБ/локальной проверки.
