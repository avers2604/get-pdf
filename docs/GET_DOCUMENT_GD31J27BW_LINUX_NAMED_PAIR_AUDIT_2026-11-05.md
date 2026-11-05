# GD-31J.27bw — Linux: привязка имени recovery-пары к удерживаемому файловому дескриптору (read-only)

**Дата:** 2026-11-05. **База:** GD-31J.27bv / Draft PR #187. **Режим:** отдельный stacked Draft, `releaseReady=false`. Не подключать к production восстановлению до реальных Windows/Astra/SMB тестов.

## Устраняемый диагностический пробел

До BW Linux-проверки умели прочитать File ID из уже удерживаемого `SafeFileHandle` и обследовать родительскую цепочку. Однако copy и manifest могли быть **переименованы или подменены после открытия**, а проверка одного held handle не доказывает, на какой объект сейчас указывает *имя* в каталоге.

Добавлен `src/GetDocument.Office/LibreOffice/RecoveryLinuxNamedPairProbe.cs`: только read-only обследование существующих **двух имён** под **уже открытым** родительским fd. Оно ничего не создаёт, не переписывает, не переименовывает и не удаляет. Исходные пользовательские документы и WORM-оригиналы не читаются.

## Контракт

1. `ValidatePairNames(copyName, manifestName)`: требует одинаковые 32 **строчных** шестнадцатеричных символа GUID и точные имена `<nonce>.docx/.xlsx/.pptx` + `<nonce>.json`. Никакие пути с `/`, `..`, временными суффиксами или иными расширениями не допускаются.
2. `ObserveExistingPair(heldParent, heldCopy, heldManifest, copyName, manifestName)`: удерживает три caller-owned handle посредством `DangerousAddRef`/ `DangerousRelease`. Требует Astra/Linux little-endian x64/aarch64, существующий приватный родительский каталог (`statx`, `0700`, текущий effective UID).
3. Каждое имя открывается **от parent fd** через `openat2` + `O_PATH|O_NOFOLLOW|O_CLOEXEC`, `RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS|RESOLVE_NO_MAGICLINKS|RESOLVE_NO_XDEV`. **Нет fallback** на открытие по абсолютному пути, нет создания, `renameat2` или `unlinkat`.
4. Важно: по man page `O_PATH|O_NOFOLLOW` при `RESOLVE_NO_SYMLINKS` может вернуть **дескриптор самой последней symlink**. Поэтому `ParseRegularStatx` дополнительно требует строго `S_IFREG`, полный `statx`, owner UID, mode ровно `0600`, `stx_nlink=1` и ненулевой mount ID. Каталоги, устройства, FIFOs, ссылки, расширенные права и hardlinks не принимаются. Это лишь ограниченный снимок сведений.
5. Идентификаторы held copy/manifest сравниваются с двумя независимыми относительными открытиями **по имени**; сами held descriptors также повторно обследуются, как и родитель. При смене identity, inode, устройства, mount или UID — отказ. Имя не оставляется доступным для выполнения операции записи.
6. При ошибке и отмене все новые named handles закрываются, а внешние удерживаемые fd отпускаются; никаких деструктивных fallback. `RecoveryLinuxNamedPairFinding.MatchedAtObservation` означает только **совпадение на момент наблюдения**.

**Жёсткая защита:** `CanAuthorizePublish=false`, `CanAuthorizeDelete=false`, `RecoveryLinuxNamedPairObservation.CanAuthorizeMutation=false`. Ни `OfficeRecoveryStore`, ни `RecoveryPairMutationGate` не получают право публикации/удаления из этого компонента.

## Почему P0 всё ещё открыт

- Имя можно заменить **сразу после** последнего `openat2` даже если два чтения совпали. Нужен атомарный нативный протокол операции с идентичностью, закреплённой на уровне ядра/дескриптора, или доказанная изоляция процесса-владельца и каталога; простое повторное сравнение File ID недостаточно.
- `/proc`, bind mounts, mount namespaces, ACL/LSM, `seccomp`, user namespaces и реальные права `geteuid` требуют Astra Linux квалификации. `RESOLVE_NO_XDEV` отвергает в том числе легальные bind mounts и отдельные файловые разделы.
- Windows пока имеет лишь диагностические ACL/token/owner/File ID адаптеры без подтверждённой handle/name-bound операции. SMB-кроссхостовые гарантии отсутствуют.
- `OfficeRecoveryStore.CaptureAsync` по-прежнему публикует `File.Move` по имени. Это **не защищено** новым модулем; реальное recovery publication не переключено на новую диагностику. `Discard` и `Prune` остаются недеструктивными согласно BV; не включать их снова автоматически.
- Реальная атомарность манифеста/копии при power loss, `fsync` каталога, жизненный цикл lock и задержанная отмена требуют отдельного OS-испытания.

## Подготовленные тесты

- **12 MSTest**: строгая грамматика имён, отказы traversal и temporary names, regular/symlink/dir/device, 0600/UID/hardlink, неполный `statx`, missing mount, повторная подмена, другие тома, изменившийся родитель, закрытые дескрипторы, cancellation и неизменная политика запрета.
- **8 source-only Python unittest**: ABI-константы, held handles, тип regular и `stx_nlink`, no-follow, два открытия имён, запрет запись/удаление, отсутствие production wiring и ручные gates. В зарегистрированном офлайн runner выполняются перед NuGet restore.
- **4 новые ручные проверки**, все `not-run`:
  - `gd31j27bw-astra-openat2-name-symlink-hardlink-manual-e2e`.
  - `gd31j27bw-astra-rename-during-held-fd-manual-e2e`.
  - `gd31j27bw-windows-no-native-delete-publish-manual-e2e`.
  - `gd31j27bw-smb-cross-host-race-manual-e2e`.

Количество обязательных ручных gates в `scripts/office_acceptance.py` увеличено с **149 до 153**. Текущие тесты **подготовлены, но не выполнялись на .NET/Astra/Windows**.

Команды на одобренном изолированном ПК:

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_linux_named_pair_contract.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryLinuxNamedPairProbeTests
python3 scripts/recovery_release_readiness.py --details
```

Документация семантики `openat2`: https://man7.org/linux/man-pages/man2/openat2.2.html

**Результат BW:** появилось строгое read-only наблюдение name-to-held-handle identity, но **никакого нового разрешения на создание/удаление файлов или применение в production нет**. `main` не менять, GitHub Actions не включать, `releaseReady=false`.
