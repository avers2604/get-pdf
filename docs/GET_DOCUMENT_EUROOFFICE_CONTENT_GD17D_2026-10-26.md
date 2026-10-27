# GD-17D.1 — серверный клиент чтения зафиксированной Office-редакции GUP-2

Дата: 26.10.2026. **Draft implementation** с отдельно выключенным backend endpoint, без запуска редактора или callback-записи.

GUP-2 реализует отдельный серверный read-only HMAC endpoint в [GD-21](https://github.com/avers2604/Gup-2/blob/feature/get-document-eurooffice-content-gd21/docs/GET_DOCUMENT_EUROOFFICE_CONTENT_GD21_2026-10-26.md). Это **не** повторное использование `GetDocumentOfficeGrant` или `GetDocumentTabBindGrant`; они выполняют другие операции. Короткий одноразовый nonce из GD-20 допускает **либо** получение метаданных, **либо** чтение pinned файла (не обе операции).

## Что внесено в C#

- `Gup2EuroOfficeAttestationClient.CreateSignedContentRequest` формирует отдельный HMAC canonical purpose `POST\ngetdocument/eurooffice/content\n{nonce}\n{user}\n{seconds}` и `POST https://gup2.intra.example/documents/getdocument/eurooffice/content/`, без токенов в URL или теле.
- `Gup2EuroOfficeContentClient` — серверный HTTP-клиент: без cookies, fallback credentials, redirect, с TLS validation ОС, строгим `200`, ожидаемым Content-Type и Content-Length, отказом на усечённый поток, ограничением размера и SHA-256 прежде, чем байты могут быть переданы дальше.
- Важный текущий лимит GET broker — **64 МиБ на файл**, чтобы не записывать секретные данные Office в незашифрованный временный каталог и не загружать произвольно большой файл в память. GUP-2 позволяет до 256 МиБ; для более крупных документов последующим этапом необходим защищённый дисковый spool/потоковый канал с независимым механическим SHA before-serve.
- `ReadVerifiedResponseAsync` позволяет локально тестировать truncated/MIME/size/errors независимо от сервера.
- `Gup2EuroOfficeContentTests`: проверка HMAC scope и неизменяемости URL, побайтового SHA, превышения лимита, недопустимого MIME, усечённых ответов и отказа на redirect.

## Почему нельзя считать завершённой интеграцию с редактором

`GetDocument.EuroOfficeHost` остаётся зарегистрированным с `DenyAllEuroOfficeLaunchStore`: существующий аттестационный/бинарный клиент **пока не** выдаёт Document Server ссылку, не подтверждает пользовательскую SSO-сессию и не может создать ревизию по callback. Запуск сессий и совместная запись требуют отдельной тестируемой сети доверия; прямой проксирующий URL к GUP-2/S3 никогда не передавать браузеру.

Для настоящего редактора нужна server-side политика, связывающая один GUP-2 actor UUID, один document/revision SHA, одноразовую привилегию на конкретный operation, ACL/rescope, sessionKey и ограниченный token. На чтение и запись должны быть отдельные права и endpoints. При отсутствии GUP-2 backend — отказ, а не переключение на публичный сервер.

## Локальная приёмка

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

Отдельно: корпоративный HTTPS/CA, отключение redirect, pinning S3, отзыв прав во время чтения, контроль Content-Length при усечении, size и memory pressure, test fixtures DOCX/XLSX, security audit.

**Фактически:** исходники и MSTest добавлены в Draft PR #42; .NET SDK/сервер Euro-Office/интеграционные тесты не запускались, production включать нельзя. WORM-оригиналы, PDF и мастер печати не изменены.
