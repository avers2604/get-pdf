# GD-25 — доверенный SSO-сеанс и закреплённая загрузка Euro-Office

26 октября 2026. Статус: **Draft, без работающего enterprise SSO server в данном окружении; production выключен**. База ветки — GD-24 / [PR #42](https://github.com/avers2604/get-pdf/pull/42). Связанный GUP-2 GD-25 реализуется в отдельном stacked PR.

## Исправление нативного JWT Document Server

По протоколу Docs API исходящий callback JWT содержит `{"payload":{"key":"...","status":2,"url":"..."},"operation":"callback"}` (в новых версиях операция обязательна), а не поля `key/status/url` на верхнем уровне токена. В прежнем планировщике `EuroOfficeNativeCallback` были top-level ожидания. Исправлены:
- проверка HMAC/HS256 перед чтением параметров, обязательные `operation=callback` и объект `payload`; `key/status/url` должны совпадать с отдельным JSON и корпоративным сеансом;
- `EuroOfficeCallbackToken.Select`: если JSON содержит `token`, использовать только проверенный body JWT, иначе заголовок **`Authorization: Bearer`** (стандарт), не исторический демо-`AuthorizationJWT`; неверный body JWT никогда не заменяется header JWT;
- статусы `2/6` — лишь кандидаты на сохранение, `1/4` — отсутствие новой редакции, `3/7` отклоняются. Результат без CAS никогда не подтверждать `error:0`.

Официальная документация: https://api.onlyoffice.com/docs/docs-api/additional-api/signature/request/token-in-header и https://github.com/Euro-Office/documentation/blob/main/docs/configuration/server.md . Версия и конфигурация реальной Euro-Office сборки должны быть закреплены и испытаны, прежде чем включать callback.

## Безопасная загрузка изменённой редакции

`PinnedEuroOfficeDownloader` реализует server-side HTTPS GET без разрешения произвольных сетевых адресов:
- документный URL обязан иметь тот же `scheme=https`, hostname и port, что настроенный корпоративный `GET_EUROOFFICE_ORIGIN`;
- транспорт `SocketsHttpHandler.ConnectCallback` **никогда не разрешает DNS URL в адрес для TCP**; сокет всегда устанавливает соединение к заранее разрешённому администратором `IPAddress`. Host и SNI остаются исходными, X.509 проверяется стандартной системой доверенных CA;
- `UseProxy=false`, `AllowAutoRedirect=false`, `UseCookies=false`, `AutomaticDecompression=None`, 200 only, только разрешённые MIME DOCX/XLSX/octet, обязательный фиксированный Content-Length до 64 МиБ;
- усечённая или расширенная передача — отказ; полный массив байтов получает SHA256 до передачи к `Gup2EuroOfficeSaveTransportClient`. Запись по-прежнему проходит AV/OOXML, версионный CAS и ObjectLock на GUP-2.
- При отсутствии одобренного IP или несовпадении host/port не выполняется сетевой запрос. Оператор должен подтвердить актуальный IP и корпоративный CA после изменения сетевой топологии.

## SSO→broker контракт

`Gup2EuroOfficeSsoIntentClient` погашает отдельный 256-битный браузерный одноразовый код GUP-2 после **независимой проверки** актуального корпоративного employee UUID на сервере брокера. Формат HMAC:

```text
POST
getdocument/eurooffice/sso-intent/redeem
<64 lowercase hex browser intent>
<canonical lowercase GUP-2 employee UUID>
<unix UTC seconds>
```

POST только на `https://<corporate-gup2>/documents/getdocument/eurooffice/sso-intent/redeem/`, нулевое тело, ключ `GETDOCUMENT_EUROOFFICE_SSO_INTENT_KEY` **отдельный** от writeback HMAC и JWT. В ответе после успешного ACL/SSO повторного контроля GUP-2 возвращает строго проверяемые `document_id/user_id/edit_version/sha256/extension/document_key/save_grant`. `document_key` перепроверяется как SHA256 `UTF8(doc UUID + LF + edit_version)`. Код save_grant держится **только на серверной стороне**; `ToString()` модели ответа не показывает его.

## Состояние подключения

Функции и unit tests добавлены, но реальный `GetDocument.EuroOfficeHost` всё ещё регистрирует `DenyAllEuroOfficeLaunchStore`, `DenyAllEuroOfficeCallbackSessionStore` и `DenyAllEuroOfficeCallbackRevisionSink`. Нет SSO issuer в ASP.NET, доверенного файлового сервера/URL для Document Server и устойчивого реестра совместных сессий. Подключать editor к боевым документам только после следующего этапа с локальными PostgreSQL/S3/Keycloak/Euro-Office испытаниями.

## Локальная приёмка

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

Добавлены `EuroOfficeCallbackTokenTests`, исправлены `EuroOfficeNativeCallbackTests`, добавлены `PinnedEuroOfficeDownloaderTests` и `Gup2EuroOfficeSsoIntentTests`. **.NET SDK и реальный Euro-Office/HTTPS сервер отсутствуют в текущей исполнительной среде; команды пока не запускались.** Оставить оба PR Draft, не включать CI вместо локальных тестов.
