using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27cf: read-only laboratory inspection of two DISTINCT caller-held
/// streams: a real OfficeRecoveryStore manifest and an actual OOXML copy.
/// No pathname opening, no export, no mutation, no restore/delete/publish.
/// In particular, manifest.SourcePath is UNTRUSTED METADATA and is NEVER
/// passed to File/Directory/Path operations.
/// Repeated reads and SHA-256 cannot authenticate a maliciously replaced
/// pair or prove an atomic namespace snapshot. Not wired to production.
/// </summary>
internal static class RecoveryHeldProductionOoxmlReader
{
    internal const long MaxCopyBytes = 128L * 1024 * 1024;
    private const long MaxEntryBytes = 128L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxEntryCount = 4096;
    private const int MaxCompressionRatio = 500;
    internal static bool ProductionReaderEnabled => false;
    internal static bool CanOpenManifestSourcePath => false;
    internal static bool CanAuthorizeCleanup => false;

    internal static async Task<RecoveryHeldProductionOoxmlReadbackResult>
        ReadOnHeldStreamsAsync(
            Stream heldCopy, Stream heldProductionManifest,
            string expectedSourcePath, string expectedExtension,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heldCopy);
        ArgumentNullException.ThrowIfNull(heldProductionManifest);
        if (ReferenceEquals(heldCopy, heldProductionManifest))
            throw new ArgumentException(
                "Копия и манифест должны иметь разные удерживаемые потоки.");
        if (expectedExtension is not (".docx" or ".xlsx" or ".pptx") ||
            string.IsNullOrWhiteSpace(expectedSourcePath))
            throw new ArgumentException(
                "Нужны внешний ожидаемый SourcePath и формат DOCX/XLSX/PPTX.");
        if (heldCopy is FileStream != heldProductionManifest is FileStream)
            throw new ArgumentException(
                "Оба потока должны быть либо FileStream, либо тестовыми потоками.");
        if (!heldCopy.CanRead || !heldCopy.CanSeek ||
            !heldProductionManifest.CanRead || !heldProductionManifest.CanSeek)
            throw new ArgumentException(
                "Копия и манифест должны быть уже открытыми seekable read streams.");
        cancellationToken.ThrowIfCancellationRequested();

        // Treat source path as a byte-for-byte label ONLY. Never normalize,
        // traverse, reopen, resolve symlinks, or touch an original document.
        var firstManifest = await ReadManifestAsync(
            heldProductionManifest, cancellationToken);
        var firstCopy = await ReadCopyAsync(
            heldCopy, expectedExtension, cancellationToken);
        var secondManifest = await ReadManifestAsync(
            heldProductionManifest, cancellationToken);
        var secondCopy = await ReadCopyAsync(
            heldCopy, expectedExtension, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (firstManifest != secondManifest || firstCopy != secondCopy)
            throw new InvalidDataException(
                "Второе чтение копии или рабочего манифеста отличается от первого.");

        if (!string.Equals(firstManifest.Claim.SourcePath, expectedSourcePath,
                StringComparison.Ordinal) ||
            !string.Equals(firstManifest.Claim.Sha256, firstCopy.Sha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Рабочий манифест не совпадает с заявленным источником " +
                "или фактическим SHA-256 удерживаемой копии.");

        if (heldCopy is FileStream &&
            (firstCopy.NativeFileId == firstManifest.NativeFileId ||
             firstCopy.NativeFileId.DeviceOrVolume !=
                 firstManifest.NativeFileId.DeviceOrVolume))
            throw new InvalidDataException(
                "Копия и рабочий манифест являются алиасами или лежат на разных томах.");

        return new RecoveryHeldProductionOoxmlReadbackResult(
            RecoveryHeldProductionOoxmlFinding.MatchedTwiceUntrusted,
            firstCopy.Sha256,
            firstManifest.Sha256,
            firstManifest.Claim.CapturedUtc,
            firstCopy.ValidatedEntries,
            firstCopy.TotalExpandedBytes,
            heldCopy is FileStream);
    }

    private static async Task<RecoveryHeldProductionManifestPass> ReadManifestAsync(
        Stream heldManifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long length = heldManifest.Length;
        if (length is < 2 or > RecoveryHeldProductionManifestCodec.MaxManifestBytes)
            throw new InvalidDataException(
                "Рабочий манифест выходит за лимит 2048 байт.");

        RecoveryNativeFileId before = FileIdIfHeldFile(heldManifest);
        heldManifest.Position = 0;
        byte[] bytes = new byte[(int)length];
        await heldManifest.ReadExactlyAsync(bytes, cancellationToken);
        if (heldManifest.Length != length)
            throw new InvalidDataException(
                "Размер удерживаемого манифеста изменился при чтении.");
        RecoveryHeldProductionManifestCandidate claim =
            RecoveryHeldProductionManifestCodec.DecodeCandidate(bytes);
        RecoveryNativeFileId after = FileIdIfHeldFile(heldManifest);
        if (before != after)
            throw new InvalidDataException(
                "File ID удерживаемого манифеста изменился при чтении.");
        cancellationToken.ThrowIfCancellationRequested();
        heldManifest.Position = 0;

        return new RecoveryHeldProductionManifestPass(claim,
            Convert.ToHexString(SHA256.HashData(bytes)), before);
    }

    private static async Task<RecoveryHeldProductionCopyPass> ReadCopyAsync(
        Stream heldCopy, string extension, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long length = heldCopy.Length;
        if (length is < 1 or > MaxCopyBytes)
            throw new InvalidDataException(
                "OOXML-копия пуста или больше 128 МиБ.");
        RecoveryNativeFileId before = FileIdIfHeldFile(heldCopy);

        heldCopy.Position = 0;
        string firstSha = Convert.ToHexString(
            await SHA256.HashDataAsync(heldCopy, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (heldCopy.Length != length)
            throw new InvalidDataException(
                "Размер OOXML-копии изменился после первого хеширования.");

        heldCopy.Position = 0;
        (int entries, long expanded) = InspectZipEnvelope(
            heldCopy, extension, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (heldCopy.Length != length)
            throw new InvalidDataException(
                "Размер OOXML-копии изменился во время разбора ZIP.");

        // Hash the SAME already-open object again after decompression to
        // catch some concurrent content changes, including equal-size edits.
        heldCopy.Position = 0;
        string secondSha = Convert.ToHexString(
            await SHA256.HashDataAsync(heldCopy, cancellationToken));
        RecoveryNativeFileId after = FileIdIfHeldFile(heldCopy);
        if (heldCopy.Length != length || before != after ||
            !string.Equals(firstSha, secondSha, StringComparison.Ordinal))
            throw new InvalidDataException(
                "OOXML-копия поменялась при проверке пакета.");
        cancellationToken.ThrowIfCancellationRequested();
        heldCopy.Position = 0;
        return new RecoveryHeldProductionCopyPass(
            firstSha, before, entries, expanded, length);
    }

    private static (int Entries, long ExpandedBytes) InspectZipEnvelope(
        Stream openedCopy, string extension, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(openedCopy, ZipArchiveMode.Read,
            leaveOpen: true);
        if (archive.Entries.Count is < 2 or > MaxEntryCount)
            throw new InvalidDataException(
                "Число частей OOXML вне диапазона 2..4096.");
        string mainPart = extension switch
        {
            ".docx" => "word/document.xml",
            ".xlsx" => "xl/workbook.xml",
            ".pptx" => "ppt/presentation.xml",
            _ => throw new ArgumentException("Неподдерживаемый OOXML формат.")
        };

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName;
            string[] segments = name.TrimEnd('/').Split('/');
            if (string.IsNullOrEmpty(name) ||
                name.StartsWith("/", StringComparison.Ordinal) ||
                name.Contains('\\') || name.Contains(':') ||
                name.Any(char.IsControl) ||
                segments.Any(s => s.Length == 0 || s is "." or "..") ||
                !names.Add(name.TrimEnd('/')))
                throw new InvalidDataException(
                    "OOXML содержит дубли или запрещённые ZIP-пути.");

            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                name.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("activeX/", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("/activeX/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "В обычном OOXML обнаружена ссылка, макрос или ActiveX.");

            if (name.EndsWith("/", StringComparison.Ordinal))
            {
                if (entry.Length != 0)
                    throw new InvalidDataException(
                        "ZIP-каталог не может содержать данные.");
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaxEntryBytes ||
                expanded > MaxExpandedBytes - entry.Length ||
                (entry.Length > 4096 &&
                    (entry.CompressedLength == 0 ||
                     entry.Length > entry.CompressedLength * (long)MaxCompressionRatio)))
                throw new InvalidDataException(
                    "Недопустимый распакованный размер или коэффициент сжатия.");
            expanded += entry.Length;
            ValidateZipPart(entry, cancellationToken);
        }

        if (!names.Contains("[Content_Types].xml") || !names.Contains(mainPart))
            throw new InvalidDataException(
                "В OOXML отсутствуют [Content_Types].xml или основная часть.");
        return (archive.Entries.Count, expanded);
    }

    private static void ValidateZipPart(
        ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            using Stream content = entry.Open();
            if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                using var xml = XmlReader.Create(content, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxEntryBytes,
                    IgnoreComments = true
                });
                bool rootSeen = false;
                while (xml.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (xml.NodeType == XmlNodeType.Element)
                        rootSeen = true;
                }
                if (!rootSeen)
                    throw new InvalidDataException(
                        "XML-часть OOXML не содержит корневого элемента.");
            }
            else
            {
                byte[] buffer = new byte[65536];
                long readTotal = 0;
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    readTotal += read;
                    if (readTotal > entry.Length || readTotal > MaxEntryBytes)
                        throw new InvalidDataException(
                            "ZIP-часть превышает разрешённый размер.");
                }
                if (readTotal != entry.Length)
                    throw new InvalidDataException(
                        "ZIP-часть короче объявленной длины.");
            }
        }
        catch (Exception ex) when (ex is XmlException or NotSupportedException)
        {
            throw new InvalidDataException(
                "Повреждённая или запрещённая OOXML ZIP/XML-часть.", ex);
        }
    }

    private static RecoveryNativeFileId FileIdIfHeldFile(Stream stream) =>
        stream is FileStream file
            ? RecoveryNativeHandleIdProbe.ReadFromExistingHandle(
                file.SafeFileHandle)
            : default;
}

internal enum RecoveryHeldProductionOoxmlFinding
{
    MatchedTwiceUntrusted
}

internal readonly record struct RecoveryHeldProductionManifestPass(
    RecoveryHeldProductionManifestCandidate Claim,
    string Sha256, RecoveryNativeFileId NativeFileId);

internal readonly record struct RecoveryHeldProductionCopyPass(
    string Sha256, RecoveryNativeFileId NativeFileId,
    int ValidatedEntries, long TotalExpandedBytes, long EncodedLength);

internal readonly record struct RecoveryHeldProductionOoxmlReadbackResult(
    RecoveryHeldProductionOoxmlFinding Finding,
    string CopySha256,
    string ManifestBytesSha256,
    DateTimeOffset CapturedUtc,
    int ValidatedEntries,
    long TotalExpandedBytes,
    bool UsedHeldNativeFileStreams)
{
    internal bool OoxmlEnvelopeParsed => true;
    internal bool HasIndependentManifestAuthentication => false;
    internal bool HasVerifiedNameBinding => false;
    internal bool HasVerifiedDurability => false;
    internal bool CanRestore => false;
    internal bool CanDelete => false;
    internal bool CanPublish => false;
}
