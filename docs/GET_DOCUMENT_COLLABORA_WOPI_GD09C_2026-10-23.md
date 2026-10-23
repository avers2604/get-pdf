# GD-09C — Collabora Online: серверный WOPI-контракт, без выбора основного движка

Дата: 23.10.2026. Parent: PR #22 (ONLYOFFICE server-side contracts).
Этот PR **не выбирает** ONLYOFFICE или Collabora как основное решение.
Работа по SOURCE-FIRST: переиспользовать существующий протокол WOPI, встроенный
`System.Security.Cryptography` и `Hs256Jwt` из уже имеющегося GD-09;
не разрабатывать собственные OT/CRDT и Office-rendering.

## Подтверждённые официальные спецификации

- Collabora: [SDK 25.04](https://sdk.collaboraonline.com/25.04/CO-SDK-manual.pdf)
  и [раздел безопасности](https://www.collaboraonline.com/security/).
- MS-WOPI: [CheckFileInfo JSON](https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-wopi/a4ba20a7-b571-4ba9-9cac-3f71cac4847a),
  [proof headers](https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-wopi/70254fa6-9d3d-4f93-9b1a-e6597a63b900)
  и [proof-key verification](https://learn.microsoft.com/en-us/microsoft-365/cloud-storage-partner-program/online/scenarios/proofkeys).
- Collabora WOPI discovery обычно доступен по `/hosting/discovery` и
  публикует поддерживаемые actions/urlsrc. Нужно доверенно загрузить и
  разобрать discovery, сформировать launch URL с `WOPISrc` и передать
  краткоживущий токен в form POST. Эти HTTP/UI шаги **пока не реализованы**.

## Реализовано в коде

- `src/GetDocument.Collaboration/WopiReadOnlySessionService.cs`:
  краткоживущий, сервером подписанный WOPI access token (1–15 мин)
  поверх существующего `Hs256Jwt`; привязка к стабильным
  documentId/revisionId и сотруднику; срок `access_token_ttl` в
  миллисекундах Unix. Проверка подписи, `aud`, `mode=view`,
  периода действия, отсутствия повторного использования между
  редакциями при `Authorize`.
- `CheckFileInfo`: поля BaseFileName, OwnerId, Size, Version, UserId,
  UserFriendlyName, ReadOnly, SupportsLocks=false,
  SupportsUpdate=false, UserCanWrite=false. Даже если пользователь
  может править обычный документ, **на этой стадии редактирование
  не рекламируется**: отсутствует проверенный серверный save/lock.
  Для WORM редактор остаётся только для просмотра.
- `PlanGetFile`: возвращает **только метаданные о разрешённом
  чтении** идентификатора/редакции; не читает сетевые файлы.
  Реальный WOPI HTTP GetFile + ACL-per-request предстоит реализовать
  во внутреннем Gateway. `PlanPutFile` намеренно выбрасывает
  `NotSupportedException` — никакого overwrite.
- `WopiProofValidator`: формирует proof input по MS-WOPI:
  4-байтные длины в network byte order, UTF-8 токен, URL в uppercase,
  8-байтный timestamp в UTC ticks; RSA-SHA256/PKCS1 проверяет
  X-WOPI-Proof с current key, X-WOPI-ProofOld с current key,
  X-WOPI-Proof с old key. **Old proof с old key не принимается.**
  Максимальный возраст 20 мин, допустимое будущее 5 мин.
  Разрешается только HTTPS URL конкретного WOPI origin; ключи
  должны поставляться **только из аутентифицированного discovery**
  с доверенным pinning, не из запроса.
- **12 MSTest-сценариев**: получение/отказ WOPI токена, срок действия,
  другой документ/ревизия, read-only WORM, нет PutFile,
  безопасные метаданные, GetFile-plan, реальная RSA-подпись в памяти,
  ротация ключей, изменение подписи/токена, timestamp, proof bytes.
  Эти тесты **только написаны**, не запускались.
- В `GetDocument.Collaboration` **0 новых пакетов**. В существующей
  цепочке Draft остались контракты ONLYOFFICE, которые не меняются.

## Ограничения / безопасность

Это НЕ полноценная WOPI-служба. Нет ASP.NET контроллера,
CheckFileInfo/GetFile HTTP-ответов, обнаружения/сертификата Collabora,
запущенной Collabora или iframe в Avalonia; редактор не подключён.
Нет PutFile, PutRelativeFile, Lock, Unlock, RefreshLock,
RefreshLock/UnlockAndRelock, server-sided version sink и ACL из AD.
Заявлять совместное редактирование пока нельзя.

Proof keys в реальном контуре публикуются Collabora при включении
подписи запросов. Нужно настроить `coolconfig generate-proof-key`
и подтвердить ключи через защищённый discovery; обработать ротацию.
WOPI URL должен соответствовать **оригинальному адресу запроса**,
включая query и публичный HTTPS origin reverse proxy.
В частности, нельзя слепо доверять `X-Forwarded-Host` или URL
из внешнего запроса: список proxy должен быть фиксирован и доверен.

Входной bearer token не должен попадать в журналы доступа.
WOPI grant сам по себе не доказывает существование файла или ACL:
для **каждого** HTTP запроса сервер проверяет user/document/revision,
действующую политику, неотозванность, срок токена, подпись proof,
роль и WORM. Нужно не допустить скачивание документов по
украденному URL/token.

Никаких секретов WOPI/ONLYOFFICE и криптографических private keys
в Avalonia клиент не включать.

## Лицензирование и производственное применение

По документации Collabora код преимущественно **MPL-2.0**, но
**исполняемые сборки** Collabora Online могут распространяться
на дополнительных коммерческих условиях. Бесплатная CODE — редакция
для разработки, **без SLA и не рекомендуется Collabora для business
production**. Возможность собственного сборочного контура из MPL
исходников проверить с юристами и ИБ, включая фирменные знаки,
права модификации, notices и стоимость сопровождения.

Сравнение с ONLYOFFICE (AGPL-3.0, ограничения white-label) обязательно.
Нельзя выбирать по простоте подключения или скриншотам.
**Главный критерий: максимальная функциональность Office через реально
доступный API при собственном UI ГЭТ**, затем точность OOXML roundtrip,
достаточность лицензии, локальная/offline работа и производительность.

## Обязательная локальная приёмка / дальнейшие работы

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

Дальше: независимая от конкретного движка лаборатория, корпоративные
DOCX/XLSX/PPTX, ручное CheckFileInfo/GetFile, WOPI discovery/proof,
блокировки и новая редакция вместо overwrite, закрытая сеть PKI,
Windows Server 2016 SMB + Linux/Samba; корректная работа на
Astra/ALT/РЕД ОС и слабом рабочем месте.

**В доступной среде нет .NET SDK: компиляция/MSTest не подтверждены.
GitHub Actions не включать. PR держать Draft до локальной приёмки.**
