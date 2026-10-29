# GD-31J.19 — EuroOffice Desktop: консолидация проверок и границы завершения

**29.10.2026.** Базовая ветка: PR #88, GD-31J.18. Этот этап исправляет ошибки **исходных Python-контрактов** в накопленной цепочке лабораторий GD-31J.15–J.18. **Это не выпуск EuroOffice Desktop и не подтверждение готового офлайн-редактора.**

## Изменено

1. Четыре теста внутри `probes/eurooffice-desktop/cef_lab/` использовали `Path(__file__).resolve().parents[2]` как корень репозитория, что фактически указывает на каталог `probes/`. Используется `parents[3]`.
2. Проверка GD-31J.15 ранее запрещала `SendKeyEvent` в **целом** `osr_input.cpp`, хотя GD-31J.16/17 законно добавили эту функцию в отдельно разрешённые обработчики клавиатуры и финального IME commit. Теперь отсутствие `SendKeyEvent` проверяется только в обработчиках клика, а не в остальных командах.
3. Контракты создания `OsrInputPump` обновлены для накопленного набора аргументов `clickLab, keyboardLab, imeCommitLab`: старые строковые сравнения ломались после следующего этапа.
4. `scripts/tests/test_eurooffice_stacked_contract_integrity.py` закрепляет инварианты (корень репозитория, разделение CEF команд и накопленные opt-in флаги). Добавлен этап `eurooffice-stacked-contract-integrity-tests` перед offline restore в `scripts/office_acceptance.py`, а также контроль его регистрации в тестах самого runner.

## Что пока НЕ завершено

| Блок | Текущий честный статус | Условие закрытия |
| --- | --- | --- |
| EuroOffice Desktop как основной offline engine | Кроссплатформенный embedded редактор **не подтверждён**. Путь Wayland CEF OSR — лабораторный | Реальный редактор в GET вкладке без стороннего HWND/XID, открытие/изменение/сохранение DOCX/XLSX/PPTX |
| Windows 10/11, Astra X11/ALT/РЕД | Для неподтверждённых OSR сред остаётся ограниченный LibreOffice/UNO fallback | Официальный поддерживаемый и проверенный нативный API/OSR; отказ от небезопасного reparenting |
| CEF input/IME | Lab pointer/click/keyboard, ограниченный committed BMP, локальный preedit; отсутствие доверенной CEF caret telemetry | Подлинная callback-provenance, expiry, текстовый ввод с системной IME, Unicode, кандидаты и focus/replay E2E |
| Clipboard и сохранение | Системный clipboard заблокирован; CEF save/writeback недоступны | Типизированные разрешения и DLP; безопасный Save/Save As, crash recovery и CAS/WORM |
| Совместное редактирование | Готовые схемы/заглушки не подтверждают сервер | Изолированный Nextcloud/EuroOffice server с SSO, ACL, HTTPS и проверкой 2/5/10 одновременных редакторов |
| Печать | Собственный мастер ГЭТ нельзя обходить из CEF | Отправка из Word/Calc/Impress через GET print master и тесты с реальным Windows spooler / CUPS |
| Пакеты/лицензии | Community EuroOffice Desktop Editors не подписаны | Обсуждение AGPL-3.0 compliance; проверка воспроизводимой сборки, SHA и подписи; для управляемого rollout рассмотреть подписанные Nextcloud Office Desktop Editors |
| Регрессия и совместимость | Статические тесты исправлены, **не выполнены** | Локальный Python/MSTest, сборка, GUI/CEF, 80 OOXML fixtures, дифф/round-trip с Microsoft Office, DPI/мультимониторность |

### Порядок локальной приёмки

1. Запустить `python -m unittest discover -s scripts/tests -p 'test_eurooffice_stacked_contract_integrity.py'` и четыре исправленных Python-теста, **без CEF и без сети**.
2. Проверить `python scripts/office_acceptance.py --help` и затем выполнить локальный acceptance только с одобренным офлайн NuGet feed и изолированным тестовым стендом согласно runner.
3. При наличии согласованной автономной VM проверить CMake/Qt/CEF и реальный OS IME; не использовать реальные документы ГЭТ в CEF без подтверждённой OS sandbox.
4. Лишь после выполнения каждого выпускающего gate проводить интеграцию в нормальный лаунчер и рассматривать merge всего stacked PR графа. GitHub Actions не включать. До приёмки `main` и корпоративные ассоциации файлов не менять.

**Решение:** GD-31J.19 — только ремонт test инфраструктуры. Функциональная задача из [issue #59](https://github.com/avers2604/get-pdf/issues/59) остаётся открытой. Закрывать её или называть EuroOffice Desktop готовым нельзя.

Рекомендации по build track и подписи: [Euro-Office Choose a build](https://euro-office.github.io/documentation/installation/releases/), [Desktop Editors](https://github.com/Euro-Office/DesktopEditors).
