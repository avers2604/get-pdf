# GD-31J.27be — изолированная лаборатория идентичности файлов

Два **автономных диагностических прототипа**, которые **не используются** `GetDocument.Office`, `OfficeRecoveryStore`, реальными документами или сервисами GUP-2. Оба работают только с метаданными/дескрипторами файлов: **не читают содержимое OOXML, не восстанавливают, не изменяют и не удаляют документы**.

## Назначение

| Прототип | Что проверяется | Чего **не** доказывает |
|---|---|---|
| `windows/fileid_identity_probe.c` | Win32 `GetFileInformationByHandleEx(FileIdInfo)`, полный 128-бит File ID и volume serial для root/parent/copy, reparse-атрибут последней компоненты, read-only handle | `CreateFileW` по полному пути **не является** безопасным root-handle-relative open. Может разрешить промежуточные junction/symlink. Это **только диагностика** |
| `linux/openat2_identity_probe.c` | `openat2(dirfd, relative_name)` с `RESOLVE_BENEATH \\| RESOLVE_NO_SYMLINKS \\| RESOLVE_NO_MAGICLINKS \\| RESOLVE_NO_XDEV`; чтение `st_dev/st_ino` из тех же открытых fd через `fstat` | Входной root **должен быть доверенным**: `open(root, O_NOFOLLOW)` не запрещает ссылки в промежуточных компонентах самого абсолютного root. Не является полной схемой безопасного восстановления |

Формат входных данных: **абсолютный корень recovery**, 64 ASCII hex символа хэша исходного документа, basename `<GUID-N>.docx/.xlsx/.pptx` или `<GUID-N>.part.docx/.part.xlsx/.part.pptx`. Нельзя передавать настоящие документы/каталоги с данными пользователей; на стенде создать отдельную временную synthetic recovery-иерархию.

### Только после отдельного разрешения на стенд

**Windows 10/11 — MSVC Developer Command Prompt, локальный NTFS:**

```bat
cl /nologo /W4 /utf-8 /DUNICODE /D_UNICODE probes\recovery-native-identity\windows\fileid_identity_probe.c
fileid_identity_probe.exe C:\synthetic-recovery AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA 0123456789abcdef0123456789abcdef.docx
```

**Astra Linux — на машине с заголовками `linux/openat2.h` и ядром, реализующим `SYS_openat2`:**

```sh
cc -std=c11 -Wall -Wextra -Werror -O2 -o /tmp/openat2_identity_probe probes/recovery-native-identity/linux/openat2_identity_probe.c
/tmp/openat2_identity_probe /tmp/synthetic-recovery AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA 0123456789abcdef0123456789abcdef.docx
```

Сборка и выполнение **не проводились**. Команды приведены для будущего изолированного стенда и могут потребовать коррекции SDK/ABI по результатам компиляции.

### Матрица отрицательных сценариев

1. Корректная синтетическая копия на локальных NTFS/ext4: вывести устойчивые идентификаторы открытых объектов.
2. Ссылка вместо recovery-копии и вместо хэш-каталога: отказ; цель ссылки не изменена.
3. Ссылка или junction **выше доверенного корня**: Windows-диагностика может последовать по ней; не принимать её успешный вывод за защиту. Linux root bootstrap тоже не доказывает отсутствие промежуточных ссылок.
4. Подмена пути во время открытия и recheck: сравнить действительные Win32 File IDs / Linux dev+ino. Никакой проверки по одному имени.
5. Unsupported `openat2`, отсутствие заголовков/ядра, `EXDEV/ELOOP/ENOSYS`: отказ; **никакого** автоматического `openat()`/обычного `open()` fallback.
6. SMB: неполные File IDs, reconnection, failover, временная недоступность — отказ; никаких прав на восстановление, если identity не подтверждена.
7. 128 MiB и пустой файл, неправильное имя, alternate data stream, `../`, вложенные mount points — отказ.

Сами прототипы ничего не передают в `RecoveryNativeReadLease`. Существующие `WindowsRecoveryNativeIdentityAdapter` и `LinuxRecoveryNativeIdentityAdapter` сохраняют `SupportsVerifiedDirectoryRelativeOpen=false` и всегда отказывают. Для включения в production нужен отдельный проект с доказанным безопасным открытием root/parent, проверкой полного идентификатора и выдачей **того же открытого дескриптора** потребителю до конца восстановления; простая подстановка этих проб не допускается.

### Подтверждающие спецификации

- https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info
- https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-operations
- https://man7.org/linux/man-pages/man2/openat2.2.html
- https://man7.org/linux/man-pages/man2/stat.2.html

**Выпуск:** Draft, Windows/Astra/SMB manual checks = `not-run`, `releaseReady=false`.
