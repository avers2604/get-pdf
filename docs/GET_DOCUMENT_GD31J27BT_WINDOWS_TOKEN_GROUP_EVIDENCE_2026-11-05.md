# GD-31J.27bt — Windows: TOKEN_GROUPS и безопасная корреляция ACE

**Дата:** 2026-11-05. **База:** GD-31J.27bs / Draft PR #184. **Режим:** отдельный stacked Draft; `releaseReady=false`, без подключения к рабочему сохранению, удалению и восстановлению.

## Что добавлено

- `RecoveryWindowsTokenGroupProbe`: внутренний **read-only** компонент принимает только **уже удерживаемый** `SafeFileHandle` для Windows access token, предоставленный вызывающей диагностикой. Сам не открывает процессный/потоковый токен и не изменяет его.
- `GetTokenInformation(TokenGroups)` выполняется с верхней границей 128 КиБ и 256 SID. Массив `SID_AND_ATTRIBUTES` разбирается с учётом разрядности указателя; диапазоны SID проверяются до вызова Windows SID conversion. Неполный ответ, несогласованная длина, превышение лимита и повторные SID приводят к отказу.
- Разделены статусы SID группы: **enabled**, **deny-only**, **disabled**, **unreviewed** (в том числе integrity/несогласованные флаги). Наличие SID в токене само по себе не означает, что ALLOW ACE действует.
- Существующий диагностический разбор DACL теперь сохраняет в памяти **ограниченный read-only список ACE с флагами наследования**. Отдельный метод `Correlate` сравнивает отдельные ACE c группами токена. Для `INHERIT_ONLY_ACE` фиксируется, что ACE не применяется к текущему каталогу, **но может иметь значение для дочерних объектов**.
- Сопоставление не «вычитает» DENY из ALLOW; неизвестные SID, нестандартные ACE и отсутствующая информация о пользователе/токене остаются `UnresolvedTokenCoverage`. Даже при нуле обнаруженных рисков `CanAuthorizeCapture=false` для **всех** итоговых типов.

### Почему это пока не полная проверка эффективных прав

Реальные Windows `AccessCheck` / `AuthzAccessCheck` зависят от **конкретного security token, который использует поток в момент операции** (включая impersonation), SID пользователя, набора enabled и deny-only групп, restricted SIDs, ACE с наследованием/порядком обработки, прав владельца, привилегий, integrity labels и ACL предков. Текущий диагностический snapshot не является атомарным ни относительно DACL, ни относительно токена.

**Новый компонент не вызывает AccessCheck, не предоставляет право WRITE/DELETE и не подключён к `OfficeRecoveryStore.CaptureAsync`, `FindLatest`, `Discard`, `Prune` или `RecoverySharedVolumeCoordinator`.** Эти ограничения обязательны до проверки настоящей операционной системы и независимого security review.

## Регрессионные тесты и ручная приёмка

Написаны **12 MSTest** для групп enabled/disabled/deny-only, integrity/неизвестных флагов, INHERIT_ONLY, read-only/DENY, дублирующихся SID, неполных данных, закрытого токена и совмещения ACE/токена. Дополнительно **6 Python source-only контрактов**, зарегистрированных перед offline NuGet restore.

Добавлены три ручных блокирующих gate (изначально `not-run`):

1. `gd31j27bt-windows-live-tokengroups-manual-e2e` — реальные токены, корректность P/Invoke, структура на x64, limits, корректное освобождение буфера и сравнение с независимым API.
2. `gd31j27bt-windows-restricted-impersonation-manual-e2e` — impersonation token потока, disabled/deny-only/restricted SID, вклад группы в фактическую проверку на файловой системе.
3. `gd31j27bt-windows-inherited-ace-mic-privilege-manual-e2e` — наследование ACE, mandatory integrity control, полный AccessCheck с привилегиями и отрицательными тестами против разрешений DELETE_CHILD/WRITE_DAC/FILE_ADD_FILE.

Количество пунктов каталога `MANUAL_GATES` после BT: **141** (138 ранее + 3). Само добавление gate — не доказательство результата.

## Безопасные команды для будущего offline-стенда

```sh
python3 -m unittest discover -s scripts/tests -p test_recovery_windows_token_groups_contract.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryWindowsTokenGroupEvidenceTests
python3 scripts/recovery_release_readiness.py --details
```

Для .NET: утверждённый .NET 10 SDK и одобренный локальный NuGet feed. Прежде чем исполнять Windows-native тесты, нужно обеспечить изолированный тестовый каталог/токен, без подлинных пользовательских документов или ACL изменений на рабочих ПК.

Источники проектирования:
- https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_groups
- https://learn.microsoft.com/en-us/windows/win32/secauthz/how-dacls-control-access-to-an-object
- https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-accesscheck

## Реальный статус

Файлы C# и тесты добавлены в Draft-ветку; отдельный рецензент должен подтвердить корректность native layout и lifetime. **Компиляция .NET, MSTest, Python unittest, Windows API runtime, Astra Linux/SMB, fault injection не выполнялись в этой сессии.** Нет доказательств безопасности операций записи/удаления и права на release. В `main` ничего не объединять, GitHub Actions не включать.

**Следующий кандидат GD-31J.27bu:** Astra Linux доверенный anchor, UID/xattrs/ACL, mount alias и контроль жизненного цикла дескрипторов — без опасного fallback. Windows effective token/AccessCheck verification остаётся P0 и не закрывается этим этапом.
