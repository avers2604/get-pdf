using System.IO.Compression;
using System.Xml;

namespace GetDocument.Collaboration;

/// <summary>
/// Limits for a server-validated OOXML ZIP. The wire-level HTTP limit is
/// separately enforced by WopiLiveHttpGateway. These limits protect CPU,
/// memory, filesystem and the XML parser when a small ZIP expands greatly.
/// </summary>
public sealed record OfficePackageLimits(
    long MaxPackageBytes,
    long MaxEntryUncompressedBytes,
    long MaxTotalUncompressedBytes,
    int MaxEntries,
    int MaxCompressionRatio)
{
    public static OfficePackageLimits Default { get; } =
        new(32L * 1024 * 1024, 128L * 1024 * 1024, 256L * 1024 * 1024, 4096, 500);
}

/// <summary>
/// Unsupported, oversized, malformed or potentially active Office ZIP payload.
/// The HTTP layer must return an appropriate 4xx, not accept a revision.
/// </summary>
public sealed class RejectedOfficePackageException : InvalidDataException
{
    public RejectedOfficePackageException(string message) : base(message) { }
    public RejectedOfficePackageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Defensive package preflight before a collaborative PutFile is persisted.
/// It intentionally does not extract entries or follow external hyperlinks.
/// A trusted AV scan, malware policy and full OOXML semantics remain separate.
/// </summary>
public static class OfficePackageGuard
{
    public static void Validate(
        Stream source, string fileName, OfficePackageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        limits ??= OfficePackageLimits.Default;

        if (!source.CanRead || !source.CanSeek)
            throw new RejectedOfficePackageException("OOXML должен передаваться читаемым позиционируемым потоком.");
        if (limits.MaxPackageBytes <= 0 || limits.MaxEntryUncompressedBytes <= 0 ||
            limits.MaxTotalUncompressedBytes <= 0 || limits.MaxEntries <= 0 ||
            limits.MaxCompressionRatio <= 0)
            throw new ArgumentException("Пределы проверки OOXML должны быть положительными.", nameof(limits));

        long originalPosition = source.Position;
        try
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            string requiredPart = extension switch
            {
                ".docx" => "word/document.xml",
                ".xlsx" => "xl/workbook.xml",
                ".pptx" => "ppt/presentation.xml",
                _ => throw new RejectedOfficePackageException(
                    "Только DOCX/XLSX/PPTX допускаются в совместную редакцию.")
            };

            if (source.Length is < 22 || source.Length > limits.MaxPackageBytes)
                throw new RejectedOfficePackageException("Недопустимый размер OOXML контейнера.");

            source.Position = 0;
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is 0 || archive.Entries.Count > limits.MaxEntries)
                throw new RejectedOfficePackageException("В OOXML слишком много частей.");

            long expandedTotal = 0;
            var partNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string path = entry.FullName;
                if (!SafePartName(path))
                    throw new RejectedOfficePackageException("Опасный путь внутри OOXML.");
                if (!partNames.Add(path.TrimEnd('/')))
                    throw new RejectedOfficePackageException("Повторяющееся имя части OOXML.");

                // UNIX file type 0120000 = symbolic link. No ZIP entry should
                // become a filesystem link even though this guard never extracts.
                int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixFileType == 0xA000)
                    throw new RejectedOfficePackageException("Символическая ссылка внутри OOXML запрещена.");

                bool directory = path.EndsWith("/", StringComparison.Ordinal);
                if (directory)
                {
                    if (entry.Length != 0)
                        throw new RejectedOfficePackageException("Каталог ZIP содержит байты.");
                    continue;
                }

                if (ContainsActiveCode(path))
                    throw new RejectedOfficePackageException(
                        "Макросы VBA и ActiveX запрещены в этом профиле совместного редактирования.");
                if (entry.Length < 0 || entry.Length > limits.MaxEntryUncompressedBytes)
                    throw new RejectedOfficePackageException("Часть OOXML слишком велика.");

                if (expandedTotal > limits.MaxTotalUncompressedBytes - entry.Length)
                    throw new RejectedOfficePackageException("Общий распакованный OOXML превышает лимит.");
                expandedTotal += entry.Length;

                if (entry.Length > 4096 &&
                    (entry.CompressedLength == 0 ||
                     entry.Length > entry.CompressedLength * (long)limits.MaxCompressionRatio))
                    throw new RejectedOfficePackageException("Подозрительная степень сжатия ZIP.");

                // Consume and count the actual decompressed bytes; central
                // directory claims are not sufficient to validate a stream.
                using (Stream uncompressed = entry.Open())
                {
                    byte[] buffer = new byte[64 * 1024];
                    long actualLength = 0;
                    while (true)
                    {
                        int count = uncompressed.Read(buffer, 0, buffer.Length);
                        if (count == 0) break;
                        actualLength += count;
                        if (actualLength > entry.Length ||
                            actualLength > limits.MaxEntryUncompressedBytes)
                            throw new RejectedOfficePackageException("Часть ZIP превышает заявленный размер.");
                    }
                    if (actualLength != entry.Length)
                        throw new RejectedOfficePackageException("Неверный размер распакованной части.");
                }

                if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    using Stream xml = entry.Open();
                    using var reader = XmlReader.Create(xml, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = Math.Min(
                            limits.MaxEntryUncompressedBytes, 128L * 1024 * 1024),
                        IgnoreComments = true
                    });
                    while (reader.Read()) { }
                }
            }

            if (!partNames.Contains("[Content_Types].xml") ||
                !partNames.Contains(requiredPart))
                throw new RejectedOfficePackageException(
                    "Отсутствуют обязательные части указанного OOXML-формата.");
        }
        catch (RejectedOfficePackageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
                                   XmlException or NotSupportedException or OverflowException)
        {
            throw new RejectedOfficePackageException("Повреждён или запрещён OOXML ZIP.", ex);
        }
        finally
        {
            source.Position = originalPosition;
        }
    }

    private static bool SafePartName(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] == '/' ||
            path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl))
            return false;
        string[] parts = path.TrimEnd('/').Split('/');
        return parts.Length > 0 &&
               parts.All(p => p.Length > 0 && p is not ("." or ".."));
    }

    private static bool ContainsActiveCode(string path)
    {
        return path.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/activeX/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("activeX/", StringComparison.OrdinalIgnoreCase);
    }
}
