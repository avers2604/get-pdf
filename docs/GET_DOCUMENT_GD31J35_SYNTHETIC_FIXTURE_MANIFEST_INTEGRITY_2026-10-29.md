# GD-31J.35 — действительные три синтетические OOXML-фикстуры и целостность полного манифеста

**Дата:** 29.10.2026. **База:** Draft PR #106, GD-31J.34. **Статус:** правки исходников и тестов записаны в GitHub; **ни одна сборка, Python/.NET тест, Qt/CEF, Windows/Linux/Astra VM или Microsoft Office round-trip не выполнялись** по ранее утверждённому порядку. Цель — не объявить продукт готовым, а устранить ложную достоверность данных будущих тестов.

## Установленный дефект

В `tests/GetDocument.Office.Tests/EuroOfficeSyntheticReviewedSnapshotTests.cs` старый `SyntheticLab.Create()` реально создавал ZIP-«DOCX» только из `[Content_Types].xml` и `word/document.xml`, причём без обычных OPC relationship частей, content-type overrides или основных OOXML namespaces. Вторые два документа (`gd31j6_calc.xlsx` и `gd31j6_impress.pptx`) **вообще не создавались**: записи в `gd31j6-fixture-manifest.json` были фиктивными `size=1`, `sha256=aaaa...`. При этом `EuroOfficeSyntheticReviewedSnapshot.RequireManifest` сравнивал только выбранный файл, а для `files` лишь требовал 3 записи. Сценарий DOCX мог заявить успешную проверку, даже когда весь комплект fixtures был подменён или отсутствовал.

## Изменения кода

`EuroOfficeSyntheticReviewedSnapshot.RequireManifest` теперь отказывает **до `Begin`, pending, журнала и hardlink** при любом из следующих нарушений:

- отсутствие или неверный тип `files`, недостаточное/лишнее число entries, неизвестное или повторяющееся имя ключа;
- неверные имена — допустимы **строго три**: `gd31j6_writer.docx`, `gd31j6_calc.xlsx`, `gd31j6_impress.pptx`;
- постороннее/повторяющееся поле записи или отсутствие `size`/ `sha256`; неверный тип, нулевой/превышающий 128 MiB размер; SHA не из 64 строчных шестнадцатеричных символов;
- отсутствие какого-либо из трёх файлов на диске, ссылка/reparse на их пути, несоответствие заявленной и фактической длины/SHA-256 **любой** фикстуры;
- несоответствие SHA-256 текущего выбранного файла ранее выданной нативной квитанции Save As.

Хеши всех трёх документов вычисляются по фактическим локальным байтам независимо от записи манифеста. Это не цифровая подпись и не доверенная attestation: если злоумышленник с правом записи на staging одновременно заменит файлы и манифест, такой набор нельзя считать доверенным. Остаётся обязательна изоляция тестового каталога и отдельная проверка происхождения самих fixtures.

## Тестовые данные

`SyntheticLab` теперь принимает формат `docx`, `xlsx` или `pptx`; `Create()` **всегда** создаёт все три физических пакета и строит манифест из их реальных `FileInfo.Length` и `SHA256`.

Тестовые ZIP-пакеты содержат:
- `[Content_Types].xml` с OPC MIME types и part overrides;
- `_rels/.rels` с relationship на основной пакет документа;
- Word — `word/document.xml` с WordprocessingML namespace;
- Excel — `xl/workbook.xml`, `xl/_rels/workbook.xml.rels`, `xl/worksheets/sheet1.xml` с простой строкой;
- PowerPoint — `ppt/presentation.xml`, `ppt/_rels/presentation.xml.rels`, `ppt/slides/slide1.xml` с простым текстовым объектом.

**Важно:** эти пакеты предназначены для проверки синтетической ZIP/XML цепочки и hash/manifest контракта. Они **не сертифицированы** по ISO/IEC 29500 и не доказывают, что Microsoft Office или EuroOffice откроет их без восстановления/потери оформления. Принятие структурной проверки `InspectRevision` не должно называться полноценной OOXML/Office validation. Реальную междвижковую совместимость проверяют отдельно после разрешения на стенд.

## Регрессии (написаны, не запускались)

`EuroOfficeSyntheticManifestIntegrityTests.cs` добавляет 5 сценариев:
1. `DOCX / XLSX / PPTX` — все три выбранных формата должны пройти синтетический review/commit, сохранив source/quarantine и флаги `AuthorizedForPublication=false`, `DurableAcrossPowerLoss=false`.
2. Не выбранный DOCX-сценарием XLSX повреждён после чтения receipt — Commit должен отказать до журнала.
3. Для выбранного XLSX отсутствует PPTX — должен быть отказ до журнала.
4. Манифест содержит иностранный ключ или повтор DOCX вместо XLSX — отказ.
5. Для второго/третьего невыбранного файла фальсифицирован SHA или размер — отказ.

`test_synthetic_manifest_integrity_contract.py` проверяет источник, порядок fail-closed в `Commit`, реальный генератор всех трёх files, отсутствие старых `new string('a',64)`, перечисленные регрессии. В `scripts/office_acceptance.py` добавлены offline gate `cef-synthetic-three-fixture-manifest-integrity-source-tests` перед restore и **ручной** `eurooffice-gd31j35-docx-xlsx-pptx-manifest-and-roundtrip-manual-vm`. Обновлён `scripts/tests/test_office_acceptance.py`. По договорённости ничего не запускалось.

## Остаточные ограничения

- Всё сохранение пока исключительно синтетическое, sandboxed и без WORM/GET-system/GUP-2/Nextcloud, пользовательских путей, внешней сети.
- SHA-256 не защищает от контролируемого злоумышленником одновременно manifest+bytes, не снимает проблемы гонок между проверками; нужны права каталога, capability isolation и независимое доверенное происхождение.
- `WriteThrough`/`Flush(true)` не означают выполненный fsync directory metadata и подтверждённую устойчивость к отключению питания.
- Требуются будущие локальные `dotnet build/test` (.NET 10), Python offline tests, Qt/CEF native, Windows/Linux/Astra filesystem, OOXML interop с EuroOffice / LibreOffice / Microsoft Office и лицензии, прежде чем какие-либо PR можно сливать.
- В ветке `main` и GitHub Actions ничего не менялось. PR остаётся Draft поверх #106.

**Следующий этап GD-31J.36:** устранение доказанных ошибок границ `CEF native receipt → .NET lab transport → reviewed snapshot` и окончательная предсборочная проверка API контрактов. Никаких production файлов и автоматических удалений.
