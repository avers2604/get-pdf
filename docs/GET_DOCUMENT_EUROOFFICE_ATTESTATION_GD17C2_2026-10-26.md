# GD-17C.2 — внутренний контракт GUP-2 / Euro-Office

**26.10.2026. Draft.** Связанный репозиторий GUP-2: ветка `feature/get-document-eurooffice-launch-gd20`, отдельный GD-20 Draft PR.

## Что работает на уровне исходников

- `Gup2EuroOfficeAttestationClient` генерирует подписанный сервисный **POST с пустым телом**, проверенным HTTPS /documents/ адресом и HMAC-SHA256; shared service secret используется только backend, **не Avalonia/WebView**.
- Сообщение HMAC в точности: `POST\ngetdocument/eurooffice/consume\n{nonce}\n{userUuid}\n{unixSeconds}`. Заголовки: `X-Get-Eurooffice-Nonce`, `X-Get-Eurooffice-User`, `X-Get-Eurooffice-Timestamp`, `X-Get-Eurooffice-Signature`.
- Получаемая attestation строго read-only, только `status=attested`, document UUID, revision ID, employee UUID, SHA-256, extension DOCX/XLSX и `can_edit=false`. **Любые дополнительные ключи, content/callback URL или `can_edit=true` отвергаются**.
- `Gup2EuroOfficeAttestationTests` покрывают одинаковый формат HMAC, отсутствие nonce в URL, формат сотрудников, закрытый HTTPS, строгие ответы и отказ неожиданных полномочий.
- GUP-2 дополнен SSO-выдачей аттестации и сервисным endpoint; ключи раздельны, одноразовый nonce привязан к актору, версии и SHA; записи не разрешены.
- Предыдущая `Gup2OfficeTabBindingClient` не изменяет полномочия: это **отдельный** механизм локального связывания вкладки SHA-256.

## Чего это НЕ делает

**Сервис GUP-2 подтверждает факт доступа, но пока не выдаёт Document Server доступ к байтам.** Поэтому `Gup2EuroOfficeAttestationClient` намеренно НЕ реализует `ITrustedEuroOfficeLaunchStore`, а `GetDocument.EuroOfficeHost` остаётся с `DenyAllEuroOfficeLaunchStore`. Ни документ, ни сотрудник не откроет реальный корпоративный редактор через этот код.

Для реального запуска отдельно требуются:

1. Корпоративный SSO на ASP.NET broker с принудительным связыванием внутреннего GUP-2 UUID сотрудника. Нельзя доверять пришедшему с клиента `X-User` и нельзя считать nonce логином.
2. Корпоративный HTTPS content gateway со строгим server-side ACL на **каждый** запрос, проверкой имени/типа/размера и контроля SHA.
3. Безопасная обработка callback Euro-Office с JWT, запретом redirects/SSRF, antivirus/OOXML intake, повторным ACL, и атомарным созданием новой рабочей версии через GUP-2 `services.update_document`, строго без изменения WORM-оригинала. Нельзя повторно применять native office-return capability.
4. Стабильный co-edit session identity, контроль отзыва прав, тесты 2/5/10 сотрудников и ограниченный API в собственной ленте ГЭТ.

## Тестирование

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

GUP-2: `python manage.py test apps.documents.tests.test_getdocument_eurooffice_launch` с локальным PostgreSQL; миграции 0040 проверять без auto-merge.

**Статус:** код добавлен и тестовые сценарии написаны; .NET, Django/PostgreSQL, настоящий сервер Euro-Office и корпоративное SSO в доступном окружении не запущены. Поэтому никакой production activation, GitHub Actions, слияния и претензии на выполненный E2E нет.

Секреты HMAC и JWT **раздельны**, не включать отладочные демо; запрещён доступ к внешнему интернету и self-signed TLS exception.
