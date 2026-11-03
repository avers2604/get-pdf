# GD-31J.27at — аудит цепочки Draft PR и контрактов диагностики восстановления

**Дата:** 03.11.2026. **Репозиторий:** avers2604/get-pdf. **Текущая ветка аудита:** `feature/gd31j27at-recovery-contract-audit`, продолжение `feature/gd31j27as-reopen-shutdown-hardening`. Никаких слияний, релизов или запуска GitHub Actions.

## Проверено по живым данным GitHub

В момент чтения GitHub Pull Request API цепочка имела следующий вид:

| PR | Этап | Base (предыдущая ветка) | GitHub состояние |
|---|---|---|---|
| #149 | GD-31J.27aj | `feature/gd31j27ai-ipc-denial-hardening` | open, draft, mergeable_state=clean |
| #150 | GD-31J.27ak | `feature/gd31j27aj-cross-platform-copy-save-hardening` | open, draft, clean |
| #151 | GD-31J.27al | `feature/gd31j27ak-windows-named-pipe-denial-lab` | open, draft, clean |
| #152 | GD-31J.27am | `feature/gd31j27al-synthetic-filesystem-observation` | open, draft, clean |
| #153 | GD-31J.27an | `feature/gd31j27am-windows-pipe-byte-read-fix` | open, draft, clean |
| #154 | GD-31J.27ao | `feature/gd31j27an-recovery-link-guard` | open, draft, clean |
| #155 | GD-31J.27ap | `feature/gd31j27ao-recovery-readonly-inventory` | open, draft, clean |
| #156 | GD-31J.27aq | `feature/gd31j27ap-recovery-diagnostics-pane` | open, draft, clean |
| #157 | GD-31J.27ar | `feature/gd31j27aq-recovery-a11y-keyboard` | open, draft, clean |
| #158 | GD-31J.27as | `feature/gd31j27ar-recovery-tab-lifecycle` | open, draft, clean |

Таким образом, у этих **десяти** последовательных PR не обнаружено пропущенной base-ссылки, замены ветки или конфликта GitHub mergeability на момент проверки. Это **не доказательство успешного .NET build или корректности реального SaveAs**. Для SHA головы #158 `aa2e664f860d9cba47669719d57c69d06cda490f` GitHub API `check-runs` возвращал `total_count=0`, а `commits/:sha/status` — `statuses=[]`, `state=pending`. Автоматических результатов сборки нет.

## Реальные ошибки, обнаруженные и исправленные в GD-31J.27at

**Ошибка 1 — порядок Dispose связанного CancellationTokenSource:**

В `RecoveryDiagnosticsPane.RefreshAsync` было объявление `using var inspection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);`, но в блоке `finally` вызывался `DisposeLifetime()`. Для using declaration освобождение `inspection` выполняется **после выхода из блока finally**, поэтому общий родительский CTS освобождался раньше связанного CTS. При завершении приложения на фоне медленного SMB эта последовательность противоречила ожидаемому жизненному циклу.

Исправление: обычная локальная переменная `var inspection`; **явное** `inspection.Dispose()` в `finally`, после удаления `_activeInspection`, **до** `DisposeLifetime()`. Повторный запуск отложенного осмотра по-прежнему разрешён только после завершения предыдущего. `Dispose()` панели не блокирует окно и не дожидается системного файлового вызова. Изменение выполняется только в уже существующей панели диагностики; новых полномочий чтения содержимого, изменения/удаления данных нет.

**Ошибка 2 — устаревший source-only тест CTS:**

`test_recovery_tab_lifecycle_contract.py` ожидал буквальную строку `using var inspection = ...`; она теперь противоречит безопасному порядку. Тест требует явного создания и `inspection.Dispose()` и не привязан к небезопасному `using var`.

**Ошибка 3 — ранее пропущенный устаревший тест:**

`test_recovery_diagnostics_ui_contract.py` всё ещё требовал `CancellationToken token = _lifetime.Token;`, хотя начиная с GD-31J.27ar нужен **отдельный** `CancellationToken token = inspection.Token;` от связанного источника. При старом ожидании source-only тест заведомо не мог пройти. Обновлён на фактический контракт, а новый `test_recovery_chain_consistency.py` предотвращает повторное появление этой ошибки.

## Добавлен общий source-only контракт

`scripts/tests/test_recovery_chain_consistency.py` — **7 независимых unittest-методов**. Проверяет:

1. порядок освобождения дочернего и родительского CancellationTokenSource в `RefreshAsync.finally`;
2. запрет отображения результата при отмене/скрытой/закрытой панели и отсутствие синхронного ожидания;
3. регистрацию каждого из шести source-only контрактов цепочки **ровно один раз до NuGet restore**;
4. уникальность новых ручных acceptance gates, начальные `manualAcceptance:not-run` и `releaseReady:False`;
5. отсутствие старого API-ожидания `CloseRecoveryDiagnostics(id);` в тестах;
6. наличие актуального per-scan-token ожидания в предыдущем UI-контракте;
7. отсутствие файловых операций чтения содержимого/удаления/записи и новых кнопок в диагностической панели.

Новый контракт зарегистрирован в `scripts/office_acceptance.py`; его регистрацию на Windows/Linux проверяет `scripts/tests/test_office_acceptance.py`. Ручные платформенные acceptance gates остаются `not-run`.

## Реально выполненные проверки этого аудита

- Живые GitHub PR API для #149–158: `open/draft/mergeable_state=clean` и непрерывность base/head.
- Живые GitHub REST endpoints для SHA #158: **0 check-runs, 0 commit statuses**, нет доказательств сборки.
- Файлы исходников/контрактов считаны обратно из GitHub после внесённых правок.
- Проведена модельная **строковая статическая сверка** прямых Python `self.assertIn/NotIn`-ожиданий из source-only контрактов против актуального текста C#/Python. После исправления забытого token-assertion: **101 из 101 прямого буквального ожидания совпало** (включая проверки `self.close`, `self.source_close`, `self.closed`, `self.attached`, `self.refresh` и `self.finally_body`). Такая проверка не запускает сами Python unittest-методы, не выполняет их ветвления/регулярные выражения и не доказывает компиляцию.

**Не выполнены:** Python unittest runner (рабочее дерево недоступно как локальный git checkout), C# компиляция (нет `dotnet` SDK), MSTest, настоящий UIA/NVDA, Windows/NTFS, Astra Linux, SMB, EuroOffice native, WORM/CAS, GUP-2. В среде исполнения `git ls-remote https://github.com/avers2604/get-pdf.git HEAD` возвратил `Could not resolve host: github.com`. Доступ GitHub через разрешённый connector позволяет править файлы и PR, но не заменяет окружение сборки. Не утверждать «все тесты пройдены».

## Рекомендуемая дальнейшая проверка

На изолированном ПК с одобренным локальным NuGet feed и .NET 10 SDK выполнить:

```sh
python3 -m unittest discover -s scripts/tests -p 'test_recovery_*contract.py' -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DocumentWorkspaceTests
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~OfficeRecovery
dotnet build src/GetDocument.Desktop/GetDocument.Desktop.csproj -c Release --no-restore
```

Далее отдельная ручная приёмка на Windows 10/11 и Astra Linux: symlink/junction/SMB, переключения и закрытие вкладок, конфликт имен, WORM-оригиналы, длинные и прерванные операции, доступность с клавиатуры и экранным диктором. До прохождения этого этапа весь стек остаётся **Draft/not-run**, `releaseReady=false`. Не сливать PR в `main` и не включать GitHub Actions автоматически.

## Следующий этап

После получения реальных результатов сборки/тестов — устранение конкретных ошибок. До этого целесообразнее не наращивать число новых тестов, проверяющих только исходный текст, и не создавать ещё новые функциональные ветки.
