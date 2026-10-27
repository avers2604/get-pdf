# GD-12 — защищённый OOXML, Collabora/ONLYOFFICE во вкладках одного окна

**Дата:** 25.10.2026. **Base:** Draft PR #31 «ГЭТ Документ» (консолидированный UI).
Новые изменения оформлены отдельным **Draft PR**, без merge в main.

## Реальная функциональность

1. **SOURCE-FIRST:** обновлены существующие WOPI/ACL части из PR #29,
   а не написан второй backend. На консолидированную ветку перенесены:
   `RevisionBackedWopiDocumentStore`, `IEnterpriseDocumentPolicy`,
   `WopiHostMode`, исправления `WopiLiveHttpGateway`, стартовая конфигурация
   `GetDocument.WopiHost` и регрессионные MSTest. Ветка #31 самостоятельно
   не включала эти последние улучшения.
2. **OfficePackageGuard** добавлен в существующий `GetDocument.Collaboration`
   без NuGet-библиотек. Проводит безопасную серверную проверку ZIP,
   перед тем как `RevisionBackedWopiDocumentStore.CommitNewRevisionAsync`
   создаст новую редакцию.
3. **WopiLiveHttpGateway** отклоняет такие файлы через HTTP 422;
   никакая новая версия не публикуется. Для WORM/отзыва ACL и
   конкурентных изменений сохраняются правила предыдущих PR.
4. **Avalonia.Controls.WebView 12.1.1**, MIT (официальный OSS-проект),
   добавлен в `GetDocument.Desktop`. `CollaborationEditorPane` держит
   `NativeWebView` внутри ОБЫЧНОЙ верхней вкладки общего окна,
   отменяет выход на недоверенные top-level origins и создание
   дополнительных окон, отключает DevTools.
5. **CollaborationLaunchDescriptor** в `GetDocument.Core` требует
   два заранее заданных HTTPS origin (broker и Office), отличающихся друг
   от друга, а также точную ссылку вида
   `/get-document/editor/launch/{128-bit random id}` без userinfo,
   query, fragment и access_token. В офисном браузере разрешает
   только HTTPS office-origin без токенов/JWT в параметрах навигации.
6. В лаунчере ГЭТ добавлена доступная пользователю команда
   «Совместное редактирование» с явным сообщением о неготовом сервере;
   реально открыть внутреннюю вкладку может только будущий доверенный
   вызов `MainWindow.OpenCollaborationSession` (internal).
   Параметры URL/файлы, поступившие по обычной командной строке
   и локальному IPC, **не могут включить** web editor.

## Что проверяется в OfficePackageGuard

- Максимальный ZIP: 32 МиБ (тот же предел live WOPI HTTP body).
- Максимум 4096 entries, 128 МиБ uncompressed per entry,
  256 МиБ uncompressed total, compression ratio <= 500 (для больших частей).
- Проверка фактически распакованных байтов (не только объявленных length).
- Отказ на absolute/traversal/backslash пути, повторных part names
  независимо от регистра, ZIP symlink.
- Запрет DTD/XXE для XML/`.rels`, ограничение XML-парсера.
- Сверка основного OOXML-part:
  DOCX = `word/document.xml`, XLSX = `xl/workbook.xml`,
  PPTX = `ppt/presentation.xml` и наличие `[Content_Types].xml`.
- Отказ для частей VBA/`activeX`, чтобы неподписанное активное
  содержимое не сохранялось как обычный DOCX/XLSX/PPTX.
- Поток не записывается и его позиция восстанавливается.

Это **промежуточный ZIP/XXE контроль**, а не полноценная проверка
OOXML по ISO 29500 и не антивирус; не проверены вся сеть relationship
ссылок, формулы, объекты, внешние связи, цифровые подписи/КЭП.
Дальнейший security gate: AV, структура [Content_Types]/relationships,
контроль ZIP64, размерность/CPU на реальных файлаx, сохранение MS Office fidelity.

## Режим real-time

**Передача правок и курсоров** целиком внутри готового
офисного сервера Collabora Online (возможно ONLYOFFICE Fast),
не в WOPI PutFile и не в UI-шлюзе ГЭТ. У каждого сотрудника
свой подписанный access token; общий редактор определяется одним
WOPISrc/documentId+room key. Сервер сохраняет полный файл
периодически в отдельную immutable revision; WORM original не меняется.

Появление настоящего `NativeWebView` — это только GUI-host,
**не утверждение о готовом совместном редактировании**.
Нужен серверный одноразовый editor launch broker с реальным SSO:
проверка текущего GUP-2/AD ACL, mint/consume одноразового запуска
на сервере, POST `access_token`/TTL в iframe/editor (не в URL),
ограничение redirect/CSP, подтверждение отклика редактора.
`CollaborationLaunchDescriptor` НЕ удостоверяет сотрудника и
НЕ выдаёт WOPI токен.

**Production fail-closed:** `GET_WOPI_HTTP_MODE=live` всё ещё
отклоняется, потому что в `Program.cs` нет реального GUP-2 ACL +
WORM/audit провайдера. Нельзя заменять его тестовым FakePolicy
или включать только переменной окружения.

## OSS / установка

`Avalonia.Controls.WebView` 12.1.1 — MIT:
https://github.com/AvaloniaUI/Avalonia.Controls.WebView/releases/tag/12.1.1

Официальный NativeWebView:
https://docs.avaloniaui.net/docs/app-development/embedding-web-content

- Windows 10: проверить наличие/установку корпоративного WebView2 Runtime.
- Windows 11: использовать установленный WebView2 при наличии.
- Astra/ALT/РЕД Linux: `NativeWebView` требует WPE WebKit
  совместимого с дистрибутивом и его лицензированную offline-поставку.
  **Не заменять** на отдельное browser window, потому что
  требование — одно окно. Если WPE недоступен, показывать ошибку/placeholder.
- Профили WebView: на Windows WebView2 включён `InPrivate`,
  на GTK — `EphemeralDataManager`, на macOS — `NonPersistentDataStore`.
  **Linux WPE пока не подтверждает полностью эфемерное хранилище**
  в доступном публичном API. До проверки очистки WPE cache/cookies
  и политики ИБ не разрешать реальные конфиденциальные редакции.
  `NavigationStarted` ограничивает top-level URL, но не является
  полноценным межсетевым экраном для subresources: egress должен
  контролироваться в закрытом корпоративном сегменте.
- Серверы Collabora/ONLYOFFICE работают внутри закрытой сети.
  Никакой публичной CDN, telemetry или внешнего доступа.

**UI caveat:** NativeWebView показывает готовый Office canvas/UI.
Собственная фирменная оболочка ГЭТ окружает его, но полный
white-label Office не доказан через публичные API; уточнить лицензию
движка до production. MIT WebView не снимает лицензии с Office Server.

## Тесты и release gate

Добавлены MSTest спецификации для ZIP/XXE/traversal/bomb/format/ActiveX,
неизменности исходных ревизий при ошибке, HTTP 422, HTTPS launch
origin/nonce и запрета URL bearer/new windows. Тесты **не запускались**
в доступной среде без .NET 10 SDK. Отдельно требуется проверить GUI
Windows/Linux/WebView и 2/5/10 пользователей.

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
dotnet test tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj -c Release --no-restore
dotnet test tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj -c Release --no-restore
```

**НЕ объявлять готовый релиз.** GitHub Actions остаются отключены.
PR Draft до компиляции, кроссплатформенного NativeWebView smoke,
безопасного GUP-2 broker/ACL и настоящей multiuser Collabora
интеграции с серверным журналом аудита.
