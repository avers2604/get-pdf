# GD-31J.27bu — Astra Linux: read-only привязка каталогов к корню mount namespace

**Дата:** 2026-11-05. **База:** GD-31J.27bt / Draft PR №185. **Статус:** отдельная stacked Draft-ветка, `releaseReady=false`. ЗАПРЕЩЕНО включать нативное создание/удаление документов или считать этот аудит разрешением на публикацию и восстановление.

## Назначение

В GD-31J.27bq `RecoveryPinnedLinuxParentProbe.OpenChildRelative()` открывал директорию относительно **уже удерживаемого fd**, но доверенное происхождение самого parent fd не было подтверждено. Новый `RecoveryLinuxAnchoredDirectoryAudit.cs` добавляет независимое диагностическое наблюдение пути от **`/` текущего mount namespace процесса**. Это **не доказывает доверие к самому namespace, supervisor, контейнеру и политике ОС**; не даёт прав записи/удаления.

Новые внутренние API:

- `ValidateAbsolutePath(path)`: требует исходный абсолютный Linux-путь без `.`, `..`, повторных разделителей и обратного слэша; не нормализует и не игнорирует опасные компоненты. Лимиты: 4096 байт на путь, 255 байт на сегмент, 32 сегмента.
- `InspectExisting(absoluteDirectory, cancellationToken)`: закрепляет системный каталог `/` через read-only `open(O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC)`; далее **каждый** существующий сегмент открывается `openat2` от ранее закреплённого fd с `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV`. Никакого fallback на `File.Open`, `openat` или разрешение ссылок.
- Каждый открытый каталог подтверждается повторным `statx(AT_EMPTY_PATH)` с UID, mode, dev/inode и mount ID. Для всех предков допускается только root или эффективный UID и отсутствие group/other write; **последний каталог** должен принадлежать эффективному UID и иметь **ровно 0700**. Каталог вида `/tmp` (мировая запись) отклоняется, даже если есть sticky bit.
- Через удерживаемые fd вызывается `flistxattr` с лимитом **8 KiB / 64 имён**. Наблюдаются только **имена**, включая `system.posix_acl_access`, `system.posix_acl_default`, `security.*`, `trusted.*` и неизвестные. Сортированный SHA-256 от видимых имён фиксирует изменения состава при повторном чтении.
- Возвращается `RecoveryLinuxAnchoredDirectoryLease`: все fd (включая родительские) остаются открыты до `Dispose`. Программа повторно обследует каждый удерживаемый fd. По ошибке, отмене, недоступному kernel API, смене mode/UID/mount/xattr — закрывает уже открытые дескрипторы без побочных операций с файлами.
- `InspectChildRelative(parentPath, childName)`: сначала обследует цепочку предков, затем использует **старый безопасный** `RecoveryPinnedLinuxParentProbe.OpenChildRelative()`. `RecoveryLinuxAnchoredChildLease` удерживает одновременно всю цепочку и дочерний fd. При `Dispose` дочерний lease закрывается перед цепочкой.
- В `RecoveryPinnedLinuxParentProbe.cs` добавлен только `ReadExistingDirectoryEvidence(SafeFileHandle)`, который повторно использует уже существующий read-only `statx`.

**Все evidence/lease объекты: `CanAuthorizeCapture=false`.** Новые методы не подключены к `OfficeRecoveryStore`, `CaptureAsync`, `Discard`, `FindLatest`, `Prune`, `RecoverySharedVolumeCoordinator`, EuroOffice Native SaveAs, GUP-2, WORM, СЭД и не меняют ACL/права.

## Чего аудит не может утверждать

- Исходная точка `/` относится к **текущему mount namespace процесса**, а не гарантированному пространству имён хостовой Astra Linux. Перед использованием потребуется доказать происхождение процесса, наличие/отсутствие user namespaces, chroot, контейнеров и bind mounts.
- `RESOLVE_NO_XDEV` откажет при любом переходе между mount points, **включая bind mount**; легальные отдельные разделы `/home` или `/var` могут стать недоступны для диагностики. Не убирать флаг автоматически ради совместимости.
- `flistxattr` **может не показать атрибуты, к которым у процесса нет доступа**. Отсутствие видимых ACL/xattr не доказывает отсутствия effective ACL/LSM. Значения атрибутов не читаются; хеш покрывает только видимые имена.
- Две проверки metadata/xattr не являются атомарным snapshot. Посторонний привилегированный процесс может поменять ACL, xattr, namespace или имя после проверки; удержание fd не запрещает конкурентные изменения.
- Сведения о POSIX ACL не являются полноценной оценкой допустимости записи: ещё требуются фактические ACL, supplementary groups, LSM/AppArmor/SELinux/мандатная модель Astra, capabilities, mount options и привилегии процесса.
- Пока нет доказанного OS-held FILE-ID bound create/delete/rename, owner-only lock identity и безопасного Native SaveAs. Политика `releaseReady=false` обязательна.

## Тесты — подготовлены, не запущены

- **14 MSTest** `tests/GetDocument.Office.Tests/RecoveryLinuxAnchoredDirectoryAuditTests.cs`: path traversal, лимиты, UID/mode, чужой владелец, 0700, xattr ACL/security, повреждённый или изменённый список, стабильный digest, смена mount ID и lifecycle/отказ до native open.
- **8 Python source-only тестов** `scripts/tests/test_recovery_linux_anchored_directory_contract.py`. Проверяют отсутствие production wiring и операций записи/удаления, удержание fd, отсутствие fallback, список системных вызовов, лимиты и ручные gates.
- 4 ручных E2E-gate, **не выполнены**:
  - `gd31j27bu-astra-root-chain-openat2-manual-e2e` — openat2/statx на одобренной Astra, POSIX perms, ссылки/rename.
  - `gd31j27bu-astra-xattr-acl-visibility-manual-e2e` — реальные POSIX ACL и mандатные xattr/LSM, проверка видимости и отказов.
  - `gd31j27bu-astra-mount-namespace-alias-manual-e2e` — mount namespaces, bind mount, домашний каталог на отдельном разделе, зафиксированный root inode.
  - `gd31j27bu-astra-uid-permissions-cancel-manual-e2e` — не-root user, чужие владельцы, cancellation и жизненный цикл fd.

В `scripts/office_acceptance.py` зарегистрированы новые source-only tests перед offline NuGet restore. Всего обязательных `MANUAL_GATES` **145** (141 ранее + 4 новых); новые gates и проверки ОС имеют `not-run`.

## Команды на согласованном изолированном ПК (позже)

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_linux_anchored_directory_contract.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryLinuxAnchoredDirectoryAuditTests
python3 scripts/recovery_release_readiness.py --details
```

Оригинальные документы, recovery WORM, рабочие каталоги/тома и ACL не изменять. До настоящих platform tests никто не должен считать `statx`/xattr сигналы достоверным разрешением на запись.

**Статус проверки в данной сессии:** код и тесты написаны; выполнялась проверка исходников и GitHub-ветки, **не выполнялись dotnet build/MSTest, Python unittest, реальный вызов openat2/statx/flistxattr на Astra Linux, SMB, CI, аварийное завершение и fault injection**.

Справочные системные спецификации:
- https://man7.org/linux/man-pages/man2/openat2.2.html
- https://man7.org/linux/man-pages/man2/flistxattr.2.html
