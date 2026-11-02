# GD-31J.27ai — усиление deny-only IPC и устранение дефекта регрессионного теста

**Статус:** изменения в отдельной stacked Draft-ветке поверх GD-31J.27ah / PR #147. **Реальная Astra Linux VM, три UID и настоящий IPC не запускались; release НЕ разрешён.**

## Проверенная проблема GD-31J.27ah

Тест `test_invalid_scm_rights_packet_closes_every_received_fd` подменял `recvmsg` функцией с двумя позиционными параметрами. Код `_recv_three` вызывает `recvmsg(data_size, control_size, MSG_CMSG_CLOEXEC)` **с тремя**. Поэтому тест выбрасывал `TypeError` до проверки обязательного `os.close`. Исправлено соответствие mock-сигнатуры.

## Изменения

1. В `native_saveas_manual_triparty_devnull.py` после `F_GETFL` проверяется `F_GETFD & FD_CLOEXEC` у каждого из трёх передаваемых `/dev/null` FD. Отсутствие close-on-exec — `LabError` и отказ. К штатным документам это не относится.
2. При получении Unix `SCM_RIGHTS` с неполным остатком управляющих байтов `_extract_any_rights` извлекает **только полные int-дескрипторы**. Ветка отказа `_recv_three` пытается закрыть все извлечённые FD. Не принимается усечённый или лишний ancillary packet; правило `MSG_TRUNC|MSG_CTRUNC` остаётся.
3. В `test_native_saveas_manual_triparty_devnull.py` исправлена mock-сигнатура и добавлены проверки отсутствия `FD_CLOEXEC` и очистки при частично усечённом control message.
4. Source contract `test_native_saveas_manual_triparty_contract.py` закрепляет наличие `F_GETFD`, `FD_CLOEXEC` и разбора целых FD при усечённом ancillary.

## Ограничения и проверка

- **Не выполнять** `supervisor`, `broker`, `worker` или реальную передачу FD на production ПК. Ручной IPC допускается только на отдельно подготовленной, независимо подтверждённой disposable OFFLINE Astra Linux VM **без корпоративных файлов и mount points** по runbook GD-31J.27ah.
- Для проверки **только mock/source тестов** на локальной машине из `probes/eurooffice-desktop/cef_lab`:

```sh
python3 -E -s -S -m unittest -v \
  test_native_saveas_manual_triparty_devnull \
  test_native_saveas_manual_triparty_contract
```

- После независимого security-review VM: отдельные роли `UID/GID 0/0`, `44001/44003`, `44002/44003`, фиксированные каталоги `/run`, строго три `O_RDONLY /dev/null`, `SO_PEERCRED` и `MSG_CMSG_CLOEXEC`. Все три роли возвращают exit `2`; единственный допустимый ответ `denied`.
- Ни один mock/source тест **не удостоверяет** реальный kernel transport, отсутствие документов, pidfd, MAC/seccomp, правдивость VM, сохранение OOXML или допустимость publication. Не запускать GitHub Actions. Не сливать в `main` до ручных проверок и утверждения.
- Новые автоматические прогоны **не заявлены**; фактическое выполнение mock suites и реального VM smoke E2E требует отдельного протокола с точными результатами.

**Следующий этап GD-31J.27aj:** на независимой disposable VM выполнить локальные тесты и ручной deny-only smoke test; устранить выявленные runtime-дефекты перед подключением штатного supervisor/bridge/worker. Полномочия SaveAs и публикации по-прежнему отсутствуют.
