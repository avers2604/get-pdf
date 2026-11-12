# GD-31J.27cf — чтение реального RecoveryManifest и OOXML через удерживаемые потоки

**Дата:** 2026-11-12. **Основа:** GD-31J.27ce, Draft PR #196. Новый этап изолирован от рабочего OfficeRecoveryStore и WORM, статус releaseReady=false, GitHub Actions не включаются.

## CF-1: существующий формат манифеста

Фактический производственный OfficeRecoveryStore.RecoveryManifest описан полями SourcePath, CapturedUtc, Sha256. Это другой формат, чем лабораторная CE-квитанция с TransactionId и JournalHeadSha256.

Новый RecoveryHeldProductionManifestCodec разбирает именно эти три существующих поля из предоставленных байтов без изменения производственного сериализатора. Максимум 2048 байт, канонический JsonSerializer JSON, отсутствие неучтённых полей и произвольных пробелов, UTC CapturedUtc, uppercase HEX SHA-256. Строка SourcePath ограничена, но рассматривается только как **недоверенные метаданные**: модуль не открывает, не нормализует и не изменяет источник по этой строке. При неподдерживаемом старом формате результат — отказ без fallback.

## CF-2: проверка настоящего OOXML

RecoveryHeldProductionOoxmlReader.ReadOnHeldStreamsAsync принимает два разных, уже открытых caller-owned seekable/readable потока: аварийную копию и её рабочий манифест. Это не экспорт, не сканирование диска и не чтение оригинального документа по SourcePath.

Дважды выполняются полные диагностические проходы: манифест (до 2048 байт) -> OOXML-копия (до 128 МиБ) -> манифест -> OOXML. Для копии в каждом проходе вычисляется SHA256.HashDataAsync по **тому же уже открытому дескриптору**, затем читается ZIP/XML, затем SHA-256 повторяется. Сравниваются длины, File ID (на FileStream), результаты проходов, предъявленный SourcePath как точная строка и фактический SHA-256 с manifest.Sha256. Если потоки — FileStream, их native File IDs не должны совпадать, том должен быть один. Не допускается чтение двух ролей из одного stream instance.

ZIP-ограничения согласованы с текущими константами OfficeRecoveryStore:
- максимальный размер OOXML-копии: 128 МиБ;
- до 4096 частей, каждая до 128 МиБ в распакованном виде, суммарно до 512 МиБ;
- коэффициент сжатия не выше 500 для частей свыше 4096 байт;
- обязательны [Content_Types].xml и word/document.xml, xl/workbook.xml либо ppt/presentation.xml по типу файла;
- отклоняются обход каталогов ZIP, дубли без учёта регистра, Unix-symlink, macro vbaProject.bin и ActiveX;
- XML проходит XmlReader с DtdProcessing.Prohibit, XmlResolver=null и ограничением символов; бинарные части считываются блоками 64 КиБ без распаковки на диск.

**Границы доверия:** ZIP-envelope не равнозначен полному семантическому аудиту OOXML; Sha256 без независимого защищённого ключа может быть подделан одновременно с манифестом. Двойное чтение не является атомарным snapshot или доказательством долговечности. В результате MatchedTwiceUntrusted все права CanRestore, CanDelete, CanPublish остаются false; HasIndependentManifestAuthentication, HasVerifiedNameBinding, HasVerifiedDurability тоже false.

## CF-3: регрессии

Подготовлено 26 C# MSTest:
- 8 RecoveryHeldProductionManifestCodecTests — производственный JSON, ограничения, защита от чужих полей, UTC и Sha256, отличие лабораторной квитанции, запрет использования SourcePath для доступа;
- 18 RecoveryHeldProductionOoxmlReaderTests — DOCX/XLSX/PPTX, хеш реальных байтов, неверный manifest/source, ZIP traversal/duplicates, ActiveX и макросы, DTD XML, усечение, лимиты, отмена, изменения между чтениями, опциональная собственная FileStream fixture.

Подготовлено 14 Python source-only проверок и добавлен offline шаг acceptance перед NuGet restore. Дополнительно 8 обязательных ручных gate, всего 219:
- gd31j27cf-production-manifest-held-json-manual-e2e
- gd31j27cf-copy-held-sha256-dual-pass-manual-e2e
- gd31j27cf-docx-xlsx-pptx-zip-guard-manual-e2e
- gd31j27cf-copy-hash-manifest-mismatch-manual-e2e
- gd31j27cf-xml-dtd-zip-bomb-rejection-manual-e2e
- gd31j27cf-windows-ntfs-smb-race-manual-e2e
- gd31j27cf-astra-ext4-held-reopen-manual-e2e
- gd31j27cf-worm-original-immutability-manual-e2e

На согласованном локальном стенде:

    python -m unittest discover -s scripts/tests -p test_recovery_cf_production_manifest_ooxml_contract.py -v
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryHeldProductionManifestCodecTests
    dotnet test tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryHeldProductionOoxmlReaderTests

Опциональный тест на реальном тестовом файле допускается только при GD31J27CF_HELD_FILE_LAB=1; создание и очистка тестовых объектов — **только внутри unit-test fixture**, никогда внутри читателя восстановления.

## P0 до включения реального восстановления

1. Подлинность manifest и copy без доверенного root/HMAC key и rollback-resistant anchor не доказана. Никаких автоматических восстановлений или удалений.
2. Привязка имени к native File ID после перезапуска не доказана. Проверка удерживаемых файлов не устраняет последующую подмену каталожных имён.
3. Windows NTFS/SMB: нужно квалифицировать ACL/token/MIC/reparse, FILE_SHARE_READ и реальное удержание имён. Astra Linux: mount namespaces, uid, LSM, ext4 и power-loss.
4. Потребуются испытания совместимости всех реальных форматов OOXML на целевой сборке и сравнение поведения нового ZIP-валидатора с рабочим OfficeRecoveryStore.EnsureValidPackage.
5. OfficeRecoveryStore.CaptureAsync сохраняет path-based File.Move как самостоятельный P0 TOCTOU риск. Автоматическое удаление Discard/Prune по-прежнему запрещено, WORM не менять.

**Статус:** исходники и тесты подготовлены в GitHub. .NET сборка, MSTest, Python unittest, NTFS/Astra/SMB E2E, fault injection и power loss ещё не запускались. Запрет выпуска: releaseReady=false; PR Draft, main и GitHub Actions не трогать.
