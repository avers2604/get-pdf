# GD-09D — read-only HTTP WOPI host для Collabora

Дата: 2026-10-23. База: GD-09C / PR #23. Исходный код размещён в
`GetDocument.WopiGateway`; в `main` кодовые PR не слиты.
**Никакой production инсталляции или подтверждения работоспособности нет.**

## SOURCE-FIRST и назначение

Использованы встроенные ASP.NET Core Minimal APIs (.NET 10,
`Microsoft.AspNetCore.App`), HMAC/JWT и WOPI/RSA proof из
`GetDocument.Collaboration`. Не пишем свой HTTP сервер, формат Office,
протокол совместного редактирования, собственную криптографию,
отдельный файловый сервер или базу документов.

Реализовано как **библиотека обработчиков и интерфейсов**, а не
включённый по умолчанию сервер. В отсутствие конфигурации
хранилища, удостоверяющего центра и политики доступа приложение
не регистрирует публичный WOPI endpoint.

Основные вызовы:
- `MapGetDocumentWopiReadOnly(endpoints, options)` регистрирует только:
  `GET /wopi/files/{fileId}` — CheckFileInfo;
  `GET /wopi/files/{fileId}/contents` — GetFile.
  PUT/POST (PutFile, PutRelativeFile), lock/unlock, редактура,
  файловая публикация и выпуск токенов через HTTP намеренно отсутствуют.
- `IWopiReadOnlyStore` — обязателен внешний доверенный адаптер
  документа из существующего Windows Server 2016/SMB, позднее Linux/Samba
  либо GUP-2. Требуется стабильный `documentId` и `revisionId`,
  контроль длины и ожидаемого SHA-256.
- `IWopiReadAuthorization` — обязательный **отдельный вызов на
  каждый запрос** для действующих AD/ACL/GUP-2 прав.
  По умолчанию никакой политики «разрешить всем» нет.
- `WopiReadOnlyGatewayOptions` принимает только серверный JWT секрет,
  конкретный публичный HTTPS origin и две доверенные RSA public-key
  структуры, извлечённые из прошедшего верификацию discovery.
  Секрет не передаётся в Avalonia и не должен попадать в логи.

## Порядок обработки

1. Сверить безопасный идентификатор документа и точный путь операции,
   проверить HTTPS, фиксированный публичный Host и единственный
   `access_token` в query. Не доверять непроверенным
   `Forwarded/X-Forwarded-Host`.
2. Проверить HS256 JWT, claims документа и версии; после этого
   **проверить WOPI RSA proof** на исходном публичном URL со всеми query
   параметрами, `X-WOPI-TimeStamp` и ключами из доверенного discovery.
   При плохом proof вернуть HTTP 500 согласно MS-WOPI, не выдавая
   детали запроса/подписи.
3. Найти стабильную редакцию через `IWopiReadOnlyStore.FindAsync`,
   вновь выполнить `IWopiReadAuthorization.CheckReadAsync` и
   повторно проверить срок и принадлежность WOPI grant.
4. CheckFileInfo: вернуть JSON по MS-WOPI с `UserCanWrite=false`,
   `SupportsUpdate=false`, `SupportsLocks=false`,
   `UserCanNotWriteRelative=true`, `ReadOnly=true`.
   Никаких UNC путей, JWT и секретов в ответе.
5. GetFile: проверить `X-WOPI-MaxExpectedSize`, прочитать **не более
   32 МиБ** из разрешённого storage adapter, сверить точный размер
   с эталоном и SHA-256. До полной проверки **не отдавать байты**;
   затем вернуть `application/octet-stream`, `no-store` и
   `X-WOPI-ItemVersion`. Несовпадение = 409 без контента.
   Это bounded memory snapshot, не прямой SMB stream.
6. Для неизвестного файла или отсутствия ACL — 404,
   плохого WOPI bearer — 401, плохого proof — 500,
   клиентского ограничения размера — 412.
   Ошибки файлового ресурса не публикуют внутренний путь.

## Исправление обнаруженного бага PR #23

При независимой проверке исходников Microsoft
`microsoft/Office-Online-Test-Tools-and-Documentation`,
`samples/python/proof_keys/__init__.py` найдено критичное
несоответствие: ранее `WopiProofValidator.BuildExpectedProof`
использовал little-endian при кодировании 64-битной UTC ticks метки.
Эталон Microsoft использует `struct.pack('>Q', timestamp)` **big-endian**.
В GD-09D исправлено на `BinaryPrimitives.WriteInt64BigEndian`.
Добавлен отдельный тест на известную последовательность 01..08,
не зависящий от подписи, сгенерированной тем же ошибочным кодом.
Источник: https://github.com/microsoft/Office-Online-Test-Tools-and-Documentation/blob/master/samples/python/proof_keys/__init__.py
и https://learn.microsoft.com/en-us/microsoft-365/cloud-storage-partner-program/online/scenarios/proofkeys

## Требования безопасности до production

- Требуется отдельный **закрытый ASP.NET хост**, явная регистрация
  `IWopiReadOnlyStore` и `IWopiReadAuthorization` из действующей
  системы AD/ACL; никаких секретов в клиентском приложении.
- TLS корпоративного PKI, проверенные reverse proxy, доверенные
  `discovery` ключи Collabora + контролируемая ротация.
- Отдельная точка выдачи WOPI-токенов **только после проверки SSO/ACL**,
  короткий TTL и отзыв доступа; запрет логирования URL с
  `access_token` даже в proxy/web-server.
- Ограничение конкурентных запросов, памяти и времени, rate limit,
  предел входных заголовков, DoS и недоверенных Office-файлов.
  Текущая реализация ограничивает **один** запрос 32 МиБ, но
  не ограничивает суммарную память процесса и не предоставляет
  защиту от replay для принятия WOPI proof внутри 20 минут.
  Не заменяет корпоративные политики и firewall.
- `WopiProofValidator` не должен принимать публичный RSA-ключ
  из HTTP-заголовков, а только из доверенного discovery.
- Отображение в Avalonia, Collabora discovery/WOPISrc и получение
  реальных DOCX/XLSX/PPTX ещё НЕ реализованы.
- **Совместное редактирование не готово:** для него потребуются
  PutFile + блокировки + версия/конфликты и
  серверная запись **только новой редакции**, без изменения WORM.
- Проверить license + white-label Collabora/LibreOffice/ONLYOFFICE
  и интерфейс через реальный API. Никакой движок пока не утверждён.

## Тесты и порядок принятия

Подготовлен `GetDocument.WopiGateway.Tests` с 11 сценариями:
успешные CheckFileInfo/GetFile с реальной в тесте RSA-подписью,
изменение ACL между запросами, неверные JWT/proof/origin/путь,
истечение токена, ограничение размера, неправильный SHA-256,
невозможность просмотра чужой редакции.
В `GetDocument.Collaboration.Tests` добавлен отдельный регресс
на big-endian WOPI timestamp.

**В текущей среде нет .NET SDK.** Написанные тесты и статический
код-ревью не равнозначны выполненной сборке/тестам.

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
dotnet test tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj -c Release --no-restore
```

Скрипты `scripts/verify-get-document-windows.ps1` и
`scripts/verify-get-document-linux.sh` также дополнены новым проектом.

Для реальных испытаний: Windows Server 2016 с корпоративным SMB,
AD/ACL, Collabora Online в отдельном закрытом сервисе, HTTPS/PKI,
проверка X-WOPI-Proof на живых запросах, быстрый отзыв прав,
повреждённые файлы, параллельные запросы, WORM аудит GUP-2 и
фактическое открытие в одной вкладке Avalonia.

**Только Draft PR. GitHub Actions не включать; в main код не сливать
до локальной сборки, тестов и подтверждения ИБ/лицензий.**
