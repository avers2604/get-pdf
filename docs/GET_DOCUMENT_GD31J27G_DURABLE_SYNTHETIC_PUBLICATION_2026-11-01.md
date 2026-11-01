# GD-31J.27g — безопасная публикация ТОЛЬКО синтетического Save As: fsync/no-replace, последствия сбоя и инвентаризация временных каталогов

**1 ноября 2026 г.** Базовая ветка: `feature/gd31j27f-bounded-native-saveas-teardown-lab`, Draft PR #119. Ветка GD31J27g затрагивает исключительно пробный Linux/Wayland CEF SaveAs, НЕ основной пользовательский редактор.

**Статус:** исходники, тесты и ручные acceptance gates подготовлены. Компиляция C++, Python-юнит-тесты, MSTest, работа EuroOffice/CEF, fault injection и эксперимент на изолированной VM **не запускались**. Ни одно условие приёмки не объявляется выполненным.

## Проблема существующего кода

`native_saveas_policy.h` (GD31J26) делал `link(source, destination)` и повторную сверку хешей. `link` уже является no-replace, однако отсутствовали `fsync` исходного inode и итогового каталога. При ошибке хеширования после создания конечной ссылки код делал `unlink(destination)`: между проверкой и удалением конкурентный процесс мог заменить объект, что создавало риск удаления неизвестного файла. `QTemporaryDir` удаляет собственный scratch при нормальном выходе, но при SIGKILL/power loss каталоги могут оставаться; по префиксу нельзя безусловно считать их своими.

## Публикация: новый native_saveas_durable.h

Введена только Linux-реализация `Gd31J27G::DurableExclusivePublish`, вызываемая из `NativeSaveAsPublish` **только после закрытия CEF** и прохождения существующих callback, source SHA и проверки синтетического формата.

1. Точные исходный и конечный filenames выбираются из allowlist `gd31j25_{writer,calc,impress}_saved.{docx,xlsx,pptx}`. Оба каталога открываются с `O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC`; возвращённые дескрипторы не наследуются дочерними процессами (`F_DUPFD_CLOEXEC`). Режимы group/world writable запрещены, `st_dev` каталогов обязан совпадать. Никакого cross-filesystem копирования.
2. `PrepareSyntheticShadow` создаёт скрытый случайный scratch `gd31j26-private-XXXXXX` с точным маркером `gd31j27g-private-owner.marker`, содержание — `GETDOC_GD31J27G_PRIVATE_SCRATCH_ONLY\n`. Маркер создаётся **только через O_CREAT|O_EXCL|O_NOFOLLOW** и синхронизируется с scratch каталогом. Родительский approved output каталог синхронизируется отдельно. Перед публикацией повторно проверяются и этот маркер, и прежний `gd31j25-output-lab.marker`, через открытые каталоги, без перехода по символьным ссылкам.
3. Native output — обязательный регулярный файл 1–32 MiB (фактически нижняя граница строго >0), один hardlink, группа/прочие не могут записывать. Открывается `openat(O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)`; `fstat` проверяет тип, размер, inode. Контент потоково хешируется из **уже открытого fd**, сравнивается с ранее измеренным SHA-256, проверяются неизменные inode/size/mtime/ctime. Дальше `fsync(outputFileFd)`.
4. Перед публикацией повторно сравниваются inode для fd и имени в private каталоге через `fstatat(AT_SYMLINK_NOFOLLOW)`. Создание конечного имени — `linkat(privateDirFd, exactBasename, approvedOutputDirFd, exactBasename, 0)`. **EEXIST → отказ**, без rename/copy/replace/retry, конечный файл не затрагивается.
5. После `linkat` `fstatat` проверяет идентичность конечного inode, затем `fsync(approvedOutputDirFd)`. Только после этого выполняются старые контрольные SHA-256 конечного имени и исходного fixture, выдаётся синтетический `publishedSha`.
6. **Важная семантика ошибки:** после успешного `linkat`, но неуспешного `fstatat/fsync/проверки SHA`, возвращается отказ. Конечное имя МОЖЕТ существовать, его долговечность неизвестна. **Автоматический `unlink` удалён.** Последующая попытка отвергается благодаря no-replace; устранить неоднозначность может только ручная проверка. Приложение не заявляет успешное сохранение по этому отказу.
7. Ошибка `fsync` — это отказ от подтверждения, но не гарантированный откат транзакции. Даже успешный `fsync` на синтетической VM не является доказательством аппаратной durability, поддержкой сетевых FS или корпоративного WORM. Проверка файловой системы и сценарии выключения питания остаются частью отдельной реальной лаборатории.

## Послеаварийная инвентаризация БЕЗ удаления

`native_saveas_orphan_inventory.py` — READ-ONLY, без background cron, обхода корпоративных каталогов, удаления или восстановления файла.

- Единственный аргумент — канонический абсолютный approved synthetic staging; required marker output root сверяется побайтно. Файлы за пределами `gd31j25-output` не анализируются.
- Рассматриваются исключительно подкаталоги формы `gd31j26-private-[A-Za-z0-9]{6}` с отдельным owner-marker. Отсутствующий/неподходящий marker, symlink, чужой тип, лишний файл или неизвестное имя, group/world writable и более свежие каталоги отвергаются.
- Требуется возраст **не менее 24 часов**; возможны ровно известный shadow fixture и соответствующий native synthetic output, либо marker-only каталог после раннего сбоя.
- Выводятся только имена подходящих scratch-каталогов и счётчики отказов. **`safeToDeleteAutomatically=false` всегда.** Сам скрипт не вызывает `unlink/rmdir/rmtree`. Итоговый `gd31j25_*_saved` файл в output root **никогда** не классифицируется как временный.
- Подделка маркера технически возможна для привилегированного процесса. Поэтому скрипт не даёт разрешения на удаление; действия по cleanup — отдельный будущий протокол, только после реального теста.

Команда для будущей отдельной офлайн disposable VM:
```sh
python3 -B probes/eurooffice-desktop/cef_lab/native_saveas_orphan_inventory.py \
  --staging /ABSOLUTE/APPROVED/GD31J6-STAGING \
  --minimum-age-hours 24
```

## Отложенные проверки

| Сценарий | Ожидание |
|---|---|
| Действительный synthetic DOCX/XLSX/PPTX, пустой destination | Один hardlink, совпадение inode+SHA, успешный file и directory fsync |
| Destination уже существует, включая symlink | `linkat: EEXIST`, без перезаписи и удаления |
| Разные файловые системы private/final | Отказ без fallback copy |
| Неверный output/private marker | Отказ до `linkat` |
| Подмена inode во время хеширования/публикации | Отказ; возможный уже опубликованный путь не удаляется |
| Ошибка file fsync | Нет `linkat`, отказ |
| Ошибка directory fsync после link | Отказ, состояние «неизвестно»; никакого rollback `unlink` |
| SIGKILL после создания scratch marker | Только read-only orphan inventory; без автоматической очистки |
| Неизвестный пользовательский файл, симлинк или повреждённый marker в scratch | Каталог отвергнут и НЕ удаляется |
| Второй вызов после неясного исхода | Не перезаписывает конечный объект |
| Повторный запуск старого native `/1` | Тот же отдельный stdout-протокол, регрессию проверить на реальной VM |
| GUP-2/WORM/SMB/corporate files | Никаких операций |

Новые тестовые файлы `test_native_saveas_durable_policy_contract.py` и `test_native_saveas_orphan_inventory.py` зарегистрированы в `scripts/office_acceptance.py` до restore с ручным gate `eurooffice-gd31j27g-fsync-no-replace-crash-recovery-e2e`. Проверка регистрации добавлена в `scripts/tests/test_office_acceptance.py`.

## Границы и следующий этап

Весь GD31J27g закрыт для пользовательских файлов. Доказаны лишь свойства предусмотренного **исходного кода**, не исполнение на реальном SDK. Следующий GD31J27h: review atomicity faults/cleanup under VM, проверка хуков SDK и консолидация GD31J27a–g; после пользовательского разрешения на стендовые испытания выбрать единственный native SaveAs протокол и начать настоящие тесты. Ни один PR в main не объединён; GitHub Actions выключены.
