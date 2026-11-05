# GD-31J.27bs — Windows DACL: read-only выявление широких разрешений ACE

**Дата:** 2026-11-05. **База:** GD-31J.27br, Draft PR #183. **Статус:** отдельная Draft-ветка, без сборки и ОС-стенда; `releaseReady=false`.

## Цель и реальные границы

В BQ диагностический Windows parent probe читал Owner SID и признак наличия DACL, но не сами ACE. Ненулевая DACL **не** доказывает отсутствие записей, позволяющих сторонним субъектам изменять recovery-каталог. Добавлен отдельный внутренний наблюдатель `RecoveryWindowsAclRiskObserver.cs`.

Наблюдатель принимает только **ранее открытый** `SafeFileHandle`, вызывает `GetSecurityInfo(SE_FILE_OBJECT, DACL_SECURITY_INFORMATION)`, `GetAclInformation`, `GetAce`. Не принимает путь к файлу и не открывает/создаёт его, не изменяет ACL/документы, не передаёт результаты как разрешение в `OfficeRecoveryStore` или координатор.

Обрабатывает только обычные `ACCESS_ALLOWED_ACE` (0) как диагностический признак риска; для каждого SID проверяет корректность границ ACE до `ConvertSidToStringSidW`. Отмечает явно широкие группы Everyone, Authenticated Users, BUILTIN Users/Guests и NETWORK при наличии прав FILE_ADD_FILE, FILE_ADD_SUBDIRECTORY, DELETE_CHILD, DELETE, WRITE_DAC, WRITE_OWNER, WRITE_EA/ATTRIBUTES или GENERIC_WRITE/ALL. Список не является исчерпывающим (могут быть группы/привилегии/права, не включённые в этот набор).

`ACCESS_DENIED_ACE` лишь учитывается и **не** отменяет риски ALLOW: правильный порядок ACE и effective token не вычисляются. Неизвестные/callback/object/conditional ACE учитываются в `UnreviewedAceCount`, но не интерпретируются как безопасные. **NULL DACL** вызывает отказ; превышение **256 ACE** — отказ. Native-ошибка — исключение, а не «чистый каталог». Из P/Invoke наружу выдаются только агрегированные счётчики; SID не записываются в отчёт.

`RecoveryWindowsPinnedParentProbe.OpenForDiagnostics` теперь добавляет этот агрегат в `RecoveryWindowsPinnedParentLease.AclRisk` из **того же закреплённого каталожного handle**. Это два независимых чтения security descriptor, **не атомарный снимок**: DACL может измениться между наблюдениями. Публикация/удаление не разрешаются и контроль предков не заменяется.

`RecoveryWindowsAclRiskEvidence.CanAuthorizeCapture` всегда `false`. Даже нулевой риск, отсутствующие ACE или совпадающий Owner SID **не дают никакой положительной авторизации**. Исходный `RecoveryWindowsParentSecurityGate.CanAuthorizeCapture` также остаётся `false`.

## Тесты и приёмка

Подготовлено **7 MSTest** для обнаружения широких ALLOW, нейтральных read-only ACE, индивидуального SID, нераспознанных типов, несостоятельности DENY как доказательства, лимита ACE и read-only дескриптора на Windows.

Добавлены **6 source-only Python contract tests**, которые запрещают операции записи/удаления и production wiring, проверяют P/Invoke и запуск до offline NuGet restore. В `scripts/office_acceptance.py` добавлены **3 обязательных ручных gates**, начальный статус `not-run`:

- `gd31j27bs-windows-ntfs-dacl-ace-manual-e2e`: DACL чтение через реальный NTFS handle, native буферы, ACE/SID, nil/null DACL, размер и ошибочные ACL.
- `gd31j27bs-windows-effective-token-inheritance-manual-e2e`: доменные группы, наследование, DENY/ALLOW, token privileges, integrity labels, чужие учётные записи и DACL у предков; эту логику ещё **предстоит реализовать** отдельным проверяемым слоем.
- `gd31j27bs-windows-reparse-owner-race-manual-e2e`: подмена последнего компонента и предков, reparse, исчезновение и переименование между проверкой имени, DACL и использованием handle.

### Команды на разрешённом offline ПК

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_windows_acl_risk_contract.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryWindowsAclRiskObserverTests
python3 scripts/recovery_release_readiness.py --details
```

Перед `dotnet test` нужен утверждённый .NET 10 SDK и восстановление только из внутреннего NuGet feed. Синтетические C# тесты и source contracts подготовлены, но **не являются доказательством Windows API без реального запуска**.

## Остаток P0

1. Проверка происхождения pinned parent handle от доверенной точки монтирования/root; исключение path-based lookup для всей цепочки.
2. Полная проверка effective Windows ACL/ACE/token/group/integrity/privileges **с отрицательными тестами**, а не только выявление заведомо широких ACE.
3. На Astra Linux — доверенный initial anchor fd; владелец, mount namespace, UID, ACL/xattrs, старые kernel.
4. Безопасные относительно race публикация, удаление, `FindLatest`, `Discard`, `Prune` только через подтверждённые native handle-bound контракты.
5. Реальная .NET сборка, MSTest, Windows NTFS/ReFS, Astra/ext4, SMB и аварийный перезапуск.
6. Отдельно Native EuroOffice SaveAs, GUP-2/SSO/WORM, DPI/accessibility, печать/установщик.

**Нет разрешения на сохранение или удаление пользовательских файлов, нет production wiring, нет merge в main, нет изменения GitHub Actions.** Схема с ручными gates после BS содержит 138 пунктов (135 до BS + 3 новых), все новые ручные проверки не выполнялись.
