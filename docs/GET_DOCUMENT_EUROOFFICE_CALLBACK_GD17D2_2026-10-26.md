# GD-17D.2a — нативный callback Euro-Office, защита от ложного сохранения

**26.10.2026. Draft:** проверки и предохранители подготовлены; **полноценная запись в GUP-2 ещё не реализована**.

## Совместимость с Euro-Office

Euro-Office Document Server использует совместимый с Docs API цикл callback: статусы `1` (работа), `2` (закрытие с изменениями, сохранять), `4` (закрытие без изменений), `6` (force-save), `3/7` (ошибки). Конкретную **схему JWT outbox** и HTTP-заголовок необходимо подтвердить на **закреплённом билде Euro-Office** из закрытого репозитория бинарных пакетов. Нельзя автоматически использовать старый `OnlyOfficeCallbackVerifier`: он ожидает внутренний объект `{operation:"callback", payload:{...}}` и не является нативным DocumentServer callback.

Официальные ориентиры:
- https://github.com/Euro-Office/eurooffice-nextcloud
- https://github.com/Euro-Office/documentation/blob/main/docs/configuration/server.md
- https://api.onlyoffice.com/docs/docs-api/usage-api/callback-handler

## Изменения в серверной библиотеке

- `EuroOfficeNativeCallback.Plan`: сверяет HMAC-SHA256 подпись JWT и отдельный callback JSON **по полям key/status/url**, связывает `key` с конкретной редакцией, ограничивает размер и URL. `status=2/6` создаёт лишь `EuroOfficeSaveCandidate`; `1/4` завершаются без записи; `3/7` и неизвестные статусы отвергаются. URL должен принадлежать **строго закреплённому HTTPS origin** Document Server. Никакого сетевого GET, записи в файловую систему или `{"error":0}` внутри планировщика нет.
- `EuroOfficeCallbackAdmission`: доверенная сессия хранится сервером, callback id — непредсказуемый 128-bit идентификатор; это **долгоживущая серверная сессия редактирования** (максимальный остаточный TTL 12 часов), а не пяти минутный одноразовый код запуска; Key и origin привязаны к сессии, права отзываются и повторно проверяются при фактическом сохранении. Контракты `ITrustedEuroOfficeCallbackSessionStore` и `ITrustedEuroOfficeCallbackRevisionSink` **по умолчанию DenyAll**.
- В `GetDocument.EuroOfficeHost` добавлен `POST /get-document/editor/callback/{callbackId}`, независимо выключен через `GET_EUROOFFICE_CALLBACKS_ENABLED=0`. Не позволяет cookies/Origin или бесконечные тела, проверяет Content-Type/Content-Length и JWT из `AuthorizationJWT`, подписанный отдельным `GET_EUROOFFICE_OUTBOX_JWT_KEY_B64`. **Этот header и структура JWT — тестовая гипотеза до проверки конкретного Euro-Office релиза.** При отсутствии корпоративной сессии — `404`; при отсутствии корпоративного revision sink — `503 {"error":1}`, а не ложное подтверждение успешной записи. Только реально подтверждённый sink сможет ответить `{"error":0}`.
- `EuroOfficeCallbackCommitPolicy`: принимает ситуацию как готовую к реальной записи лишь когда подключены корпоративный revision sink, GUP-2 CAS и pinned архив; проверка flags не заменяет фактический ACL/CAS.

`EuroOfficeNativeCallbackTests` и `EuroOfficeCallbackAdmissionTests` проверяют нативные статусы, авторизацию, mismatch между подписанным и неподписанным JSON, подмену key, внешние HTTP/HTTPS URLs, ошибки, неготовую запись и отказ анонимным callbacks.

## Сохранение на стороне GUP-2

Сопряжённый **[GD-22 Draft](https://github.com/avers2604/Gup-2/blob/feature/get-document-eurooffice-callback-gd22/docs/GET_DOCUMENT_EUROOFFICE_CALLBACK_GD22_2026-10-26.md)** содержит `check_eurooffice_write_preflight` с проверкой права редактирования черновика, действующего `auth_version`, SHA/версии, WORM и pinned `OfficeWorkingRevision`. Она только читает данные: не создаёт запись и не предоставляет storage URL.

**Для работы редактора** обязателен следующий блок GD-17D.2b: безопасная загрузка callback output с защитой от DNS rebinding/SSRF, mTLS/ACL, отдельный серверный grant на save, повторный CAS на GUP-2 под DB locks, AV/OOXML, immutable archived S3 VersionId, append-only ledger/outbox, конфликтные ответы, защита от replay, дедупликация status=6/2 и восстановление после сбоя. Нельзя считать 503 текущего заглушённого sink «успешным сохранением».

## Нерешённое качество

Поддержка принудительного сохранения и статус 2 для одного и того же `document.key` должны быть идемпотентными. Callback fingerprint вычислен, но **не хранится** в надёжном дедупликационном реестре; до внедрения реестра запись выключена. Нужно провести протокольные пробы Euro-Office для действительной JSON/JWT формы и расположения `AuthorizationJWT`.

Уникальная серверная проверка URL по hostname недостаточна против SSRF и DNS rebinding — **в коде отсутствует GET callback URL и это намеренно**. Нужен запрет redirects, pinned TLS certificate/CA, DNS/IP validation на момент установления соединения, исходящие firewall ACL и загрузка только с контролируемого сервера.

## Приёмка

```bash
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

Для GUP-2 тесты из GD-22, PostgreSQL/S3/ObjectLock и сетевой e2e. В данной среде отсутствует .NET 10 SDK, интеграционные тесты и установленный Euro-Office. **Code review/статические проверки не заменяют компиляцию или успешную обработку callback**. PR оставлять Draft; не включать GitHub Actions и реальные корпоративные права доступа.
