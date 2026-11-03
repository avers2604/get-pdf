# GD-31J.27be — неактивные прототипы Windows FILE_ID_INFO / Linux openat2 + fstat

**Репозиторий:** `avers2604/get-pdf`. **Ветка:** `feature/gd31j27be-native-fileid-openat2-probes`, Draft поверх GD-31J.27bd / PR #169.

**Принятое ограничение:** без сборки, запуска тестов и Windows/Astra/SMB-стенда. Никаких изменений `main`, GitHub Actions, действующих `OfficeRecoveryStore`, WORM, GUP-2 или native EuroOffice SaveAs. Выпуск остаётся запрещённым, `releaseReady=false`.

## Цель и реализованный объём

Предыдущий этап GD-31J.27bd создал интерфейсы `RecoveryNativeIdentityGate`, `RecoveryNativeReadLease` и отключённые `WindowsRecoveryNativeIdentityAdapter` / `LinuxRecoveryNativeIdentityAdapter`. В GD-31J.27be **реально добавлен исходный код двух изолированных программ**, но ни одна из них **не подключена** к адаптерам. Их назначение — помочь оценить OS API на изолированном стенде позднее.

### Windows — только диагностика полного File ID

`probes/recovery-native-identity/windows/fileid_identity_probe.c` использует Win32 `CreateFileW` с `FILE_READ_ATTRIBUTES`, `FILE_FLAG_OPEN_REPARSE_POINT`, `FILE_FLAG_BACKUP_SEMANTICS`, `GetFileInformationByHandleEx(FileIdInfo)` и `FILE_ID_INFO`: 64-разрядный `VolumeSerialNumber` + **полный 128-разрядный** `FileId.Identifier[16]`. Проверяется атрибут `FILE_ATTRIBUTE_REPARSE_POINT` на каждом **открытом terminal handle**, directory/file type, размер обычного файла (1…128 МиБ) и повторное совпадение идентификаторов root, parent и copy, полученных через **те же** открытые дескрипторы.

Программа **не читает содержимое** и **не делает** запись/удаление. Использование `CreateFileW` с составленным абсолютным путём **не доказывает**, что промежуточные junction/symlink запрещены или каталог/файл открыты относительно доверенного родительского дескриптора. Поэтому фраза `DIAGNOSTIC ONLY — NOT HANDLE-RELATIVE` выведена в код/результат: успешный вывод File ID **не даёт права** считать адаптер безопасным или включить восстановление.

По опубликованной спецификации `FILE_ID_INFO` полный идентификатор состоит из 16 байт, и его нужно сопоставлять вместе с серийным номером тома; старый 64-разрядный FileIndex не является полноценной заменой для ReFS. SMB может возвращать неполные ID; такой случай обязан завершаться отказом.

### Astra Linux / ALT / РЕД — эксперимент на дескрипторах

`probes/recovery-native-identity/linux/openat2_identity_probe.c` принимает доверенный абсолютный recovery-root, 64-hex каталог и имя GUID-OOXML. После `open(root, O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC)` относительно **открытого root fd** делает `syscall(SYS_openat2, ...)` с:

- `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV`;
- `O_NOFOLLOW | O_CLOEXEC` в обоих относительных открытиях;
- `O_PATH | O_DIRECTORY` для каталога и `O_RDONLY` для copy-файла.

Проверяет `fstat()`, `S_ISDIR/S_ISREG`, непустой size ≤128 МиБ, равенство `st_dev` для root/parent/file (без перехода на другое монтирование) и стабильные `st_dev/st_ino` при повторном чтении на **тех же** fd. Лишь печатает номера: **ZIP/OOXML не читает**, никаких файловых мутаций.

При недоступном `SYS_openat2` (`ENOSYS`) или неподдерживаемом ABI (`E2BIG`) возвращает `EXIT_UNSUPPORTED = 77`; **никакого** обхода через обычный `openat` и снятия no-follow. На части Astra Linux потребуются новые заголовки `linux/openat2.h` и подходящее ядро; решение об этом допустимо только после реальной компиляции/испытаний.

**Ограничение root bootstrap:** `O_NOFOLLOW` защищает только **последнюю** компоненту абсолютного `root`. Для полного протокола нужно открыть и проверить корень от заранее доверенного дескриптора, чтобы не принять символьную ссылку/junction где-либо выше.

## Явные требования к недеструктивности

Программы имеют только разрешения чтения метаданных/дескрипторов, **не вызывают** запись, копирование, удаление, переименование или операции восстановления. Имя копии проверяется как ровно 32 ASCII-hex + разрешённое OOXML расширение (включая `.part.`); имя каталога — ровно 64 ASCII-hex. Несовпадение формата/ошибка системного вызова даёт отказ.

Команды для **будущей** MSVC/Linux сборки и отрицательные сценарии описаны в `probes/recovery-native-identity/README.md`. Они приведены как инструкция, **не выполнялись**.

## Тесты и контроль (написаны, не запускались)

- Создан `scripts/tests/test_recovery_native_probes_contract.py`: **8 Python source-only методов**, проверяющих вызовы `openat2/fstat` и Win32 `GetFileInformationByHandleEx`, отсутствие небезопасного fallback/деструктивных операций, выключенные production-адаптеры и release-gates.
- `scripts/office_acceptance.py`: регистрация `recovery-native-probes-source-tests` **до** `solution-restore`.
- `scripts/tests/test_recovery_chain_consistency.py`: регистрация нового контракта и проверка уникальности manual-gates вплоть до `bd/be`.
- `scripts/tests/test_office_acceptance.py`: три новых manual-gates остаются `not-run`:
  - `gd31j27be-native-windows-fileid-manual-e2e`;
  - `gd31j27be-native-astra-openat2-manual-e2e`;
  - `gd31j27be-native-smb-identity-manual-e2e`.

**Ни одна** проверка не исполнялась, C-код не компилировался. Сверка строк исходников — не доказательство ABI, корректности системных вызовов или защиты от гонок.

## Будущая отдельная проверка безопасности

1. Скомпилировать MSVC/x64 и Astra Linux на **изолированных** синтетических папках, проверить layout `FILE_ID_INFO`, `FILE_STANDARD_INFO`, `open_how` и архитектуру `syscall`.
2. Проверить обычный файл, symlink/junction, hardlink, замену имени, переподключение SMB, отсутствие прав, обрыв сети, разные хранилища/файловые системы и невозможность получения полного ID.
3. Убедиться, что Linux `RESOLVE_BENEATH` и все `RESOLVE_NO_*` действительно доступны на целевых Astra-ядрах, а возвращаемое значение `EXDEV/EAGAIN/ELOOP/ENOSYS` не приводит к «безопасному» fallback.
4. Для Windows разработать **отдельный** действительно root-handle-relative открыватель. Эта проба не является такой реализацией; `CreateFileW` с абсолютным путём не обеспечивает нужного свойства.
5. Отдельно спроектировать передачу **того же** проверенного открытого дескриптора реальному потребителю до окончания восстановления. `OfficeRecoveryCopy.FilePath` сам по себе не переносит гарантию OS file identity.
6. Только после подтверждённых тестов интегрировать новые адаптеры в `OfficeRecoveryStore` отдельным PR с флагом и обратной совместимостью. Никаких изменений WORM-оригиналов и автоматической очистки.

## Источники спецификаций

- Microsoft: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
- Microsoft FILE_ID_INFO: https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info
- Microsoft reparse points: https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-operations
- Linux `openat2(2)`: https://man7.org/linux/man-pages/man2/openat2.2.html
- Linux `fstat(2)`: https://man7.org/linux/man-pages/man2/stat.2.html

**Следующий этап GD-31J.27bf:** анализ безопасности передачи открытого дескриптора от проверенной копии в процесс восстановления, с контрактами владения и отмены, пока без подключения нативных проб и без сборки.
