# GD-09E — совместное редактирование в реальном времени

Дата: 24 октября 2026. Предыдущее: #22 ONLYOFFICE; #23 Collabora
WOPI read-only; #26 разбор discovery. Новая ветка — **контракты
многопользовательского сеанса и прототип хранилища редакций**.

## Обязательное поведение

1. Два или более сотрудника с действующим правом **редактирования**
   открывают **один и тот же документ** и в течение сеанса видят
   новые правки, участника и курсор непосредственно в редакторе,
   без нажатия «Обновить» и без циклического скачивания файла.
2. Сотрудники без права записи или с WORM-оригиналом заходят в
   **просмотр** (при поддержке движком — live viewer).
3. Синхронизация операций и курсоров полностью принадлежит
   проверенному штатному серверу Collabora Online или ONLYOFFICE Docs.
   **Мы НЕ создаём собственный OT/CRDT, WebSocket сервер изменений,
   мерж Excel формул либо собственный редактор Office.**
4. Коммит в корпоративное хранилище и WOPI PutFile — НЕ событие
   синхронизации курсоров. Серверный движок синхронизирует изменения
   между пользователями в процессе работы, а файлы сохраняет отдельно.
5. Новая редакция всегда сохраняется **отдельно**; WORM-оригиналы
   остаются побайтно неизменными. Сохранение на устаревшей ревизии
   не считается успешным и не перезаписывает правки коллеги.
6. В общем окне ГЭТ Документ должен быть один набор верхних вкладок,
   а серверный редактор — внутри своей вкладки. Пока такой WebView
   не подключён, кнопка совместной работы в клиенте НЕ включается.

## Что добавлено этим инкрементом

### Сеансы Collabora/WOPI

- `RealtimeCoauthoring.Issue` формирует per-user подписанные WOPI
  токены `aud=get-document-wopi-live`, `doc`, `ver`,
  `room`, `mode=edit/view`, `nbf`, `exp`. TTL 1–15 минут.
- `DocumentKey` вычисляется от стабильного documentId +
  **базовой ревизии редактируемого сеанса** и не зависит от пользователя.
  Таким образом сотрудники видят одну рабочую комнату.
  Токены каждого пользователя индивидуальны; чужой токен не даёт
  присоединиться под другой учётной записью.
- WORM или отсутствие готового **серверного** пути сохранения всегда
  выдаёт **view**, даже при CanEdit=true.
- `WopiRealtimeLaunch` выбирает **edit/view** из доверенного
  discovery Collabora, формирует общий для всех пользователей
  `WOPISrc=https://.../wopi/files/{documentId}`,
  проверяет совпадение идентификатора с билетом.
  `access_token` и `access_token_ttl` передаются **в POST-форме**,
  НЕ попадают в URL. Генерация/исполнение HTML формы и установка
  cookie/token в браузере пока не реализованы.
- `RealtimeCoauthoring.CheckFileInfo` объявляет
  `UserCanWrite=true`, `SupportsUpdate=true`,
  `ReadOnly=false` только при действующем grant и ACL; иначе read-only.
  `SupportsLocks=false` следует WOPI-like схеме Collabora;
  `LastModifiedTime` и `Version` отражают текущую редакцию в
  файловом хранилище. Это НЕ Lock/Unlock интерфейс Microsoft 365.
- `PlanNewRevision` проверяет `X-COOL-WOPI-Timestamp`
  относительно актуального UTC timestamp.
  При отсутствии заголовка (включая попытку Collabora «overwrite»)
  или конфликте выдаёт `WopiVersionConflictException`
  (код `COOLStatusCode=1010` для будущего HTTP 409).
  Никакого silent last-write-wins.

### ONLYOFFICE Fast

- `OnlyOfficeSessionFactory` выставляет
  `editorConfig.coEditing.mode=fast`,
  `coEditing.change=false`, `customization.autosave=true`.
  Fast показывает правки и курсоры других участников сразу,
  Strict откладывает видимость до сохранения.
- Ограничение: по документации ONLYOFFICE настройки coEditing могут
  переопределяться ранее сохранённой клиентской настройкой браузера.
  Нужно испытать поведение версии движка, WebView и пользователя.
  Ни один движок ещё не выбран как основной.

### Отдельные редакции, контроль конфликтов

- `ImmutableOfficeRevisionStore` использует уже существующий
  **выделенный** writable каталог, не создаёт файлы рядом с WORM.
  Первое чтение сохраняет независимый immutable snapshot; новые
  ревизии имеют уникальные UUID, содержимое ограничено 256 МиБ,
  SHA-256 считается потоково, запись во временный файл и move,
  метаданные сохраняются в отдельном mutable индексе.
- `AppendAsync` принимает `expectedRevisionId` и проверяет его
  после взятия межпроцессного `FileShare.None` lock.
  Конфликт не перезаписывает старую редакцию. `ReadCurrent`
  сверяет SHA-256. Клиенту нельзя выдавать `StoredOfficeRevision.FilePath`.
- `RealtimeRevisionCommit.CommitAsync` связывает проверенный
  per-user grant, timestamp редактора и CAS запись новой ревизии.
  Между чтением индекса и записью повторная проверка ожидаемой
  ревизии в хранилище защищает от типичного save race.
- Это файловый **демо-адаптер** для локальных испытаний, а не
  промышленная версия GUP-2 и не юридический WORM/audit: нужны
  кластерные транзакции, устойчивость к сбоям SMB, права AD,
  трассировка и восстановление незавершённых файлов/индекса.

## Источники и разница Collabora vs Microsoft WOPI

- [Collabora SDK](https://sdk.collaboraonline.com/CO-SDK-manual.pdf):
  Collabora использует `LastModifiedTime` и
  `X-COOL-WOPI-Timestamp` для обнаружения внешнего изменения;
  при несоответствии рекомендуется HTTP 409/COOLStatusCode 1010.
  Классические MS-WOPI Lock/RefreshLock не требуются в её
  WOPI-like схеме.
- [ONLYOFFICE Fast/Strict](https://api.onlyoffice.com/docs/docs-api/get-started/how-it-works/co-editing)
  и [параметры конфигурации](https://api.onlyoffice.com/docs/docs-api/usage-api/config/editor)
  — изменения и курсоры в режиме Fast транслирует сервер Office.
- [MS-WOPI coauthoring](https://learn.microsoft.com/en-us/microsoft-365/cloud-storage-partner-program/online/scenarios/coauth):
  состояние общего редактора находится на стороне Office engine;
  периодические PutFile не являются механизмом передачи каждого
  изменения между участниками.

## Необходимый GD-09F для реального запуска

1. Развернуть локально Collabora и ONLYOFFICE (поочерёдно) на
   **сервере ГЭТ**; проверить license/white-label; интернет
   конечным ПК не нужен.
2. Подключить HTTPS WOPI HTTP Gateway:
   `GET /wopi/files/{id}` (CheckFileInfo),
   `GET /wopi/files/{id}/contents` (GetFile),
   `POST /wopi/files/{id}/contents` (PutFile).
   Каждый запрос валидирует `X-WOPI-Proof`,
   подписанный bearer, TTL, ACL/AD, WORM, trusted reverse-proxy,
   fileId, размер и формат. Серверные ключи и физические пути
   не передавать клиенту.
3. Подключить реальный server-side `ImmutableRevisionSink`
   или сервис ревизий GUP-2. Передавать в PutFile только
   **подтверждённую** редакцию. Заложить transactional CAS, журнал
   аудита, неизменность WORM и recovery при разрыве SMB.
4. Открыть HTML-редактор в Avalonia WebView во вкладке ГЭТ,
   проверить на слабых ПК, русском Linux, Windows и офлайн LAN.
5. Измерить 2/5/10 одновременных пользователей, видимость
   текста/формул/слайдов/курсоров, сохранение, откат, конфликт,
   сетевой обрыв, повторный вход и снятие прав. **Цель лаборатории**
   P95 задержки видимости изменений до 2 секунд в корпоративной LAN;
   измерения ещё не проводились.
6. Подтвердить roundtrip fidelity реальных корпоративных DOCX,
   XLSX, PPTX и лицензии для собственного UI.

## Тесты

Добавлено **22 MSTest-метода** для двух пользователей в общем room,
разных grant, WORM, истечения JWT, URL, ONLYOFFICE Fast,
хеширования файлов, изменений версии и конкурентного сохранения.
Тесты добавлены ДО реализации соответствующих модулей, но здесь
**не запускались**: .NET 10 SDK отсутствует. Сборку и тесты
запрещено считать пройденными без локального запуска.

```sh
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
```

PR оставить Draft, GitHub Actions НЕ включать. Перед merge нужно также
проверить весь стэк PR #8–#26 и сценарии Windows/Linux/SMB.
