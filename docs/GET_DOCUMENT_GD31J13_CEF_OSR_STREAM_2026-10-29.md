# GD-31J.13 — ограниченный поток настоящих CEF OnPaint кадров в лабораторную вкладку ГЭТ

**Дата:** 29.10.2026. **Ветка:** `feature/gd31j13-cef-osr-stream-lab`, поверх GD-31J.12 PR №82. **Только исходники и подготовленные тесты; сборку/запуск/реальные испытания отложили по решению пользователя.** В `main` ничего не попадает до полноценной локальной приёмки, GitHub Actions остаются выключены.

## 1. Что нового по сравнению с GD-31J.12

Предыдущий PR добавил реальный upstream CEF `QCefView::OnPaint`, но сохранял **только первый кадр** и выдавал лабораторную квитанцию в конце 15-секундного сеанса. Теперь добавлен отдельный **строго ограниченный поток нескольких CEF кадров** с прямой возможностью показать их в существующей диагностической вкладке Avalonia, по схеме:

```text
Wayland / Linux + изолированная офлайн-VM + синтетический файл GD-31J.6
     ↓ pinned EuroOffice CEF windowless OnPaint / C++ callback
     ↓ latest-frame-only QImage slot (QMutex; no callback I/O)
     ↓ Qt timer samples latest frame ≤ 2 FPS; ≤ 8 PNG frames
     ↓ nonblocking Linux stdout JSONL, one outstanding packet
     ↓ approved SHA-pinned child Process / .NET finite stream reader
     ↓ validate sessionId, tabId, strict frameNumber, SHA256 and PNG IHDR
     ↓ Avalonia UI Dispatcher, bounded timeout and current-tab check
     ↓ NativeOfficeDiagnosticPane — CEF LAB preview, not office document ACK
```

**Строго не является** реализацией Office editor в production или полноценным live OSR видео. Транспорт передаёт лишь уменьшенное превью кадра (`Qt::KeepAspectRatio` максимум 256×144, максимум 48 KiB PNG, максимум 8 кадров за 15 секунд). Ввод мыши, клавиатура, IME и буфер обмена через данный IPC **не включены**.

## 2. Внешняя поверхность и безопасность

### Новый CMake opt-in

`GETDOC_GD31J13_ENABLE_OSR_STREAM_LAB=OFF` по умолчанию. Для включения необходим предшествующий `GETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB=ON` и все ограничения GD31J3/4/6: закреплённые Git SHA, предварительно собранные офлайн-библиотеки, одобренная изолированная машина, явное `GETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED=ON`. CEF **может иметь `no_sandbox=true`**, флаг лаборатории не заменяет ОС-песочницу.

Новый CLI только для стенда:

```text
--open-staged-wayland-stream-lab <absolute-local-staging>
   gd31j6_writer.docx|gd31j6_calc.xlsx|gd31j6_impress.pptx
   <random-lowercase-session-guid32> <random-lowercase-tab-guid32>
```

Режим отклоняется без отдельной опции CMake и на любой платформе, кроме Linux с действительным `QT_QPA_PLATFORM=wayland` и Qt backend Wayland. Путь к тестовому документу/manifest повторно проверяется нативным кодом, включая SHA-256; доступ к рабочим документам, сети, SSO и WORM не предусмотрен. Тестовые файлы генерируются отдельно и не должны находиться в общей пользовательской папке.

### C++: безопасная ограниченная передача

`cef_staged_open.cpp`:
- существующий upstream `OnPaint` вызывается первым, далее полученный кадр копируется во внутренний QImage; последний кадр заменяет предыдущий без очереди;
- PNG кодируется **в Qt timer**, а не в CEF callback. Максимум 2 FPS, 8 кадров, не более одного пакета в ожидании;
- `stdout` открывается с `O_NONBLOCK` на отдельном лабораторном процессе. `write` поддерживает частичные записи и `EAGAIN`; если клиент не читает, данные не накапливаются. Разорванный pipe приводит к завершению лабораторного сеанса;
- после остановки CEF кадры дописываются с ограничением ожидания, затем единственный `end` receipt с фактическим `framesProduced`. Длительность CEF-пробы остаётся 15 секунд;
- в этом режиме **нет старых текстовых диагностических stdout маркеров**, так как они нарушили бы строгий JSONL протокол.

### Формат JSONL

Каждый `frame` содержит строго:
`command=frame`, `protocol=getdoc-cef-osr-stream/1`, `source=cef-osr-wayland-synthetic-only`, `sessionId`, `tabId`, `frameNumber` начиная с 1, `pixelWidth`, `pixelHeight`, `payloadBase64`, `sha256` и пять безопасных флагов: `laboratoryOnly=true`; `documentVerified=false`, `saveVerified=false`, `pdfVerified=false`, `printApproved=false`.

Завершающий `end` содержит `protocol`, `sessionId`, `tabId`, `framesProduced`, `reason=time-limit`, `laboratoryOnly=true`. Сообщения не содержат содержимого документа вне самого CEF-превью, имени сотрудника, пути исходного файла или сетевых URL. **Сам CEF-кадр потенциально содержит данные отображаемой страницы**, поэтому запуск разрешён лишь на синтетических тестовых файлах в изоляции.

**SHA-256 защищает от случайного повреждения/подмены байт в пути, но не является криптографической аутентификацией процесса.** UUID сам по себе не доказывает источник; C# процесс запускается только из утверждённого каталога с проверкой хеша. Для рабочих данных потребуется полноценная peer authentication, sandbox и проверка происхождения.

### .NET

- `EuroOfficeOsrStreamFrame`: закрытый конструктор, копирование PNG при хранении/выдаче, проверка точной JSON-схемы, ID сеанса и вкладки, монотонных номеров, PNG signature/IHDR/размеров, SHA256. `ApprovedForProduction=false`, печать/сохранение/авторизация документа всегда false.
- `EuroOfficeOsrStreamReader`: предел 65536 символов на JSONL, максимум 8 кадров, ограниченное ожидание следующей строки, требование терминального `end` с точным счётчиком и EOF без дополнительных данных. Ошибка или обрыв — fail-closed.
- `EuroOfficeOsrStreamProcessLab`: реальный `ProcessStartInfo` с закреплённым SHA-256 бинарника (через `EuroOfficeNativeLabHostPolicy`), `UseShellExecute=false`, `RedirectStandardOutput=true`. Linux only, три имени тестовых файлов, запрет reparse/symlink, отдельные GUID, общий timeout 35 секунд, kill/reap при ошибке. **Не включён в пользовательский launcher.**

### Avalonia

- `AvaloniaNativeOsrLabFrameSink`: доставляет кадры только на UI thread через Dispatcher, каждый callback ограничен 5 сек.
- `NativeOfficeDiagnosticPane.SetCefOsrLabFrame`: обновляет картинку внутри существующего `TabControl`; явно отображает **«Реальный CEF OnPaint: кадр N. Только Wayland-лаборатория, документ и сохранение не подтверждены»**. Старый Bitmap освобождается.
- `MainWindow.StartNativeCefOsrLabStreamAsync`: **internal**, с явным `labApproved` и предварительной проверкой исполняемого файла и тестовых файлов. Создаёт только отдельную диагностическую вкладку; нормальный launcher его не вызывает. Закрытие такой вкладки отменяет процесс; UI удаляется **после** освобождения process в `RunAsync`. Проверяется принадлежность кадров текущему tab/session и отсутствие повторных ID; при ошибке выводится состояние хоста Failed, без ложного возврата редакции.

Это лабораторная визуализация, **не** рабочий «ГЭТ Документ» с редактированием, сохранением и печатью.

## 3. Тесты подготовлены, но НЕ выполнялись

- `EuroOfficeOsrStreamTests` — последовательность кадров, EOF, ложные статусы, чужой сеанс, повтор/пропуск frameNumber, SHA, bounds, память, cancellation и дополнительный мусор после `end`.
- `EuroOfficeOsrStreamProcessPolicyTests` — SHA-pinned subprocess, строгий CLI и synthetic-only scope, отказ без labApproved.
- `test_osr_stream_contract.py` — исходные контрактные ограничения CEF callback, неблокирующего stdout, максимума 8 кадров, лабораторного UI и отсутствия документов/печати.
- `scripts/office_acceptance.py` — `cef-osr-stream-source-contract-tests` в будущей локальной приёмке до restore. Реальные Qt/CEF/CMake/Windows/Astra тесты по-прежнему **отложены**.

Ни CEF, ни `.NET`, ни MSTest, ни Python unittest, ни UI-тесты **не запускались**, результаты PASS отсутствуют.

## 4. Критические оставшиеся ограничения

1. **Windows / X11** — в pinned EuroOffice SDK `SetAsChild`, а не Wayland `SetAsWindowless`. Данный поток на Windows/X11 не работает. Встраивание по произвольному HWND/XID не разрешать.
2. **Мышь, клавиатура, IME, clipboard** — upstream имеет нужные методы, но пересылка на настоящий CEF требует новой модели доверенного input IPC, focus/drag/popup/scroll и безопасного отображения. Текущий кадр только для просмотра.
3. **Производительность** — 2 FPS и 8 кадров являются ограниченной диагностикой, не плавным редактором. Для настоящего редактирования потребуется проверенный shared-memory/ring-buffer или иной высокопроизводительный транспорт с OS isolation и backpressure.
4. **Сохранение / совместная работа** — WORM-оригиналы не изменяются; GUP-2/GET-system CAS, Nextcloud и серверный EuroOffice всё ещё в отдельных заглушках.
5. **PDF и печать** — только собственный мастер ГЭТ после полноценной проверки экспортированного PDF; этот поток ничего не печатает.
6. **Лицензии** — проверить AGPLv3 и уведомления в UI, а также лицензии элементов оформления, до формирования установщика.

**Следующий кодовый этап GD-31J.14:** подготовить сессионный input-протокол для мыши и прокрутки без включения клавиатуры/IME/clipboard, а также стратегии для Windows/X11. Проводить реальную лабораторию уже после завершения кодовых этапов, согласно решению пользователя.
