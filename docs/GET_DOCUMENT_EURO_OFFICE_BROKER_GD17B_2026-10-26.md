# GD-17B: Euro-Office в закрытом контуре — launch broker и лабораторная приёмка

**Дата:** 26.10.2026. **Статус:** код + схема стенда + тесты, но Docker / .NET / GUP-2 / офисный сервер физически не запущены. Этот документ не означает готовность к внедрению.

## Реализовано в PR #42

### Граница доверия

```text
GUP-2/GET-system (в будущем: подтверждённая SSO-сессия)
  -> enterprise-only grant issuance (ещё отсутствует)
  -> 128-битный одноразовый launch ID (5 мин максимум)
  -> GET EuroOfficeHost [HTTPS, без внешнего API]
       -> обязательная корпоративная аутентификация
       -> ITrustedEuroOfficeLaunchStore.ConsumeOnceAsync(id, principal)
       -> по умолчанию DenyAllEuroOfficeLaunchStore
       -> EuroOfficeSessionFactory (view-by-default)
       -> CSP-страница DocEditor
  -> один NativeWebView внутри общей вкладки ГЭТ
  -> Euro-Office Document Server (проверенный HTTPS origin)
  -> корпоративное хранилище/ACL + callback/CAS (ещё отсутствуют)
```

**Важное различие:** `GET_EUROOFFICE_JWT_KEY_B64` — секрет подписания и хранится **только в серверном процессе**. Подписанный пользовательский JWT обязательно передаётся странице редактора в её конфигурации, но никогда не попадает в URL, обычные журналы и CLI. Запрет браузерного DevTools не является защитой от администратора устройства.

**Инварианты:**
- Нет учётной записи/SSO/ACL или grant = нет документа. Выданный путь не является полномочием.
- Grant должен быть атомарно списан только один раз, привязан к сотруднику, documentId и revisionId, а также к актуальным правам. Пока такого сервиса нет, стоит DenyAll и сервер ничего не выдаёт.
- Даже доверенный grant может включить запись лишь после внедрения подтверждённого неизменяемого revision sink. WORM всегда read-only; запись исключительно новой редакцией с проверкой CAS, целостности, аудитом и отзывом прав.
- Разрешён только указанный `GET_EUROOFFICE_ORIGIN` с HTTPS и проверкой сертификата; никаких скрытых fallback на другой Document Server.
- CSP позволяет скрипт, iframe и соединения лишь к закреплённому office-origin; JS конфигурация сериализована без HTML-инъекции. Launch-page не кешируется, referrer отключён; дочернее содержимое Office должно пройти отдельную проверку CSP / HTTP.
- Офисный сервер не заменяет Nextcloud Hub / ГЭТ Файлы / GUP-2, не пишет напрямую в WORM и не хранит постоянную корпоративную копию без политики хранения.

### Новые исходники

- `src/GetDocument.Collaboration/EuroOfficeLaunchPage.cs`: генератор запуска, nonce/TTL, CSP, resolver и DenyAll контракт
- `src/GetDocument.EuroOfficeHost/Program.cs`: выделенный ASP.NET Core процесс, `GET /get-document/editor/launch/{32hex}` и `/health/live`
- `tests/GetDocument.Collaboration.Tests/EuroOfficeLaunchPageTests.cs`: HTML/XSS/CSP, срок действия, origin, WORM, отказ неавторизованным, single-use fake store
- `packaging/euro-office/compose.lab.yml`: изолированная конфигурация **только для лаборатории**
- `scripts/verify-euro-office-health.sh`: строгий HTTPS healthcheck через доверенную CA

Не путать `/health/live` нашего брокера с `/healthcheck` сервера Euro-Office: оба проверки процесса, **не** успешного открытия документа, не лицензии, не совместной работы и не записи в GUP-2.

## 1. Лабораторное развёртывание без Интернета

1. На контролируемом компьютере получить официальный пакет/образ Euro-Office и его SBOM/лицензии, зафиксировать точный релиз и SHA256 digest; проверить подлинность. На закрытый сервер переносить подписанный/проверенный файл; ни `:latest`, ни `nightly` для приёмки.
2. Внутри закрытой сети `docker load -i <verified-image>.tar`, сверить `docker image inspect`; не полагаться на сборку из внешних registry в закрытом контуре.
3. Установить `GET_EUROOFFICE_IMAGE` (версия+digest) и случайный лабораторный `GET_EUROOFFICE_LAB_JWT_SECRET` не короче 32 символов **в локальном, защищённом env файле вне git**. Docker env секрет виден пользователям с правами администрирования Docker: лабораторный compose не является production secret manager.
4. Запустить `docker compose -f packaging/euro-office/compose.lab.yml --env-file <private-env> up -d --pull never`. Порт слушает **только 127.0.0.1**. Настроить отдельный внутренний HTTPS reverse proxy и корпоративный CA; запрещено `-k`/отключение TLS validation.
5. Протестировать `GET_EUROOFFICE_ORIGIN=https://office.intra.example/ GET_CORPORATE_CA=/path/to/ca.pem bash scripts/verify-euro-office-health.sh`. Проверка /healthcheck подтверждает только доступность сервиса, а не редактирование.
6. Для реального подключения ГЭТ Файлы/GUP-2 сервер должен читать документы из приватных адресов. В лаборатории `ALLOW_PRIVATE_IP_ADDRESS=false` специально препятствует такому доступу. Разрешать приватные адреса только после ограничения исходящей сети на утверждённые сервисы, запрета метаданных 169.254/16, проверенного DNS, hostname allowlist и SSRF-аудита.
7. Проверить контейнерные тома (данные, ключи, логи) и восстановление. Не включать `EXAMPLE_ENABLED` в корпоративной сети: демонстрационное приложение не является авторизованным ГЭТ UI.

## 2. Конфигурация нашего launch broker

Требует `.NET 10`, внутреннего HTTPS Kestrel или корректно проверенных защищённых proxy headers, и двух **обязательных** переменных:
- `GET_EUROOFFICE_ORIGIN=https://office.intra.example/` — точный доверенный origin; не принимает http, query, логин в URL или произвольный путь.
- `GET_EUROOFFICE_JWT_KEY_B64=<secret>` — отдельный серверный HS256 ключ как base64 >= 32 bytes (он должен соответствовать настройке Euro-Office; в файлы/клиенты/логи не копировать).

```bash
dotnet run --project src/GetDocument.EuroOfficeHost/GetDocument.EuroOfficeHost.csproj
```

**Без реализации корпоративной аутентификации и замены DenyAll все запросы открытия останутся отклонёнными.** Это ожидаемое поведение, не баг. Нельзя подменять DenyAll фиктивными правами или выключать HTTPS/SSO ради запуска редактора на рабочих местах.

## 3. Следующая реализация

1. GUP-2: реализовать выдачу и атомарное одноразовое списание launch grants с серверной проверкой SSO/AD/ACL + отзыв доступа во время сохранения.
2. Доверенный HTTP контент и callback: ограничение размеров/формата, TLS, защита SSRF, проверка JWT подписи callback именно конкретной версии Euro-Office, запрет перенаправлений, антивирус, OOXML ZIP gate, CAS и WORM.
3. Browser glue: передача только 32hex launch URL в `CollaborationLaunchDescriptor`, внутренняя вкладка через `CollaborationEditorPane`; проверить WebView Windows/Linux/Astra, отсутствие другого окна, многомониторность.
4. Office fidelity: реальные DOCX, XLSX, PPTX; сложные стили, формулы, диаграммы, макеты, российские шрифты, PDF экспорт и печать через собственный мастер; 2, 5, 10 пользователей и LAN p95.
5. Собственный UI ГЭТ: брать максимум поддерживаемых возможностей через официальный SDK; недоступные функции не имитировать. Оценить патч Euro-Office web-apps (AGPLv3 и NOTICE) только после API-инвентаризации.

## 4. Верификация

```bash
dotnet restore GetDocument.slnx
dotnet build GetDocument.slnx -c Release --no-restore
dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release --no-restore
bash -n scripts/verify-euro-office-health.sh
```

**Ограничение:** в данной среде нет `.NET 10`, Docker, корпоративных TLS сертификатов, сервера, пользователей AD/GUP-2 и оригиналов Office; сборка, MSTest, аппаратные и интеграционные испытания не производились.

## Официальные источники

- https://euro-office.github.io/documentation/
- https://euro-office.github.io/documentation/installation/docker/
- https://github.com/Euro-Office/documentation/blob/main/docs/configuration/server.md
- https://github.com/Euro-Office/DocumentServer
