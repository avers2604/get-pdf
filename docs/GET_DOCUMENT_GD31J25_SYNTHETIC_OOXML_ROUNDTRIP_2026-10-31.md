# GD-31J.25 — независимая проверка synthetic-only OOXML save-as

**Дата:** 31.10.2026. **База:** GD-31J.24 / Draft PR #96. **Статус:** исходники и тесты добавлены; сборка, тесты и реальные CEF/Wayland-испытания **НЕ запускались**. Это этап проверки результата, **не реализация автоматического нативного сохранения EuroOffice**.

## Почему нужен отдельный gate

Анализ pinned upstream `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`:

- В `ChromiumBasedEditors/lib/src/cefview.cpp` существуют `LocalFile_SaveStart`, `LocalFile_SaveEnd`, `ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE_PATH` и `ASC_MENU_EVENT_TYPE_CEF_ONSAVE`.
- `LocalFile_SaveEnd` при определённых условиях проверяет наличие итогового файла, обновляет внутреннюю модель и посылает `OnSave`; в других ветках `OnSave` может означать отмену, ошибку или инициирование процесса.
- Нативный callback и `GetLocalFilePath` **не удостоверяют содержимое выходного DOCX/XLSX/PPTX** и не гарантируют, что редактирование изменило видимые данные.
- Автоматическую команду `SaveAs` через предполагаемый JS/CEF API **не изобретаем**: к pinned SDK пока нет аттестованной системы управления редактированием, пользовательским вводом и фиксированным путём записи.

## Что добавлено

1. `probes/eurooffice-desktop/cef_lab/synthetic_roundtrip.py` — исключительно читающий автономный Python-проверяющий модуль без внешних зависимостей. Нет записи, удаления, замены файлов, запуска CEF, shell, сервера, сети или GUP-2.
2. Для каждой из **трёх** существующих synthetic fixtures проверяется manifest `gd31j6-fixture-manifest.json` со схемой `gd31j6-office-fixtures/1`, точными `size/sha256`, отсутствием повторных JSON-ключей. Исходник читается дважды, до и после проверки; изменение блокирует результат.
3. Результаты допускаются только в отдельном заранее созданном каталоге `<staging>/gd31j25-output/` с точным marker `gd31j25-output-lab.marker` = `GETDOC_GD31J25_DISPOSABLE_OUTPUT_ONLY\n`. Имя конечного файла **строго фиксировано**, без пути от пользователя или аргументов произвольной записи. Символические ссылки в каждом компоненте пути и нестандартные объекты запрещены; чтение с `O_NOFOLLOW`, когда ОС поддерживает флаг. Никакой формальной гарантии против TOCTOU/атаки привилегированного соседа в этом стенде нет: нужна отдельная изолированная VM.
4. ZIP проверяется целиком (CRC каждого элемента): максимум 32 MiB на архив, 256 ZIP entries, 64 MiB суммарно в распакованном виде, 16 MiB на часть, коэффициент сжатия не более 200. Не допускаются ZIP traversal, дубли имён, symlink entries, шифрование, макросы, ActiveX, embedded binaries, внешние relationships и DOCTYPE/ENTITY. `[Content_Types].xml`, `_rels/.rels` и типоспецифичный основной XML обязательны.
5. Контрольная метка должна встретиться **ровно один раз как содержимое текстового XML-узла**, а не просто в ZIP, свойствах или комментарии:
   - DOCX: `word/document.xml`, узел `w:t`, `GD31J25_SYNTHETIC_WRITER_EDIT_6A9B`.
   - XLSX: `xl/sharedStrings.xml` или `xl/worksheets/sheetN.xml`, узел `s:t`, `GD31J25_SYNTHETIC_CALC_EDIT_7B8C`.
   - PPTX: `ppt/slides/slideN.xml`, узел `a:t`, `GD31J25_SYNTHETIC_IMPRESS_EDIT_8C9D`.
6. Строгий `EuroOfficeSyntheticRoundTripCandidate.Parse` принимает только три ожидаемых имени, точную JSON-схему и неповторяющиеся поля, подтверждённые хеши, отсутствие любых повышенных прав. **`nativeSaveProven`, `documentVerified`, `approvedForProduction` неизменно false** — даже при `syntheticPackageVerified=true`.
7. Отложенный локальный acceptance runner дополнен `synthetic-ooxml-roundtrip-verifier-tests`, а ручные критерии — `eurooffice-synthetic-ooxml-native-saveas-canary-e2e`. Добавлены Python и MSTest unit/regression сценарии (не запускались).

## Пример подготовленного ручного процесса — не исполнено

**Только** после одобрения ИБ/лицензирования, в offline disposable VM без доступа к реальным файлам, принтерам, SMB, WORM и сети:

1. Создать синтетические входные DOCX/XLSX/PPTX существующим механизмом GD-31J.6; убедиться, что созданный manifest соответствует фактическим входным байтам.
2. Создать каталог `gd31j25-output` **под тем же синтетическим staging**, вручную положить туда файл `gd31j25-output-lab.marker` с единственной точной строкой `GETDOC_GD31J25_DISPOSABLE_OUTPUT_ONLY` и завершающим LF.
3. Запустить **отдельный** нативный инструмент/GUI редактора в проверенной disposable VM с лабораторной копией входного fixture, вне штатного launcher. После фактически введённой в редактор контрольной метки выполнить сохранение **только как новый файл** соответствующего имени. `gd31j25_writer_saved.docx`, `gd31j25_calc_saved.xlsx`, `gd31j25_impress_saved.pptx`. До подтверждения `OnSave` и фактического завершения конвертации внешний verifier **не вызывать**.
4. Пример readonly вызова после того, как файл уже существует:

    ```sh
    python3 probes/eurooffice-desktop/cef_lab/synthetic_roundtrip.py \
      --staging /ABSOLUTE/APPROVED/DISPOSABLE/STAGING \
      --fixture gd31j6_writer.docx
    ```

5. Повторить для Calc, Impress; проверить визуальный результат повторным открытием **копии**, сравнить содержимое и форматирование, убедиться в неизменности исходных SHA, отсутствии записи в original/WORM. Записать отдельные операторские доказательства: утверждённый SDK commit, rebuilt DLL/SO SHA, host binary SHA, VM isolation, события native modified/OnSave, тождество session/tab и итоговые хеши.
6. Прогнать отрицательные сценарии: несогласованный source manifest, отсутствие canary, неуспешное сохранение, прерванная запись/CRC, повторный output path, вредоносный ZIP, symlink, неверный тип OOXML, forged JSON и подмена версий SDK.

Отложенный локальный тест:

```sh
python3 -m unittest discover -s probes/eurooffice-desktop/cef_lab -p test_synthetic_roundtrip.py
```

Программа выдаёт `getdoc-synthetic-ooxml-roundtrip-candidate-lab/1` с хешами и `syntheticPackageVerified=true`, но **не делает никаких заявлений о том, каким процессом записан файл**. Даже утверждённый оператором native callback не отменяет отдельной проверки результата.

## Оставшиеся обязательные шаги

- Подтверждённый native **SaveAs** путь в pinned SDK с гарантированно разрешённым только output-dir и отменой любого save-over-original; не подключать прямой IPC/JS без аудита API и явного opt-in.
- Реальное изменение каждой из трёх fixtures через CEF и фиксированный save completion callback плюс persisted bytes.
- Отдельная аттестация CEF/Wayland VM, процессов, sandbox, файловой изоляции, SHA библиотек и лицензий AGPL/допусловий.
- Открытие сохранённой копии в Microsoft Office/LibreOffice, проверка точности, негативные сценарии, Windows/Astra Linux и совместное редактирование.

**Только лаборатория. Доработанный EuroOffice пока не включён в рабочую редакцию ГЭТ Документ; текущий LibreOffice/UNO fallback, собственная оболочка, пользовательские документы и WORM остаются без изменений.** Никакой merge в `main`, GitHub Actions и автоматических сборок.
