# GD-31J.27i — единая офлайн-приёмка pinned EuroOffice native Save As

**Дата:** 2026-11-01. **База:** GD-31J.27h / Draft PR #121. **Статус:** исходный код, ручная матрица и тестовые контракты подготовлены, тесты/сборка/реальная VM не запускались. Этот документ НЕ является заключением о готовности.

## Принятый архитектурный режим

Для будущей одноразовой Linux/Wayland лаборатории кандидатом выбран **только** `getdoc-native-saveas-pinned-event-lab/2` (ручная кнопка, атомарные счётчики, pinned host и runtime, последующая read-only OOXML проверка). Это **план тестирования, а не выбор готового production Save As**. Legacy `getdoc-native-saveas-candidate-lab/1` остаётся изолированным регрессионным случаем, не fallback и не источник прав. Решение о единственной итоговой архитектуре — только после настоящего SDK/CEF стенда.

Нельзя использовать реальные документы, WORM, SMB, доверенную корпоративную сеть или рабочие секреты. Весь объект временного хранилища маркирован как disposable/synthetic и не используется для последующего возврата в ГЭТ Документ.

## Новое средство: строго read-only evidence gate

`probes/eurooffice-desktop/cef_lab/native_saveas_acceptance_gate.py`

- `--plan`: вывод **машинно-читаемого JSON** с 18 обязательными случаями `not-run` и 14 manual E2E gates из единого каталога `scripts/office_acceptance.py`. Не выполняет команды и не создаёт файлы.
- `--evaluate --automated-report PATH --manual-evidence PATH`: читает два заранее созданных локальных JSON. Два файла должны быть regular single-link, без symlink в цепочке, не group/world writable, размером не более 64 КиБ и не меняться при чтении.
- Автоматический отчёт обязан быть из `GD-31I`, host Linux, exact review Git revision SHA-40, `automatedChecksPassed=true`, `status=automated-checks-passed`, `releaseReady=false`. Все требуемые offline steps должны быть уникальны, завершены с `status=passed`, `exitCode=0`. Встроенный ручной каталог `manualAcceptance` должен целиком оставаться `not-run`: реальный `GD-31I` не умеет удостоверять ручной VM E2E.
- Операторский JSON `getdoc-gd31j27i-native-saveas-manual-evidence-lab/1` обязан соответствовать **точной** схеме. `revision` — тот же SHA-40, `protocol=getdoc-native-saveas-pinned-event-lab/2`, `fixtureNames` — ровно три синтетических DOCX/XLSX/PPTX, `isolatedVm` содержит четыре явных `true` (offline, disposable, corporateMountsAbsent, privilegedSecretsAbsent). `cases` — ровно 18 ключей.
- Каждое `cases[...]`: `status=not-run|passed|failed|blocked`, `evidenceSha256` — пустой для неуспешного случая, ровно 64 lowercase hex для `passed`, `reviewedByOperator` — boolean `true` только для `passed`. Хеши **только связывают локальные доказательные материалы с записью**. Модуль не открывает содержимое DOCX или файлов доказательств и не проверяет сами SHA-256 на диске. Самоотчёт оператора не есть доверенная аттестация.
- JSON с повторными, неизвестными или неполными полями, неправильной ревизией, подменённым протоколом, отсутствием исходных offline steps и заявленными производственными полномочиями отвергается.
- Для `not-run/failed/blocked` хотя бы в одном ручном кейсе выдаётся `reviewState=blocked` и список имён проверок, требующих внимания. Если **все** 18 помечены `passed`, выдаётся **только** `reviewState=requires-independent-review`, но НЕ `approved`, НЕ `releaseReady`.
- `nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `releaseReady=false`, `approvedForProduction=false`, `writePermissionGranted=false`, `safeToDeleteAutomatically=false` — **неизменные** значения при любом успешном разборе входных JSON. В режиме `--evaluate` процесс всегда завершится кодом **2**; это принципиальная защита от использования как CI/release gate, который по ошибке сочтёт exit code 0 разрешением.
- Никаких инструментов тестирования, `subprocess`, browser, backup, delete, cleanup, запись JSON на диск, cron или production Save As модуль не запускает. `--plan` не требует файловых доказательств и возвращает 0, обозначая лишь успешную выдачу плана.

### Команды для будущей ручной проверки — не запускают тесты

```sh
python3 -B probes/eurooffice-desktop/cef_lab/native_saveas_acceptance_gate.py --plan

python3 -B probes/eurooffice-desktop/cef_lab/native_saveas_acceptance_gate.py \
  --evaluate \
  --automated-report /ABSOLUTE/REVIEWED/gd31i-local-acceptance.json \
  --manual-evidence /ABSOLUTE/REVIEWED/gd31j27i-manual-evidence.json
```

Два JSON файла на стенде предварительно создаются **отдельным утверждённым оператором**, программа их не создаёт и не изменяет. Ни один режим не выдаёт разрешение к merge или работе с корпоративными документами.

## Матрица 18 отдельных будущих испытаний

| № | Тестовый случай | Основание для ручного принятия |
|---|---|---|
| 1 | offline-vm-and-no-corporate-mounts | Изолированный VM snapshot, физически отключённая сеть, нет SMB/WORM/секретов, отдельный disposable user |
| 2 | pinned-native-executable-and-runtime-sha | Exact reviewed native host+2 runtime SHA before/after; собрать список всех фактических Qt/CEF loaded modules |
| 3–5 | docx/xlsx/pptx-independent-ooxml-zip-crc-canary | Отдельно каждый формат, ZIP/CRC, содержательные отношения, SHA source/output, synthetic canary, неизменный исходник |
| 6 | early-and-duplicate-cef-callbacks | Ранние/повторные CEF SaveAs, отмена, неверный sender, повторный OnSave → deny |
| 7 | callback-storm-overflow-and-teardown | 64/64/128 budget, overflow deny, concurrent callback при detach/DestroyCefView, no UAF |
| 8 | existing-final-never-overwritten | Уже существующая цель/symlink → EEXIST/deny, сравнение inode/content до и после |
| 9 | source-name-swap-during-linkat | Контролируемая гонка проверки fd и `linkat` в disposable VM; **до устранения и доказательства отказа запрещать production путь** |
| 10 | scratch-and-final-on-different-filesystems | Cross-device → deny, нет fallback copy |
| 11 | file-fsync-denied-before-linkat | Отказ file fsync → финальное имя не появляется |
| 12 | directory-fsync-denied-after-linkat | Отказ directory fsync → UNKNOWN outcome, не удалить и не перезаписать final |
| 13 | process-killed-before-linkat | Kill строго одноразового VM процесса до hardlink, остаются только собственные marker/scratch |
| 14 | process-killed-after-linkat-before-dir-fsync | Неясный результат, final не удаляется; orphan inventory показывает linked manual review |
| 15 | process-killed-after-dir-fsync-before-scratch-cleanup | Link count=2, называть candidate только после подтверждённой очистки + независимого verify |
| 16 | orphan-hardlink-inode-manual-review-only | Ровно dev/ino/size и собственные marker; неизменно `safeToDeleteAutomatically=false` |
| 17 | unrecognized-files-symlinks-never-deleted | Подложный marker, чужой документ/ссылка/другой final inode → строго no-delete |
| 18 | v1-regression-and-no-v2-downgrade | Старый pinned /1 отдельно; поддельная /1 квитанция не принимается как authenticated /2 |

**Никаких сценариев инъекции ошибок на рабочих ПК.** Команды для генерации отказа `fsync`, убийства процесса и TOCTOU здесь намеренно НЕ приводятся: конкретный способ и точки должны быть утверждены после сборки и инвентаризации disposable VM. Ошибочная fault injection может повредить файловую систему, поэтому выполняется только после снимка VM и проверки изоляции.

## Блокирующие вопросы без автоматического решения

1. В `native_saveas_durable.h` ещё остаётся гонка между fd-верификацией и `linkat(scratchDirFd, имя, ...)`. Проверка конечного inode отсекает ошибочный успех, **но сама операция уже могла создать чужой файл**. До безопасной fd-bound публикации или иных OS-гарантий исключения конкурентного writer эту реализацию нельзя считать готовой к пользовательскому Save As.
2. Hash локального native host и ровно двух библиотек не доказывает фактический список загруженных модулей Qt/CEF/Chromium и не заменяет OS/VM sandbox, код подписи и лицензионную проверку.
3. `OnSave`, `content-ready`, `modified` и два секунды тишины — диагностические события, не доказательство fsync конечного файла или корректности изменений в MS Office.
4. Контрольные файлы `GD31J25` имеют ограниченный размер, собственные маркеры и права; код не реализует транзакционное сохранение пользовательских файлов и не включает возвращение редакции в GUP-2, WORM или СЭД.
5. **Ни один** тест стадии GD31J27i фактически не выполнен. Выполнение тестов, SDK E2E, физическая приёмка и последующая ревизия протокола остаются отдельной следующей задачей после допуска к стенду.

## Область изменений и следующий этап

Файлы: `native_saveas_acceptance_gate.py`, тестовый модуль и source contract, `scripts/office_acceptance.py`, тест плана и эта документация. Изменений `main`, GitHub Actions, пользовательского GUI, Nextcloud, печати и WORM нет.

**Следующий этап GD-31J.27j:** устранение известной TOCTOU гонки при `linkat` с fd-bound источником и пересмотр безопасности владельца scratch. Лишь после офлайн Linux/Wayland стенда выбирать окончательную пользовательскую реализацию.
