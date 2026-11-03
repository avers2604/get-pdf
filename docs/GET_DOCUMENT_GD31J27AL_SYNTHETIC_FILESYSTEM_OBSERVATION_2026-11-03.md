# GD-31J.27al — проверка синтетических файловых операций без прав Save As

**Статус:** отдельная stacked Draft-ветка над GD-31J.27ak / PR #150. Это **не** запуск EuroOffice, `.NET OfficeCopyPublisher.File.Move`, Windows IPC, Linux broker, корпоративного SMB, WORM или тест с настоящими документами.

## Почему именно этот этап

Код GD-31J.27aj/ak был дополнен защитами Windows/Linux, но не выполнялись сборки C# и настоящие E2E Windows/Astra Linux. На текущем исполнителе отсутствуют `.NET 10 SDK`, Windows VM, Astra Linux VM и корпоративные ресурсы. Вместо очередного «аттестованного» синтетического JSON добавлена **исполняемая офлайн-проверка ограниченного поведения файловой системы**, и отдельно оставлены недоказанные Windows/Linux/SMB требования.

## Новый исполняемый пробник

`scripts/savecopy_fs_probe.py` — только Python stdlib, никаких подпроцессов, Windows API, root, файлов документов, сетей или SDK. **Ручная командная строка запрещена по умолчанию**, пока нет точных `GETDOC_GD31J27AL_OFFLINE_VM=NO_DOCUMENTS_DISPOSABLE_FS_ONLY` и `I_APPROVE_SYNTHETIC_DISPOSABLE_FS_PROBE`; эти флаги — НЕ независимое доказательство VM.

Внешний оператор должен создать пустой, принадлежащий ему, непрозрачный disposable-каталог с именем `getdoc-gd31j27al-disposable-<random>` в заранее проверенном изолированном VM scratch. Утилита отказывает на неканоническом имени, `..`, symlink/junction-компонентах пути, уже существующих файлах и небезопасных правах Linux. Отчёт не содержит пути, документа, PID или сетевых адресов.

При отдельном разрешённом запуске в каталоге появляются только три одноразовых случайных имени с **заранее зашитыми недокументными байтами**. Утилита:
- создаёт stage и synthetic collision-target эксклюзивно, fsync;
- через `os.link(stage, existing, follow_symlinks=False)` проверяет, что существующее имя не было перезаписано;
- через `os.link(stage, fresh, follow_symlinks=False)` проверяет появление новых синтетических байт;
- удаляет только имена, которые создала сама, без recursive delete, и оставляет sandbox-каталог оператору;
- при неподдерживаемом hardlink (включая исключение NotImplementedError) получает `unsupported_not_attested`, **никогда не положительную публикационную авторизацию**.

**Это наблюдение свойств `os.link` на данном filesystem, НЕ проверка реализации `.NET File.Move(staging,target,overwrite:false)`.** Пробник не выполняет native SaveAs, не подтверждает атомарность SMB/CAS, аварийное восстановление, ACL, OOXML или защиту от злонамеренного оператора. Отчёт всегда содержит `netFileMoveTested=false`, `nativeSaveProven=false`, `productionStorageAttested=false`, `releaseReady=false`; процесс возвращает **exit 2** даже при `observed_not_attested`.

### Пример ручной команды для отдельно утверждённой VM

После самостоятельной проверки отсутствия корпоративных mounts, сети, документов и доступа к GET-system оператор готовит **пустую** приватную папку в заранее выделенном scratch (не путь production) и сам подставляет её абсолютное имя:

```sh
export GETDOC_GD31J27AL_OFFLINE_VM=NO_DOCUMENTS_DISPOSABLE_FS_ONLY
python3 -E -s -S scripts/savecopy_fs_probe.py --sandbox /path/to/getdoc-gd31j27al-disposable-123456789abc I_APPROVE_SYNTHETIC_DISPOSABLE_FS_PROBE
# Ожидаем exit 2 и только JSON с state=observed_not_attested или unsupported_not_attested
```

Windows PowerShell, только на **отдельной disposable Windows VM**, c Python 3.12+ и разрешённым scratch:

```powershell
$env:GETDOC_GD31J27AL_OFFLINE_VM='NO_DOCUMENTS_DISPOSABLE_FS_ONLY'
python -E -s -S .\scripts\savecopy_fs_probe.py --sandbox 'C:\DISPOSABLE_LAB\getdoc-gd31j27al-disposable-123456789abc' I_APPROVE_SYNTHETIC_DISPOSABLE_FS_PROBE
# Exit 2 ВСЕГДА. Вывод JSON — лишь ограниченное наблюдение.
```

Не копировать эти команды в производственный планировщик или установщик. Оператор вручную готовит папку и по окончании отдельно уничтожает **всю disposable VM**, а не пользовательские каталоги.

## Реально выполненная локальная проверка (не Astra, не Windows)

На отдельном временном каталоге среды Linux с Python 3.13 проведён red→green тест: сначала `ModuleNotFoundError` (модуль отсутствовал), затем исправлена излишняя проверка длины уникального имени `TemporaryDirectory`, после чего **8/8 локальных тестов PASSED**. Выполнен отдельный `observe()` на временном sandbox; подтверждены `collisionPreserved=true`, `newCopyBytesVerified=true`, `netFileMoveTested=false`, `releaseReady=false`. Файлы удалены. Это не доказательство работы на NTFS/ext4/SMB.

Тесты: `scripts/tests/test_savecopy_fs_probe.py` — ранний отказ, сохранность чужого файла, symlink на root, неканонический `..` путь, реальный локальный hardlink без замены, mock unsupported, отсутствие поддержки link API на Windows, отсутствие CLI-доступа без флага. Symlink-test **не пропускается** при отсутствии права: сначала необходимо отдельно настроить офлайн-VM.

Локальная команда теста из корня репозитория:

```sh
python3 -m unittest discover -s scripts/tests -p test_savecopy_fs_probe.py -v
```

`scripts/office_acceptance.py` регистрирует этот сценарий до restore на обеих ОС; `scripts/tests/test_office_acceptance.py` проверяет его наличие и оставляет ручные E2E в статусе `not-run`.

## Что требуется перед выпуском — отдельно для трёх платформенных условий

1. **Windows NTFS:** сборка .NET 10 и запуск настоящих `OfficeCopyPublisherPlatformSafetyTests` + `OfficeCopyPublisherTests`, проверка реальной `File.Move(overwrite:false)`, файлового lock, конфликтов, антивируса, ACL, повышения прав, длинных и Unicode имён.
2. **Linux Astra/ext4:** та же сборка и файловые сценарии под обычным пользователем и Astra MAC/CUPS окружением, учёт permissions, fd cleanup, symlink/hardlink/rename, безопасный crash recovery.
3. **Отдельный offline SMB:** только синтетическая песочница и одобренная сеть/сервер, конфликты параллельной записи, offline reconnect, версия/квитанции; `os.link` может быть недоступен. SMB CAS нельзя считать атомарным на основании локального теста.

В `MANUAL_GATES` зафиксированы `gd31j27al-windows-ntfs-real-dotnet-savecopy-manual-e2e`, `gd31j27al-linux-ext4-real-dotnet-savecopy-manual-e2e`, `gd31j27al-offline-smb-real-dotnet-savecopy-manual-e2e`; все остаются непройденными. **Нет releaseReady, production SaveAs, WORM writeback, merge в main или GitHub Actions.**

**Следующий этап GD-31J.27am:** после фактических Windows/Astra стендов — выполнить кроссплатформенный .NET SaveAs E2E и ручной Windows NamedPipe deny-only E2E, устранить обнаруженные runtime-ошибки; только после этого рассматривать отдельный Windows low-privilege broker и handle-bound publication.
