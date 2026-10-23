# GD-09 — совместное редактирование через существующий офисный сервер

**Дата:** 23.10.2026. **Статус:** первый протокольный контракт, а не
запущенная совместная сессия. Зависимость: GD-08 / PR #21, затем этот PR.

## Решение SOURCE-FIRST

Не разрабатывать свои CRDT/OT, WebSocket-редактор, Office-рендерер,
хранение присутствия и конфликтное слияние файлов. Сравнивать два зрелых
локально развёртываемых backend:

1. **ONLYOFFICE Docs**: официальный `DocsAPI.DocEditor` для сеансов
   DOCX/XLSX/PPTX, изменение `document.key` при выпуске новой редакции,
   `editorConfig.callbackUrl` для завершённого сохранения, JWT для
   configuration и callback. Документация:
   https://api.onlyoffice.com/docs/docs-api/usage-api/doceditor
   https://api.onlyoffice.com/docs/docs-api/usage-api/callback-handler
   https://api.onlyoffice.com/docs/docs-api/additional-api/signature/request/token-in-header
2. **Collabora Online**: официальный протокол WOPI,
   `CheckFileInfo`, `GetFile`, `PutFile`, short TTL access token и
   обязательная валидация `X-WOPI-Proof`. Документация:
   https://www.collaboraonline.com/security/
   Провайдер не выбран по умолчанию для выпуска; WOPI host пока НЕ написан.

**Важное требование собственного UI:** оба сервера предоставляют свой
встроенный редактор. Создать оболочку/вкладки ГЭТ вокруг него возможно,
но полноценно заменить Ribbon/Canvas, сохранив 100% функциональности,
через публичные API не подтверждено. Нельзя обещать «полностью свой»
редактор до лабораторной проверки доступности API. Внутренний портал с
родным UI движка не следует выдавать за собственный интерфейс ГЭТ.

**Лицензии:** ONLYOFFICE Docs Community распространяется под AGPL-3.0,
для внутренней организации разрешён самостоятельный intranet-use, но
модификации и производные работы, распространение и удаление фирменных
элементов регулируются лицензией. Для закрытого ГЭТ решения и полного
white-label необходим юридический аудит; НЕ копировать AGPL исходники
редактора в собственную проприетарную оболочку без оценки.
Сервер Collabora/LibreOffice также оценить по реальной редакции, лицензии
и условиям публикации модификаций. Не включать коммерческий SDK по умолчанию.

## Что реализовано в этом PR

- Отдельный **`GetDocument.Collaboration` / `net10.0`**, без сторонних
  NuGet зависимостей: используем встроенные `System.Text.Json`,
  `HMACSHA256`, `CryptographicOperations.FixedTimeEquals`.
- `OnlyOfficeSessionFactory`: получение *уже авторизованных сервером*
  `documentId` / `revisionId` / `userId`, HTTPS ссылок на внутреннее
  хранилище и callback, серверной secret key.
  Формирует подписанный JWT (HS256), конфигурацию для официального
  `DocsAPI.DocEditor`, revision-specific `document.key`,
  `word/cell/slide` и `edit/view` режим.
- Защита **WORM** на уровне конфигурации: `IsWormOriginal = true`
  принудительно устанавливает `permissions.edit = false` и `mode = view`.
  Это НЕ отменяет серверного запрета изменения оригинала.
- `OnlyOfficeCallbackVerifier`: проверяет подписанный JWT,
  `operation = callback`, точный `document.key`, допустимые статусы
  **2/6** (готово к save/forcesave), HTTPS URL скачивания с
  **точно совпадающим, заранее разрешённым origin**.
  Статусы 1/4 не публикуют редакцию. Невалидный подписанный callback
  отклоняется. Ключи не хранятся в UI.
- Результат проверки — **`VerifiedSaveCandidate`**, только данные.
  Прототип **не отправляет HTTP-запрос на URL**, не записывает байты,
  не меняет оригиналы и не считает сохранение завершённым.
  Для этого нужен защищённый внутренний сервис с ACL, проверкой
  пользователя, анти-replay, лимитами, DNS/IP pinning,
  запретом редиректов и подтверждённым получением нового файла.
- Десять новых *написанных*, но **не запущенных** MSTest по DOCX/XLSX/PPTX,
  WORM, подписям, недопустимым URL/состояниям и неизменности session key.
- Локальные Windows/Linux скрипты дополнены этими тестами.
  GitHub Actions не включать.

## Что сознательно остаётся заглушкой

- Нет запущенного ONLYOFFICE Docs или Collabora Online, нет настоящей
  многопользовательской Office-сессии.
- Нет сетевого хостинга страницы `DocEditor`, сервиса `GetFile`/
  `PutFile`, callback-controller, подтверждения личности через AD,
  проверки ACL или TTL/anti-replay. Никакая кнопка редактирования
  у пользователей пока не активируется.
- Нет подтверждённого сохранения результатов в корпоративное хранилище,
  GUP-2 или их аудита; **WORM оригинал никогда не перезаписывается**.
- Нет интеграции WebView в Avalonia: небезопасно выносить серверный JWT
  секрет в клиент. Исполнение браузерного Office редактора в одном окне
  возможно только после наличия доверенного внутреннего backend.
- Нет определения полноты функций через API для замены UI движка.

## Release gate / следующий этап GD-09B

1. Юридически проверить AGPL-3.0 vs LGPL/MPL/MIT и собственную
   проприетарную оболочку с официальным брендбуком.
2. На изолированном сервере выполнить сравнение ONLYOFFICE/Collabora
   на реальных корпоративных DOCX/XLSX/PPTX; зафиксировать
   форматный fidelity round-trip, скорость, память, макросы и доступный
   API для собственного интерфейса.
3. Разработать **серверный** Gateway в закрытой сети с SSO/AD,
   HTTPS/TLS корпоративного PKI, per-document ACL, bounded token TTL,
   trusted host DNS pinning, защищёнными callback и save-новой-редакции.
4. Зафиксировать stable `documentId` (не локальный файловый путь),
   версия `revisionId`; конфликту редактирования назначать новую
   ревизию либо отклонять, без last-write-wins/перезаписи.
5. Проверить WORM GUP-2, возврат новой редакции, контроли SHA-256,
   журнал аудита, бэкапы и восстановление после сетевого сбоя.
6. На Windows/Linux/Astra/ALT/РЕД ОС испытать собственную Avalonia
   оболочку и только после этого подключить реальный редактор во вкладке.

В среде разработки **нет `dotnet` SDK**. Поэтому ни компиляция,
ни MSTest, ни UI, ни серверная проверка не запускались; имеющиеся тесты
являются проверяемой спецификацией, **не доказательством** работоспособности.

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```
Локально: `bash scripts/verify-get-document-linux.sh`
или `.\\scripts\\verify-get-document-windows.ps1`.
