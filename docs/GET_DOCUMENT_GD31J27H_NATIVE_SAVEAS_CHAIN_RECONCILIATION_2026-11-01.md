# GD-31J.27h — сквозная сверка Save As /1 → /2, независимого OOXML и послеаварийной публикации

**1 ноября 2026 года.** Исходная ветка: `feature/gd31j27g-durable-synthetic-publication-lab`, PR №120. Этот этап — лабораторное согласование кода GD-31J.27a–g, **не выпуск пользовательского Save As**. На основании одного исходного кода не подтверждаются корректность компиляции, работа EuroOffice SDK, надёжность записи или совместимость с Microsoft Office. Пользователь отложил реальные тесты; они не выполнялись.

## Что обнаружено

После GD-31J.27g `NativeSaveAsPublish` использует `linkat` для эксклюзивного имени в `gd31j25-output`. До штатного удаления `QTemporaryDir` один inode одновременно существует под временным и конечным именем, то есть имеет `st_nlink == 2`. При падении после `linkat` и до удаления scratch это состояние сохраняется.

Ранее `native_saveas_orphan_inventory.py` принимал только `st_nlink == 1`, поэтому реальный важный сценарий после аварии просто отбрасывался как неизвестный. И наоборот, временный файл с `st_nlink == 1` при уже существующем, **другом** конечном файле считался подходящим для общего ручного списка — без явного предупреждения о несовпадении inode. Нельзя интерпретировать такие случаи как обычную завершённую публикацию.

Одновременно `synthetic_roundtrip.verify` проверял SHA/ZIP/CRC/canary, но не проверял, что конечный inode уже больше не связан с временным scratch. Это могло позволить квалифицировать ещё не очищенный результат как обычный независимый синтетический кандидат (хотя всё равно **без production прав**).

## Изменения

1. В `native_saveas_orphan_inventory.py` открытие `gd31j25-output` и каждого scratch выполнено через дескрипторы каталогов с `O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC`. Файлы внутри проверяются по `os.stat(..., dir_fd=..., follow_symlinks=False)`, маркеры — по именам относительно открытых дескрипторов. Это ограничивает часть ошибок из-за символьных ссылок и подмены пути, но **не** создаёт полноценную kernel-level sandbox/dirfd-tree attestation.
2. Возраст каталогов рассчитывается по самым свежим **mtime и ctime** файлов и каталога; минимальный возраст остаётся 24 часа. Тесты симулируют ручную проверку через 25 часов без попыток подделать filesystem ctime. Неизвестные имена, типы, writable permissions, необычные hardlink counts и неподходящие маркеры — отказ.
3. Для точно известного synthetic output допускается отдельное состояние `linked_final` **только при совпадении** `st_dev/st_ino/st_size` в private scratch и конечном output, regular-file без group/world writable и ровно двух hardlinks. Это **не подтверждение сохранения**; отдельный счётчик `linkedPublicationPendingReview` подсказывает оператору, что перед ним послеаварийное состояние с неясным исходом.
4. Если конечный файл существует, но **не тот же inode**, либо временный файл имеет ещё посторонние hardlinks, каталог отвергается как неопознанный. Скрипт не удаляет scratch, не удаляет конечный файл, не перезаписывает и не восстанавливает содержимое. `safeToDeleteAutomatically=false` остаётся неизменным. Добавлен отдельный диагностический schema `getdoc-gd31j27h-synthetic-orphan-reconciled-lab/2`, так как контракт расширен полем `linkedPublicationPendingReview`.
5. `synthetic_roundtrip._read_limited` получил необязательный `single_link`-guard. В `verify` обе независимые проверки **только конечного** синтетического OOXML включают `single_link=True`. Если crash оставил второй hardlink, v2 `native_pinned_event_ooxml_join.verify` тоже не сможет выдать положительное `independentOoxmlCorrelated`. После штатной уборки scratch и при неизменном output прежняя положительная проверка остаётся возможной. Исходные fixture/manifest проходят по прежнему алгоритму, чтобы не нарушать /1.
6. В `test_native_saveas_orphan_inventory.py` добавлена проверка `linkat`-послеаварийного состояния, посторонних hardlinks и несовпадающего конечного inode. В `test_synthetic_roundtrip.py` и `test_native_pinned_event_ooxml_join.py` добавлены отрицательные сценарии, когда SHA и canary верны, но output всё ещё имеет вторую ссылку.
7. В `test_native_saveas_chain_reconciliation.py` собран **единый read-only source contract**: одинаковые три fixture/basename в C++ и Python; строгие типы /1, pinned /2, read-only join, pinned supervisor, запрет downgrade, `fsync/linkat`, маркировка ручной проверки и `nativeSaveProven/documentVerified/saveVerified/approvedForProduction=false` во всех слоях. Шаг добавлен в `scripts/office_acceptance.py` до restore и в `scripts/tests/test_office_acceptance.py`. Реальное завершение, SDK library hashes, fork/CEF callbacks и лабораторная консистентность требуют отдельного ручного gate `eurooffice-gd31j27h-v1-v2-native-ooxml-crash-lineage-e2e`.

## Протоколы не смешиваются

| Ступень | Схема и основание | Что она НЕ удостоверяет |
|---|---|---|
| Native legacy v1 | `getdoc-native-saveas-candidate-lab/1` — только первоначальная лабораторная квитанция | Источник байтов / durability / production |
| Pinned native v2 | `getdoc-native-saveas-pinned-event-lab/2`, вложенный v1, счётчики/serial callback | Реальную аутентичность SDK callback, юридическое подтверждение сохранения |
| Independent OOXML | `getdoc-native-pinned-v2-ooxml-joined-lab/1`, SHA/ZIP/CRC/canary и теперь single-link output | Происхождение editor, совместное редактирование и бизнес-авторизацию |
| Pinned process supervisor | `getdoc-native-pinned-v2-supervised-ooxml-lab/1`, SHA бинарника/2 runtime libraries и только успешный bounded child stdout | Фактический состав загруженных библиотек, OS sandbox |
| Crash inventory | `getdoc-gd31j27h-synthetic-orphan-reconciled-lab/2` — **только диагностика старых synthetic scratch** | Разрешение на удаление, откат, доверенное подтверждение `fsync` или запись |

Нет automatic v1→v2 downgrade, нет попыток автоматически «восстанавливать» пользовательские документы. Данные GUP-2, WORM, основная база и сеть GET-system вне области правок.

## Реальные блокеры перед финальной интеграцией

- **Сборка:** компиляция C++ в pinned EuroOffice SDK, Python unit, MSTest, местные источники пакетов, CMake и Linux/Wayland остаются неиспытанными. Ни один тест не объявляется пройденным.
- **Подлинность исполнения:** callback serial/SHA 2 библиотек — самоотчёт/локальный контроль, не runtime attestation. Не проверены `/proc/<pid>/maps`, связанные Qt/CEF .so и перехват системных вызовов.
- **Гонки путей:** `linkat` по имени источника после проверки fd сохраняет потенциальную TOCTOU-гонку с процессом той же UID, меняющим private entry. Эту архитектуру нельзя переносить в production без защиты каталога от конкурентных writers и физической fault-injection проверки. `fsync` сам по себе не доказывает hardware power-loss persistence.
- **Процесс публикации:** orphan inventory не доказывает корректность output и намеренно не открывает содержимое рабочего документа. `linkedPublicationPendingReview` означает неопределённость, а не «сохранено».
- **Старый v1:** изменения общего `native_saveas_policy.h` и порядка shutdown в PR №119 должны проходить отдельные регрессионные VM-сценарии.
- **Релиз:** пользовательские DOCX/XLSX/PPTX, совместное редактирование через EuroOffice, PDF, печать, GUP-2 и WORM пока не подключены к этой лабораторной цепочке. Не объединять Draft PR в main и не включать GitHub Actions до реальных проверок.

**Следующий шаг GD-31J.27i:** подготовка одного приемочного checklist/feature-gate решения и безопасной fault-injection методики на disposable VM, выбор единого pinned native протокола после стенда, без расширения полномочий лабораторных квитанций.
