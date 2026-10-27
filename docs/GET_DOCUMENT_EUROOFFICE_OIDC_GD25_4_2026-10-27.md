# GD-25.4 — корпоративный OIDC (Keycloak / authentik) для EuroOfficeHost

Дата: 27 октября 2026. Статус: **Draft. Реальный login-контур подготовлен; корпоративный редактор и WORM-файлы недоступны**.

## Реализовано в коде

- \`src/GetDocument.EuroOfficeHost/EuroOfficeOidcAuthentication.cs\` — стандартный Microsoft.AspNetCore.Authentication.OpenIdConnect **10.0.14**, .NET 10, confidential Authorization Code Flow + **PKCE**, внутренний корпоративный OIDC provider (Keycloak/authentik).
- \`src/GetDocument.Collaboration/EuroOfficeOidcConfiguration.cs\` — строгий разбор конфигурации (не допускаются HTTP, userinfo, query, fragment, escape-пути, невалидный client_id, короткий секрет). Отсутствующие переменные при \`GET_EUROOFFICE_OIDC_ENABLED=0\` безопасны.
- OIDC metadata и JWKS получаются через HTTPS из **фиксированного issuer**; \`TokenValidationParameters\` требуют JWT подписи, issuer, audience, expiration, 30-sec clock skew, только **RS256**, без fallback к HS256, unsecured JWT или чужому JWKS. Authorization endpoint дополнительно ограничен origin выбранного IdP. Отключены HTTP redirects, cookies и системные полномочия во внутренних запросах backchannel.
- \`OnTicketReceived\`: **после** полной OIDC-проверки библиотеки, включая nonce/state и подпись ID token берётся ровно один \`sub\` и один \`gup_user_id\`, значение \`gup_user_id\` — канонический непустой UUID GUP-2. При конфликтующих ID или отсутствии подписанного claim вход отклоняется. Новая корпоративная principal получает только \`ClaimTypes.NameIdentifier\`, authentication type \`CorporateOidc\`. Логин ОС / заголовок proxy / \`sub\` нельзя использовать как GUP-2 UUID.
- Cookie \`__Host-GetEuroOfficeSession\`: Secure, HttpOnly, Path=/, SameSite=Lax, срок 5 минут, sliding renewal выключен; cookie повторно проходит \`EuroOfficeEnterprisePrincipalPolicy\` на каждом запросе. Токены OIDC/refresh не сохраняются в cookie (\`SaveTokens=false\`).
- \`GET /get-document/auth/login\` — **только** когда OIDC включён, инициирует OIDC challenge с фиксированным локальным return URL \`/get-document/auth/status\`. \`GET /get-document/auth/status\` возвращает только \`authenticated=true,editorAccess=false\` или 401 (без документа, UID и секретов).
- Режим OIDC включается **независимо** от редактора. \`GET_EUROOFFICE_ENTERPRISE_LAUNCH_ENABLED=1\` по-прежнему намеренно блокирует startup, пока нет проверенных \`ITrustedEuroOfficeLaunchStore\`, контент-шлюза и callback sink. \`ITrustedEuroOfficeLaunchStore\`/ \`ITrustedEuroOfficeCallbackRevisionSink\` остаются DenyAll; \`GET_EUROOFFICE_CALLBACKS_ENABLED=0\`.

## Переменные для закрытого контура

Задавать только в защищённых server secrets/конфигурации, **не коммитить значения**:

| Переменная | Допустимое назначение |
| --- | --- |
| \`GET_EUROOFFICE_OIDC_ENABLED\` | \`0\` по умолчанию; \`1\` только на лабораторном сервере |
| \`GET_EUROOFFICE_OIDC_ISSUER\` | точный HTTPS issuer, напр. \`https://sso.intra.get/realms/get\` (Keycloak) или issuer приложения authentik |
| \`GET_EUROOFFICE_OIDC_CLIENT_ID\` | зарегистрированный конфиденциальный web client, напр. \`get-documents-broker\` |
| \`GET_EUROOFFICE_OIDC_CLIENT_SECRET\` | 32+ символа, из менеджера секретов; никому не пересылать |
| \`GET_EUROOFFICE_ENTERPRISE_LAUNCH_ENABLED\` | \`0\` (до полной интеграции защищённых документов) |

**В IdP:** зарегистрировать confidential client, только Authorization Code, PKCE S256; redirect URI **строго** \`https://<internal-eurooffice-host>/signin-eurooffice-oidc\`, без wildcard; запретить implicit/password grant; client secret, MFA для привилегированных, корпоративную CA. Обязательно настроить protocol mapper/Scope, включающий **неизменяемый \`gup_user_id\` в подписанный ID token**. \`sub\` — собственная неизменяемая ID IdP, **не подмена UUID GUP-2**. В GUP-2 и IdP UUID должны связываться из контролируемого реестра, а не из пользовательского профиля.

Для закрытого контура на Windows/Astra Linux: внутренние DNS, корпоративные корневые CA на host, внутренний NuGet mirror для \`Microsoft.AspNetCore.Authentication.OpenIdConnect 10.0.14\` и транзитивных зависимостей. Для host использовать **настоящее HTTPS до Kestrel** или доверенную явно настроенную схему TLS-to-TLS: сейчас проект не доверяет произвольным \`X-Forwarded-Proto\`. OIDC discovery/JWKS и callback должны быть доступны внутри закрытой сети; внешний Интернет не требуется.

## Безопасность и незакрытые задачи

1. **Немедленный logout/revocation IdP:** пока cookie действует до 5 минут, а не проверяется в каждом запросе на сервере IdP. До появления backchannel logout/онлайн-проверки корпоративного пользователя **нельзя** разрешать редактирование или доступ к документам по одной этой cookie.
2. OIDC \`sub\`-mapping и \`gup_user_id\` должны пройти проверку на конкретных сборках Keycloak/authentik, включая JWKS rotation, refresh metadata, issuer mismatch, consent/nonce/state, локальную корпоративную CA.
3. \`ITrustedEuroOfficeLaunchStore\` всё ещё DenyAll. GD-25.5 должен атомарно проверить текущий SSO и одноразовую сессию GUP-2, а не использовать cookie как самостоятельный grant.
4. Безопасный data gateway и проверенный Euro-Office callback пока не подключены. Реальное 2/5/10-user E2E и Object Lock тесты обязательны перед снятием заглушек.
5. **Сетевые логи**: не писать raw JWT, code/state, client_secret, launch nonce, document key или Authorization. Reverse proxy должен скрывать чувствительные пути. На сервере отключены подробные логи ASP.NET start paths.

## Тесты

\`tests/GetDocument.Collaboration.Tests/EuroOfficeOidcContractTests.cs\`: issuer pin/HTTPS/redirect, secret strength, отсутствие OIDC config в off-режиме, проверка ID-token claim \`sub\` и \`gup_user_id\`, отказ при duplicate/anonymous/conflicting/uppercase, redacted \`ToString\`.

Локальная проверка без GitHub Actions:

\`\`\`sh
dotnet restore GetDocument.slnx --source <your-internal-nuget-mirror>
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
\`\`\`

**В текущем окружении .NET SDK и корпоративного IdP нет**; тесты и компиляция **не запускались**. PR оставить Draft; включать OIDC лишь на лабораторном стенде с настоящим IdP и сетевой ИБ-приёмкой, editor launch не включать.
