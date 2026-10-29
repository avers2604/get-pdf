# GD-31J.36 — согласование размерного лимита CEF stdout и Save As квитанции v2

**Дата:** 29 октября 2026. **База:** Draft PR #107, GD-31J.35. **Статус:** исправлены исходники и подготовлены тесты; **сборка, unit/integration/regression, native CEF/Qt и VM не запускались** по ранее утверждённому порядку.

## Подтверждённое межмодульное несоответствие

Цепочка синтетического нативного сохранения состоит из `CEF/Qt → stdout JSONL → EuroOfficeNativeBindingProbeLab.RunRawLabAsync / ReadOnlyReceiptAsync → EuroOfficeNativeSaveAsEventReceipt.Parse → EuroOfficeNativeSaveAsLab.ReviewCandidate → InspectRevision → EuroOfficeSyntheticReviewedSnapshot.Commit → GD31J30 journal / GD31J31 report`.

В модуле транспорта `EuroOfficeNativeBindingProbeLab.ReadOnlyReceiptAsync` **был жёсткий максимум 2048 символов** для любого native reply. Вложенная квитанция CEF SaveAs `getdoc-native-saveas-event-lab/2` вместе с `receipt/1` корректно парсится `EuroOfficeNativeSaveAsEventReceipt.Parse` при длине **до 4096 символов**. Таким образом, v2 квитанции длиной 2049–4096 символов до этого автоматически отбрасывались в транспорте, хотя весь дальнейший строгий parser мог их прочитать. Компактная текущая выдача CEF, вероятно, обычно короче 2048 символов; **фактический production runtime-сбой из этого несоответствия не установлен без запуска**.

Причина — расширение формата v2 без соответствующей настройки общего строго ограниченного native stdout reader. Увеличивать лимит для всех старых протоколов опасно и не нужно.

## Исправление

- `RunRawLabAsync(ProcessStartInfo info, CancellationToken ct, int maxReceiptChars=2048)` и `ReadOnlyReceiptAsync(TextReader reader, CancellationToken ct, int maxChars=2048)` теперь принимают **только** значения `2048` или `4096`. Любое другое число — `ArgumentOutOfRangeException` **до запуска процесса** или чтения.
- `EuroOfficeNativeSaveAsLab.RunAsync` явно передаёт `maxReceiptChars: 4096` для **единственного** вложенного протокола v2. `EuroOfficeNativeBindingProbeLab.RunAsync` по-прежнему использует неизменный default 2048; базовый `EuroOfficeNativeSaveAsReceipt.Parse` (/1) не меняет свой лимит 2048.
- Все проверки stdin/stdout unchanged: только одна непустая JSON-строка, обязательный `\n`, недопустимы `\r`, NUL, управляющие символы, доп. строка/лог после JSON, чрезмерный размер, неверный exit code; timeout и kill дерева процесса с последующим bounded reap остаются.
- Парсер v2 по-прежнему проверяет версию, точные имена полей, session/tab/fixture, nested v1 receipt, хеши и отсутствие полномочий `DocumentVerified/SaveCompleted/AuthorizedForPublication`. Изменение транспортного лимита **не** подтверждает корректность Office/OOXML, сохранение в пользовательские документы или долговечность FS.

## Написанные регрессионные сценарии — не исполнялись

`EuroOfficeNativeBindingProbeLabTests`:
1. v2-синтетический JSON длиной более 2048, но менее 4096: `ReadOnlyReceiptAsync(...,maxChars:4096)` должен вернуть полную строку;
2. `4097` символов, дополнительная строка после JSON, `\r`, `\0`, обрыв без `\n` — отказ;
3. старый binding v1 с расширенной строкой `>2048` по умолчанию отвергается;
4. нельзя запросить произвольные `65536` символов.

`EuroOfficeNativeSaveAsEventReceiptTests.BoundedV2TransportAndStrictParserAcceptSameNestedEnvelope`: действительная тестовая nested v2 квитанция, дополненная допустимыми JSON-пробелами до 2–4 KiB, должна проходить **тот же** stdout-reader и затем строгий v2 parser; все публикационные разрешения остаются false.

`test_saveas_v2_transport_bound_contract.py` — offline source gate для двух квитанционных лимитов, явного вызова v2 и сохранённой строгости stdout. `scripts/office_acceptance.py` включает `cef-saveas-v2-transport-bound-source-tests` до restore и будущий ручной `eurooffice-gd31j36-native-saveas-v2-stdout-2048-4096-manual-vm`; обновлён `scripts/tests/test_office_acceptance.py`. GitHub Actions не включались.

## Отложенные обязательные проверки

- Реальная `dotnet build/test` net10 на Windows и Linux/Astra, Python offline tests и CEF/Qt native lifecycle.
- Создать в **исключительно disposable offline** лаборатории реальное native Save As v2 сообщение в интервале 2049–4096, проверить single newline, UTF-8/нет неожиданных символов и отказы выше лимита. CEF обычно формирует compact JSON, поэтому для длинных сообщений нужен контролируемый тестовый emit — без расширения публичного протокола или произвольного вывода данных.
- Проверить malformed v2/nested v1, неавторизованный запуск, неполную загрузку, timeout, отмену и дубликаты callbacks.
- Проверить, что GD31J35 manifest/3 fixtures проходит независимый structurally valid OOXML, CEF работает с SDK, а GD31J29–32 по-прежнему не публикуют результат и не изменяют оригинал.
- File system crash/power-loss и WORM остаются отдельными фазами; `DurableAcrossPowerLoss=false` и `AuthorizedForPublication=false`.

Текущий код по-прежнему **не готов к production**, пока не выполнены сборка, реальные тесты и OS/FS/Office interop. Все изменения — только Draft поверх PR #107, без прямых изменений `main`, GitHub Actions, лаунчера, пользовательских файлов и хранилища GET-system/GUP-2/Nextcloud/WORM.

**Следующий этап GD-31J.37:** сверка полного synthetic native receipt/manifest/journal ABI перед финальной сборкой, без преждевременной интеграции в production save pipeline.
