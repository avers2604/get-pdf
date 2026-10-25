# GD-09F — HTTP WOPI-шлюз для совместного редактирования в реальном времени

Дата: 24–25 октября 2026. Статус: **Draft / исходный код подготовлен;
не запущен на корпоративном сервере**.

## Связь с существующими PR

- [PR #24](https://github.com/avers2604/get-pdf/pull/24) — готовый read-only
  ASP.NET WOPI host, WOPI proof RSA, bounded GetFile, fail-closed policy:
  **исходники 6 файлов импортированы без переписывания с нуля**;
  независимые 13 тестов HTTP сохранены.
- [PR #26](https://github.com/avers2604/get-pdf/pull/26) — Collabora
  discovery and view action; унаследован.
- [PR #27](https://github.com/avers2604/get-pdf/pull/27) — общий roomKey,
  персональные токены, edit action, ONLYOFFICE Fast и immutable CAS
  revision store: унаследован.
- Этот PR объединяет эти ветви по функциональному назначению, но **не
  сливает их в main**, не включает GitHub Actions.

## Контракт реального времени

**Движок Collabora Online** выполняет передачу правок, курсоров и
совместную работу пользователей в одной комнате через собственную
серверную синхронизацию. Шлюз НЕ распространяет изменения после
каждой клавиши; он предоставляет WOPI `CheckFileInfo`, `GetFile` и
`PutFile`, то есть транспорт полных ревизий/прав доступа.

Одному документу+базовой редакции соответствует общий WOPISrc/roomKey.
У каждого пользователя собственный signed access token. В
`CheckFileInfo` сервер показывает `UserCanWrite` только с живым
разрешением сотрудника; просмотр WORM остаётся только для чтения.

### Маршруты и защита

`WopiLiveHttpGateway` (`GetDocument.WopiGateway`) содержит:
- `GET /wopi/files/{fileId}` — авторизованный `CheckFileInfo`, включая
  LastModifiedTime, текущую хранимую Version и права пользователя.
- `GET /wopi/files/{fileId}/contents` — выдача заранее проверенного
  immutable snapshot; поток читается с лимитом 32 МиБ, длина и SHA-256
  сверяются **до** HTTP-ответа. Физический путь не отправляется клиенту.
- `POST /wopi/files/{fileId}/contents` — PutFile Collabora. Проверка
  X-WOPI-Proof, signed JWT `aud=get-document-wopi-live`, `doc`,
  `ver`, `sub`, `nbf`, `exp`, индивидуальных ACL и WORM-политики,
  body <=32 МиБ. Обрабатываются стандартный `X-WOPI-Override=PUT`
  и Collabora POST без Override. Иной непустой override отклоняется.
  Поддерживается и ограниченный chunked body без Content-Length.
- `X-COOL-WOPI-Timestamp` сверяется с последней редакцией. При
  расхождении или отсутствии **HTTP 409 с `{"COOLStatusCode":1010}`**.
  Операцию «overwrite anyway» без timestamp принципиально запрещаем,
  чтобы сохранить другие правки и WORM-оригинал.
- Успех возвращает новый X-WOPI-ItemVersion и JSON LastModifiedTime,
  сохраняя новый файл отдельной редакцией через доверенный
  `ITrustedRealtimeWopiDocumentStore`.
- WOPI proof RSA использует **big-endian timestamp** по MS-WOPI и
  доверенные ключи; публичный HTTPS origin строго разрешён.
  Секрет JWT и приватные ключи не попадают в приложение Avalonia.

### Корпоративная безопасность

HTTP сервер **по умолчанию остаётся read-only**. Попытка запустить
`GET_WOPI_HTTP_MODE=live` **отклоняется при старте приложения**,
пока сервер не связан с явно утверждённым корпоративным ACL/SSO +
immutable revision backend. `WopiHostMode.Resolve` проверяет
конфигурацию; присвоение одной переменной окружения не открывает
маршруты записи. Read-only хост остаётся с `DenyAllWopiDocumentStore`.

Добавлен `RevisionBackedWopiDocumentStore`, переиспользующий существующий
`ImmutableOfficeRevisionStore` и абстракцию
`IEnterpriseDocumentPolicy`. Доверенная политика должна вернуть
**стабильные** documentId/sessionRevisionId и пользователя, имя файла,
CanEdit/IsWormOriginal; никакие абсолютные пути от HTTP-клиента
не принимаются. Перед `CommitNewRevisionAsync` политика спрашивается
заново, а HTTP-шлюз повторно проверяет ACL ещё и после загрузки body.
Используется optimistic CAS: новая редакция не перезаписывает оригинал.

`DenyAllEnterpriseDocumentPolicy` запрещает любую выдачу по умолчанию.
**Реального провайдера AD/GUP-2 нет**, и `Program.cs` НЕ объявляет
этот адаптер корпоративно готовым: включение real-time сервера требует
явного подключения и приёмки, а не флага окружения.

Сервер должен на **каждом запросе** проверять права через AD/SSO/GUP-2;
для сохранения права нужно проверить повторно непосредственно перед
CAS. Не выдавать права по факту доступности SMB ресурса service account.
В реальной системе нельзя автоматически подключать тестовый
`ImmutableOfficeRevisionStore` без защитного ACL-адаптера.

`ImmutableOfficeRevisionStore` — локальный/односерверный прототип,
использующий уникальные файлы, SHA-256 и межпроцессный lock.
Добавлены ограниченные retry блокировки при параллельном сохранении,
затем проверка expectedRevisionId; старый WORM original и
предыдущие снимки никогда не перезаписываются.
Для нескольких узлов и SMB требуется устойчивый **серверный CAS,
транзакционный manifest и журнал аудита** — обычный FileShare.None
не объявляется распределённой транзакцией.

### Длительная смена

Токен только для **live-сеансов** теперь разрешает TTL 1 минута –
**10 часов**. Collabora показывает предупреждение за 15 минут до
истечения срока; старый максимум 15 мин создавал риск немедленных
предупреждений и потери несохранённой работы. На каждом HTTP-запросе
сохраняется проверка JWT, WOPI proof и текущего ACL/отзыва прав.
Статические read-only токены унаследованного шлюза не меняются
(1–15 мин). Для смен длиннее десяти часов требуется повторная
авторизация/механизм token refresh, проверенный на конкретной
версии Collabora.

Официальная документация:
- https://sdk.collaboraonline.com/CO-SDK-manual.pdf
- https://learn.microsoft.com/en-us/microsoft-365/cloud-storage-partner-program/rest/concepts
- https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-wopi/42d0e5ae-074f-4d11-9d9b-706d843fad9c

## Сценарии локальных тестов (НАПИСАНЫ, НО НЕ ЗАПУЩЕНЫ)

- 13 тестов read-only HTTP WOPI унаследованы из PR #24.
- 16 тестов `WopiRealtimeHttpGatewayTests`: два участника,
  текущая редакция и хеш, подписанный GetFile, POST success,
  stale-save 409/1010, WORM read-only, ограничение размера,
  отсутствие timestamp, Collabora без override, chunked, deny-all,
  отзыв прав и proof-failure; включая **отзыв ACL непосредственно
  во время загрузки файла**.
- 5 тестов `RevisionBackedWopiDocumentStoreTests`: доверенная политика,
  повторная проверка AD/ACL перед commit, запрет чужого документа,
  immutable snapshot, конфликт при устаревшей версии.
- 3 теста `WopiHostModeTests`: read-only по умолчанию и запрет
  старта live-режима до подключения реального ACL backend.
- 2 теста concurrent/CAS в `ImmutableOfficeRevisionStoreTests`,
  включая уже занятый файл на файловой системе.
- 1 тест 10-часового live-сеанса и истечения срока.

```bash
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

Реально запускать локальные scripts `verify-get-document-windows.ps1`,
`verify-get-document-linux.sh` без GitHub Actions. **Сейчас .NET SDK
в окружении отсутствует; компиляция и тесты не подтверждены.**

## Release blockers

1. Развернуть Collabora Online и ONLYOFFICE Docs в изолированной
   тестовой сети, с корпоративным PKI; оценить лицензии и white-label.
2. Подключить AD/SSO/проверку файловых ACL и GUP-2 WORM/audit через
   `ITrustedRealtimeWopiDocumentStore`. Добавить антивирусную проверку
   и валидацию OOXML ZIP (включая zip bomb и размеры содержимого).
3. Проверить реальные DocEditor/PostMessage/WebView во вкладке ГЭТ,
   авторизацию, выход и пересоздание сессий в Windows и
   Astra/ALT/РЕД ОС; UI редактора пока не полностью свой через API.
4. Проверить одновременно 2, 5 и 10 сотрудников на DOCX/XLSX/PPTX,
   изменения, курсоры, автосохранение, конфликты, разрыв сети,
   перезапуск сервера и отзывы прав; измерить время видимости
   правок (цель P95 <=2 сек по локальной сети).
5. Разработать резервное копирование, восстановление и
   промышленный транзакционный CAS для центрального хранилища.
6. Выполнить реальную сборку и MSTest. До этого — оставить Draft,
   не подключать реальные корпоративные документы.

**Статус:** подготовленный интеграционный слой и тестовые спецификации,
но пока НЕ готовая работающая совместная сессия на пользовательских ПК.
