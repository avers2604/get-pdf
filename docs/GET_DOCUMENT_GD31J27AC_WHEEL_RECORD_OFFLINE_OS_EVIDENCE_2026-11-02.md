# GD-31J.27ac — проверка wheel RECORD и внешних offline OS evidence Astra Linux

**Дата:** 2 ноября 2026 года. **База:** Draft PR №141 / GD31J27ab. **Статус:** исходники и отложенные тесты, без реальных локальных тестов, Python/C++ сборки, VM, systemd, доступа к корпоративным файлам, выполнения SCM_RIGHTS и worker SaveAs.

## Исправляемый пробел

Подписанный офлайн-каталог GD31J27ab содержит список wheel/ELF и эффективных параметров systemd, но **файл wheel может содержать изменённый отдельный модуль при корректном имени**, а сам список ОС может быть неполным или получен из недоверенной среды. Нужны **раздельные** проверки содержимого архива и сопоставления внешних наблюдений.

Модуль `native_saveas_offline_vm_evidence.py` предназначен для последующего использования **только на отдельной disposable офлайн-VM Astra Linux** как чистая функция над переданными байтами. Он **сам не получает** данные из `systemctl`, `/proc`, `readelf`, `ldd`, `pip`, сети, контейнеров или реальной файловой системы и **не устанавливает** wheel.

### 1. Проверка wheel и RECORD

`inspect_wheel_record(archive_bytes,expected_filename,expected_sha256)`:

- Проверяет SHA-256 **всего ZIP-архива** против значения из подписанного SBOM.
- Принимает только ограниченный размер архива (16 MiB), не более 256 элементов и суммарно ≤8 MiB распакованных данных; разрешены ZIP_STORED и ZIP_DEFLATED, но не зашифрованные элементы, каталоги, символьные ссылки, опасные/абсолютные пути, повторяющиеся имена или другие типы файлов.
- Требует точный `<distribution>-<version>.dist-info/{WHEEL,METADATA,RECORD}`. `RECORD` разбирается CSV-парсером; каждый файл (кроме самого RECORD) обязан присутствовать в RECORD **ровно один раз**, иметь формат `sha256=<base64url>`, точный байтовый размер и совпадающий SHA-256. Сам RECORD имеет пустые поля digest/size. Лишние ZIP entries, недостающие или повторные RECORD-строки → отказ.
- Не извлекает файлы на диск и никогда не вызывает импорт Python-модулей из проверенного архива.

Итог может содержать `wheelRecordObserved=true`, но `dependencyClosureAttested=false` и `releaseReady=false`.

**Ограничение:** RECORD подтверждает соответствие предоставленного архива **его же** содержимому лишь в сочетании с независимо доверенным архивным SHA. Он не подтверждает происхождение wheel, безопасность C extensions, отсутствие скрытых runtime загрузок, корректность установки в конкретную среду, `RECORD` установленного пакета или `pip` dependency resolution.

### 2. Сверка внешнего снимка ОС

`inspect_external_snapshot(catalog,snapshot,expected_processes)` требует строго определённый документ:

- `schema=getdoc-gd31j27ac-offline-external-os-snapshot-untrusted/1`, `mode=external-offline-observation-untrusted`, без дополнительных или разрешающих флагов.
- Exact `wheels` и `loadedElf` (сортированный список) равны объявленному подписанному каталогу; `elfNeeded` совпадает с объявленными связями.
- `effectiveUnits` обеих ролей broker/worker полностью совпадают с прежней строго **заблокированной** unit-policy GD31J27ab: `ExecStart=/usr/bin/false`, `NoNewPrivileges`, `PrivateNetwork`, отсутствие privileges, `UnitFileState=disabled`, пустые DropInPaths.
- `processes`: точные `pid/startTicks/uid/gid` broker и worker, сверенные с **отдельно предоставленными expected pins**. Два процесса должны иметь разные PID и непривилегированные UID.

Внешний снимок **нельзя считать достоверным из-за поля source или успешного сравнения**. Он мог быть предоставлен самим проверяемым процессом и не содержит доказательства происхождения от доверенного ядра. Точные совпадения означают только `externalFactsCompared=true`, всегда `osEvidenceAttested=false`, `effectiveUnitAttested=false`.

Отдельно в будущем требуется доказать, что `loadedElf` действительно собраны из независимого отображения `/proc/<pid>/maps`, `elfNeeded` соответствует реальным `DT_NEEDED`, параметры `systemd` не подменены и нет дополнительных загрузок `dlopen`. Сейчас эти сведения **сравниваются как данные, а не извлекаются из ОС**.

### 3. Совместный маршрут

`review_offline_vm_evidence(...)` последовательно:
1. Проверяет **реальную Ed25519 detached signature** (через код GD31J27ab) только при наличии офлайн-библиотеки и независимо закреплённых SHA публичного ключа / каталога, точной версии и предыдущего legacy manifest.
2. Требует **все** wheel, объявленные в каталоге; пустой список wheel не может создавать ложное доказательство записи RECORD.
3. Для каждого wheel сверяет полный ZIP hash и RECORD.
4. Сравнивает внешние ELF/systemd/process факты с закреплёнными данными.

Ни один путь не порождает OS authorization. **Независимая подпись каталога — не доказательство полноты снимка ОС**, а неофициальный доверенный ключ не становится доверенным от самой проверки.

### Блокировки и приёмка

Новые тесты `test_native_saveas_offline_vm_evidence.py`, `test_native_saveas_offline_vm_evidence_contract.py` **написаны, но не запускались**. Они проверяют синтетические ZIP/RECORD, неправильные digest и размеры, duplicate/unlisted entries, symlink/path traversal, большой распакованный файл, различия UID/PID/ELF/drop-in и ложные флаги attestation; через мок Ed25519 проверяют последовательность маршрута. Никаких реальных файлов /proc/systemd не читают.

Две новые pre-restore offline регистрации внесены в `scripts/office_acceptance.py`, дополнена `scripts/tests/test_office_acceptance.py`. Матрица `native_saveas_acceptance_gate.py`: **37 fault cases / 34 mandatory manual E2E gates**, новые:
- `wheel-record-archive-and-os-observation-mismatch-deny`;
- `eurooffice-gd31j27ac-wheel-record-elf-systemd-os-snapshot-e2e`.

CMake `GETDOC_GD31J27AC_ENABLE_OFFLINE_VM_EVIDENCE_PUBLICATION_LAB=OFF`, попытка ON — безусловный **FATAL_ERROR GD31J27AC_BLOCKED**. Все ограничения предыдущих этапов l–ab сохранены. Обычные worker/broker listener/CEF и графический интерфейс эти функции **не вызывают**.

### Следующий GD-31J.27ad

Создать отдельно reviewed **непривилегированный или минимально привилегированный OS evidence collector** для запуска на одноразовой Astra Linux VM: фиксированный root-of-trust, получение фактических wheel distributions/RECORD, строго ограниченных ELF `DT_NEEDED`, `/proc` maps, systemd effective unit/drop-ins и PID startticks. Нужно доказать происхождение снимка снаружи проверяемого worker и отсутствие корпоративных mounts. Затем выполнить реальные тесты, сверить kernel/version/OS политики и только после независимого review рассмотреть одноразовый трёхпроцессный **deny-only** E2E. Пользовательские документы и production SaveAs по-прежнему запрещены.

**Не выполнялись:** локальные Python unit tests, C++ сборки, настоящий `os.pidfd_open`, `systemctl`, Astra VM, реальные SCM_RIGHTS, публикация, GitHub Actions, merge в main. WORM, GUP-2, SMB, Nextcloud и пользовательские OOXML/PDF не изменены.
