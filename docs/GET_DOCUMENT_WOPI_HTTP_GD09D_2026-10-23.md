# GD-09D — серверный WOPI HTTP gateway для Collabora (read-only)

**Дата:** 23 октября 2026. **Источник:** PR #23 (Collabora/WOPI) и
утверждённый план «ГЭТ Документ», SOURCE-FIRST. Это отдельный модуль,
который не устанавливается на компьютеры пользователей и не переносит
сканирование/WPF/PDF инфраструктуру.

## Реализовано в этом Draft PR

**`GetDocument.WopiGateway`** (`net10.0` + встроенный
`Microsoft.AspNetCore.App`, без нового NuGet) реализует:

- `GET /wopi/files/{fileId}?access_token=...` — WOPI `CheckFileInfo`.
- `GET /wopi/files/{fileId}/contents?access_token=...` — WOPI `GetFile`.
- `POST /wopi/files/{fileId}` и `POST /wopi/files/{fileId}/contents`
  возвращают 405: редактирование, PutFile, Lock и сохранение на этом
  этапе запрещены, даже для обычных документов.
- Требование **HTTPS public origin**, валидной RSA proof-подписи из
  `X-WOPI-Proof`/`X-WOPI-ProofOld`, актуальной временной метки,
  подписанного HS256 токена именно для нужных documentId/revisionId/userId
  и соответствия правам в доверенном `ITrustedWopiDocumentStore`.
- Токен валидируется **до** обращения к хранилищу. При отсутствии
  разрешений данные о существовании файла не выдаются.
- Для `GetFile` проверяется `X-WOPI-MaxExpectedSize`,
  плановый размер до 256 МиБ и совпадение фактической длины потока.
  Только **seekable immutable revision snapshots**, не открытые
  для конкурентного изменения WORM/SMB оригиналы.
- `DenyAllWopiDocumentStore` — безопасный провайдер по умолчанию.
  Нет автоматического доступа к SMB, сетевого сканирования или
  распространённого пароля; он **всегда отклоняет файлы**.
- `GetDocument.WopiHost` — запускаемая серверная ASP.NET Core
  оболочка, которая требует настройки секретов, pinning RSA-ключей,
  правильного HTTPS origin и службы с корпоративным TLS.
  Отсутствие параметров приводит к отказу запуска, HTTP-запросы
  также отвергаются. В безопасной стартовой конфигурации host
  регистрирует `DenyAllWopiDocumentStore`, без реальной выдачи байтов.
- Уточнён `WopiProofValidator`: timestamp должен кодироваться
  в **big-endian** (проверено по исходнику официальных тестов
  Microsoft `Office-Online-Test-Tools-and-Documentation`,
  `samples/python/proof_keys/__init__.py`, `struct.pack('>Q', ...) `).
  Ранее в PR #23 использовался little-endian, исправлено в этой ветке.

## Граница доверия

`ITrustedWopiDocumentStore.ResolveForReadAsync(documentId, revisionId, userId)`
должен реализовать корпоративный **серверный** модуль:
1. Подтвердить SSO/AD и право пользователя читать *конкретную* редакцию.
2. Проверить отзыв разрешения **при каждом** запросе; связать
   stable documentId с файлом на существующем Windows Server 2016
   или будущем Linux/Samba, не выдавать произвольные UNC-пути.
3. Открыть сохранённый **неизменяемый снимок** нужной редакции, с
   стабильными размером/контрольной суммой. Не читать активный,
   изменяемый офисный файл «как есть».
4. Обеспечить квоты, cancellation, timeouts, rate limits, правильные
   настройки SMB-сервисного аккаунта и проверку revocation.
5. Следующий этап сохранения — отдельная новая редакция и аудит
   GUP-2 без любых изменений WORM. `PutFile` до этого отключён.

**Пока эти пункты НЕ реализованы.** Установка сервера с default
DenyAll не позволит Collabora открыть документ — это сознательный
security gate, а не ошибка конфигурации.

## Требования для запуска в защищённой лаборатории

Только на доверенном сервере задаются **переменные окружения**, передаваемые
из корпоративного секрета/хранилища, без значений в Git:

- `GET_WOPI_PUBLIC_ORIGIN` — публичный **HTTPS** origin WOPI, например
  `https://wopi.internal.invalid/` (не URL пользователя и не его ПК).
- `GET_WOPI_HS256_KEY_B64` — секрет минимум 32 байта, Base64.
- `GET_WOPI_PROOF_CURRENT_MODULUS_B64`,
  `GET_WOPI_PROOF_CURRENT_EXPONENT_B64`,
  `GET_WOPI_PROOF_OLD_MODULUS_B64`,
  `GET_WOPI_PROOF_OLD_EXPONENT_B64` — только RSA public keys
  из аутентифицированного и проверенного discovery Collabora.
- TLS-сертификат и привязка HTTPS настраиваются отдельными средствами
  сервера Kestrel/PKI. `X-Forwarded-Host` от неизвестных proxy **не
  доверять**; исходный WOPI origin для proof должен быть именно
  фактическим наружным HTTPS адресом.
- Reverse proxy, ASP.NET logs и трассировка должны **скрывать
  `access_token`** в query; WOPI-tokens нельзя записывать в журналы.
  Host снижает verbosity хостинговых request-start логов, но не может
  управлять внешними прокси или удалёнными агентами.

Приложение `GetDocument.Desktop` **не** содержит ключей, не
реализует протокол WOPI самостоятельно и пока не открывает сервер
Collabora в своей вкладке.

## Тесты

`GetDocument.WopiGateway.Tests` содержит **11 написанных HTTP тестов**:
успешный CheckFileInfo/GetFile через фиктивный авторизованный store,
подпись proof, запрет подмены/неавторизованного пользователя, ACL deny,
получение неправильной редакции, просроченный JWT, неверный origin,
запрос слишком большого файла и запрет отдачи изменённого содержимого.
Кроме того, в Collaboration.Tests добавлен тест на **big-endian**
временную метку по официальной спецификации Microsoft.

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
dotnet test tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj -c Release --no-restore
```

В Windows/Linux scripts добавлены проверки. **`dotnet` SDK в
доступном окружении отсутствует**, поэтому ни restore, ни build, ни
MSTest, ни реальное WOPI/Collabora соединение не запускались.
Требуется тестирование на отдельной закрытой лабораторной машине,
в том числе с официальными WOPI proof vectors, реальной Collabora,
TLS/PKI, AD/SMB ACL, отозванными правами и 100–256 МиБ файлами.

## Лицензии и выбор движка

Окончательный выбор между **LibreOffice UNO**, **Collabora/WOPI**
и **ONLYOFFICE Docs API** не принят. Решение будет основано на
функциональности через **собственный UI ГЭТ**, сохранности
DOCX/XLSX/PPTX, работе в закрытой сети, Windows/Linux,
производительности и допустимости лицензии, а не только простоте
интеграции или наличии протокольного клиента.

Этот PR **Draft**. До локальной сборки и серверного ACL/SSO
компонента не объединять с `main`, GitHub Actions не включать.
