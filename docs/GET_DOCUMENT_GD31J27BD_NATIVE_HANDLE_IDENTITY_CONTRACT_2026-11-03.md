# GD-31J.27bd — контракты Windows File ID / Linux inode для будущего безопасного открытия recovery-файлов

**Репозиторий:** `avers2604/get-pdf`; **ветка:** `feature/gd31j27bd-native-identity-contracts`, Draft поверх GD-31J.27bc / PR #168.

**Режим работ:** только GitHub-изменения исходников/сценариев; сборка, MSTest, Python unittest, Windows NTFS, Astra Linux/ext4 и SMB испытания **не выполнялись**. `main`, GitHub Actions, WORM, GUP-2, native EuroOffice SaveAs и пользовательские документы не изменялись.

## Решение: подготовить строгий слой, не включать недоказанный native open

Последний работающий путь `OfficeRecoveryStore.OpenRecoveryReadStream()` (GD-31J.27bb–bc) делает path-based preflight + post-open повторную проверку и читает SHA/ZIP через один дескриптор. Он **не** гарантирует атомарного запрета символических ссылок, доказанную идентичность OS file ID или безопасную выдачу копии после закрытия дескриптора.

В GD-31J.27bd добавлены **два отдельных C# модуля**, которые пока никак не подключены к `OfficeRecoveryStore`:

1. `src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityContracts.cs` — типы идентификации и проверяемая политика **fail-closed**.
2. `src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityAdapters.cs` — отдельные Windows/Linux адаптеры, оба **намеренно отключены** (`SupportsVerifiedDirectoryRelativeOpen=false` и `PlatformNotSupportedException`).

Никакие платформенные P/Invoke, подозрительные `FileOptions`, скрытый fallback к `FileInfo`, перемещение или удаление recovery-файлов в этот этап не вводились.

## Типизированные контракты

- `RecoveryRelativeKey`: 64 ASCII-hex символа SHA-256-ключа каталога документа + имя `<GUID-N>.docx/.xlsx/.pptx` или `<GUID-N>.part.docx/.part.xlsx/.part.pptx`. Запрещены произвольные/абсолютные пути, `../`, обратные слэши, ADS `:`, дополнительные расширения и JSON-манифесты. Даже `default(RecoveryRelativeKey)` повторно проверяется **до** вызова адаптера.
- `RecoveryNativeFileId`: `Platform`, `DeviceOrVolume`, `ObjectLow`, `ObjectHigh`. **Windows** резервирует полные 128 бит File ID; **Linux** допускает только 64-битный `st_ino` (`ObjectHigh=0`) плюс `st_dev`. Значения `0` / частичные идентификаторы не считаются достаточными доказательствами для release-gate (консервативная политика).
- `IRecoveryNativeIdentityAdapter`: требует декларировать платформу, доступность *подтверждённого* открытия относительно доверенного родительского дескриптора с no-follow и метод `OpenReadOnlyRelative(key)`.
- `RecoveryNativeReadLease`: владение одним открытым `FileStream` и показания OS file identity для родителя и файла до/после открытия. Все значения **в реальном адаптере должны измеряться по OS-дескрипторам**, а не по строке пути. Дескриптор живёт пока lease не `Dispose`-нут.
- `RecoveryNativeIdentityGate.OpenVerifiedRead()` повторно проверяет относительное имя, поддержку платформы, полный и стабильный ID файла/родителя, единый volume/device, no-follow и разрешение только чтения, возможность seek, предел 128 МиБ. При любом неполном подтверждении — `InvalidDataException`, lease освобождается; если адаптер не поддерживает режим — `PlatformNotSupportedException` до открытия.

**Нюанс:** interface/gate не могут самостоятельно *доказать* честность показаний native backend. До реализации/аудита конкретных OS-адаптеров и испытаний на целевой файловой системе значения от тестовых фейков — только проверка политики, а не безопасности ОС.

## Windows-адаптер — требования к следующему этапу

Для будущей реализации `WindowsRecoveryNativeIdentityAdapter`:

- Безопасный открытый **доверенный дескриптор** recovery-root и каталога отдельного документа, запрет или проверка reparse-point на **всех компонентах пути** (не путать с `FileInfo.LinkTarget`).
- `GetFileInformationByHandleEx(FileIdInfo)` для получения volume serial и 128-бит File ID **по тому же дескриптору**, который будет читать SHA/ZIP. Вариант `GetFileInformationByHandle` предоставляет 64-бит FileIndex для некоторых FS — он **не заменяет** полный 128-бит ID в новом контракте.
- Проверка неизменности идентичности открытого родителя и файла при повторном чтении. Отдельно исследовать ограничения Win32 relative open (не приписывать `CreateFileW` директивную семантику `openat` без доказательства), native `NtCreateFile`/RootDirectory рассматривать только в изолированном прототипе.
- **SMB:** на некоторых сетевых серверах File ID может отсутствовать, быть частичным либо вести себя иначе после failover. Требуется явный `unsupported` вместо фиктивных ID, а также сохранение ранее верифицированного снимка без удаления.

Источники:
- Microsoft, `GetFileInformationByHandleEx`: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
- Microsoft, `GetFileInformationByHandle` (включая различие сетевых файловых систем): https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle
- .NET `File.OpenHandle`: https://learn.microsoft.com/en-us/dotnet/api/system.io.file.openhandle

## Astra Linux / ALT / РЕД — требования к следующему этапу

Для `LinuxRecoveryNativeIdentityAdapter`:

- Открыть доверенный каталог через `dirfd`, затем разрешать только **проверенный относительный** ключ каталога и GUID-имя копии.
- При достаточном ядре Linux `openat2` + `RESOLVE_BENEATH` / `RESOLVE_NO_SYMLINKS` / `RESOLVE_NO_MAGICLINKS`. Поддержка `openat2` появляется с Linux **5.6**, но конфигурацию и ABI конкретной Astra Linux необходимо сверить на стенде.
- Возможный fallback `openat` + `O_NOFOLLOW` должен отдельно проверять **каждый** компонент пути и удерживать соответствующие дескрипторы; `O_NOFOLLOW` для последней компоненты **не** защищает промежуточные каталоги.
- Идентичность файла `fstat(fd).st_dev + st_ino` и каталога — по открытым дескрипторам, а не по `stat(path)`; проверка изменения состояния после чтения; отсутствие полного доказательства = отказ.
- Следить за ABI структур `stat` и системных вызовов на целевых архитектурах, не копировать layout из другой libc/CPU. Для сетевых томов — независимый SMB-механизм проверки.

Источники:
- `openat2(2)`: https://man7.org/linux/man-pages/man2/openat2.2.html
- `openat(2)`: https://man7.org/linux/man-pages/man2/open.2.html

## Написанные регрессионные сценарии (не запускались)

`tests/GetDocument.Office.Tests/OfficeRecoveryNativeIdentityContractTests.cs`: **10** синтетических MSTest:

1. Разрешённые GUID-OOXML имена и SHA-256 папка.
2. Запрет traversal, root path, ADS, JSON и нестандартных имён.
3. Различие полных Windows File ID по верхним 64 битам.
4. Различие Linux inode на разных устройствах.
5. `default(RecoveryRelativeKey)` отвергается **до** вызова адаптера.
6. Оба Windows/Linux адаптера явно недоступны.
7. Проверенный fake-lease возвращает только read-only поток и передаётся вызывающему.
8. Изменение родительского/file-ID отклоняется, дескриптор закрывается.
9. Недостаточная no-follow/dir-relative заявка и неполный SMB ID отклоняются, дескриптор закрывается.
10. Нулевой файл и несоответствие платформы отклоняются.

`scripts/tests/test_recovery_native_identity_contract.py` содержит **8 source-only unittest методов**, защищающих схему, отсутствие реальных P/Invoke, изоляцию production-открытия и регистрацию ручных ворот.

Добавлены `scripts/office_acceptance.py` + `scripts/tests/test_office_acceptance.py` и регистрация в `scripts/tests/test_recovery_chain_consistency.py`. Гейты:
- `gd31j27bd-native-identity-windows-ntfs-manual-e2e`;
- `gd31j27bd-native-identity-astra-ext4-smb-manual-e2e`.

Оба **`not-run`**, `releaseReady=false`. Изменений действующего UI, обычного пути чтения, удаления, восстановления, 128 МиБ-лимита или SHA/ZIP нет.

## Внедрение — только после независимой приёмки

1. Скомпилировать отдельные native прототипы на целевых Windows 10/11 и Astra Linux (без реальных документов).
2. Проверить P/Invoke ABI, no-follow и безопасное удержание родительского дескриптора; проверить symlink/junction, hardlink, rename race, directory swap, ADS, inode/File ID, SMB failover.
3. Проверить, что **проверенный дескриптор остаётся у потребителя** до завершения копирования/восстановления; возврат одного лишь `FilePath` из `OfficeRecoveryCopy` не обеспечивает гарантий.
4. Только после доказательств подключить платформенные адаптеры к `OfficeRecoveryStore` отдельным ревью и флагом совместимости; не включать автоматическое удаление либо обход проверок при unsupported.

```sh
# Команды для будущего разрешённого стенда — НЕ запускались.
python3 -m unittest discover -s scripts/tests -p test_recovery_native_identity_contract.py -v
python3 -m unittest discover -s scripts/tests -p test_office_acceptance.py -v
dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OfficeRecoveryNativeIdentityContractTests"
```

**Следующий этап GD-31J.27be:** подготовить отдельные прототипы Windows `FILE_ID_INFO` и Linux `openat2/fstat` в неактивной лаборатории; не использовать пользовательские файлы, не утверждать операционную безопасность до сборки и настоящих испытаний.
