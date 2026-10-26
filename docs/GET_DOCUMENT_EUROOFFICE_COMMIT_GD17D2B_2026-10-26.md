# GD-17D.2b / GD-23 — доменный CAS commit Euro-Office в GUP-2

26.10.2026. Статус: **Draft, внутренний серверный commit в GUP-2 реализован, вызов от Euro-Office НЕ подключён**.

## Два связанных PR

- GUP-2 [GD-23 PR #179](https://github.com/avers2604/Gup-2/pull/179): `apps/documents/eurooffice_commit.py`, модели/миграции 0041–0042, тесты `test_getdocument_eurooffice_commit_gd23.py`.
- GET Documents [PR #42](https://github.com/avers2604/get-pdf/pull/42): `EuroOfficeNativeCallback`, `EuroOfficeCallbackAdmission`, отдельный outbox JWT, `Gup2EuroOfficeContentClient`, принимающий модуль и *DenyAll* sink.

## Подтверждённый серверный контракт

GUP-2 internal `commit_trusted_eurooffice_callback(session, uploaded)` принимает только:
- удостоверенную сервером UUID-пару employee/document и актуальный `auth_version`;
- `expected_edit_version`, SHA исходной рабочей копии, SHA WORM PDF;
- тип DOCX/XLSX, уникальный fingerprint аутентифицированного callback и предварительно проверенный SHA-256 полученного файла;
- серверно полученные байты файла максимум **64 МиБ**.

На GUP-2 повторно проверяются ACL, статус черновика, WORM, S3 VersionId, AV/OOXML, конфликт CAS. Применяется штатный `services.update_document` и `archive_copy`. Новая версия, SHA-256, append-only `OfficeWorkingRevision`, receipt и событие outbox фиксируются совместно в PostgreSQL; WORM original не переписывается. Повтор точно этого же callback не дублирует редакцию; конфликт другого события со старой версией отвергается.

## Непреодолённая граница доверия

`TrustedEuroOfficeWriteSession` — Python объект, **не секрет и не авторизация**. Нельзя заполнять его из браузерного JSON или простого `document.key`. `EuroOfficeHost` по-прежнему использует `DenyAllEuroOfficeCallbackSessionStore` и `DenyAllEuroOfficeCallbackRevisionSink`. Следовательно, **реального автосохранения и рабочей совместной сессии нет**.

Прежде чем поменять DenyAll на подключённый sink, требуется GD-23B:
1. GUP-2 выпускает привязанный к SSO одноразовый **серверный write-grant** (не повторное использование 5-минутных read grants); поддерживает безопасные серии force-save и статус 2.
2. Доверенный broker проверяет сертификат и JWT конкретной сборки Euro-Office, контролирует replay, PIN DNS/IP и загружает результат только с allowlisted HTTPS Document Server (никакого SSRF/redirect).
3. Межсерверная передача результата и метаданных с mTLS/HMAC, запись разрешена только после подтверждённого server-side grant, с повторной CAS проверкой GUP-2 и фиксированной политикой byte limit.
4. Broker требует `working_copy_saved` с SHA/версией, а не просто HTTP 200. Только после действительного commit отправляет `{"error":0}`; при конфликте `{"error":1}` и соответствующий HTTP код, исходная версия не затирается.
5. Полный контур E2E: 2/5/10 пользователей, смена сотрудника/прав посреди сессии, параллельные save, AV/S3/DB outage, обрыв сети и повтор после восстановления.

## Проверки

GUP-2: `python manage.py check`; `python manage.py makemigrations --check --dry-run`; `python manage.py test apps.documents.tests.test_getdocument_eurooffice_commit_gd23` плюс существующие Office tests. .NET: `dotnet build GetDocument.slnx -c Release` и `dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release`.

**Здесь не запускались** Django/PostgreSQL/S3/Object Lock/.NET/настоящий Euro-Office. Все функции переключены в deny-by-default до реальной локальной приёмки. GitHub Actions не включать.
