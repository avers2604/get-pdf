using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Inspect a staged file as an UNTRUSTED candidate. A result is NOT a successful
/// native save receipt, a signature, a WORM authorization, or approval to print.
/// </summary>
public sealed record EuroOfficeNativeArtifactCandidate(string Format, long Length, string Sha256)
{
    public bool AuthorizedForPublication => false;
    public bool WormUpdated => false;

    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const int MaxArchiveEntries = 4096;

    public static EuroOfficeNativeArtifactCandidate InspectRevision(
        string stagingRoot, string original, string revision)
    {
        var (source, candidate, info) = ValidatePaths(stagingRoot, original, revision);
        string extension = Path.GetExtension(candidate).ToLowerInvariant();
        if (extension is not (".docx" or ".xlsx" or ".pptx") ||
            !Path.GetExtension(source).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Формат кандидата новой редакции должен совпадать с исходным.");

        string mainPart = extension switch
        {
            ".docx" => "word/document.xml",
            ".xlsx" => "xl/workbook.xml",
            ".pptx" => "ppt/presentation.xml",
            _ => throw new ArgumentException("Недопустимый формат.")
        };
        using (var stream = File.OpenRead(candidate))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            if (archive.Entries.Count is < 2 or > MaxArchiveEntries)
                throw new InvalidDataException("Недопустимое число OOXML ZIP элементов.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long aggregate = 0;
            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.Length == 0 || name.StartsWith('/') ||
                    name.EndsWith('/') || name.Contains("//", StringComparison.Ordinal) ||
                    name.Split('/').Any(p => p is "." or "..") ||
                    !names.Add(name))
                    throw new InvalidDataException("Опасные или повторяющиеся ZIP имена.");
                if (name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("activeX", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    entry.ExternalAttributes >> 16 is int mode &&
                    (mode & 0xF000) == 0xA000)
                    throw new InvalidDataException("Макросы, ActiveX и ссылки запрещены.");
                if (entry.Length > MaxFileBytes || entry.Length < 0 ||
                    aggregate > MaxArchiveBytes - entry.Length)
                    throw new InvalidDataException("Превышен размер ZIP части.");
                if (entry.Length > 4096 &&
                    (entry.CompressedLength == 0 ||
                     entry.Length > entry.CompressedLength * 500L))
                    throw new InvalidDataException("Подозрительное сжатие ZIP части.");
                aggregate += entry.Length;

                using var content = entry.Open();
                if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = XmlReader.Create(content, new XmlReaderSettings
                        {
                            DtdProcessing = DtdProcessing.Prohibit,
                            XmlResolver = null,
                            MaxCharactersInDocument = MaxFileBytes
                        });
                        bool rootFound = false;
                        while (reader.Read())
                        {
                            if (reader.NodeType == XmlNodeType.Element)
                            {
                                rootFound = true;
                                if (reader.GetAttribute("TargetMode") == "External")
                                    throw new InvalidDataException("Внешние связи запрещены.");
                            }
                        }
                        if (!rootFound)
                            throw new InvalidDataException("Пустая XML часть.");
                    }
                    catch (XmlException ex)
                    {
                        throw new InvalidDataException("Некорректная XML часть OOXML.", ex);
                    }
                }
                else
                {
                    // Drain safely to detect truncated parts rather than trusting
                    // the central ZIP directory, without extraction to disk.
                    byte[] buffer = new byte[65536];
                    long seen = 0;
                    int count;
                    while ((count = content.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        seen += count;
                        if (seen > entry.Length)
                            throw new InvalidDataException("Размер ZIP части не соответствует объявленному.");
                    }
                    if (seen != entry.Length)
                        throw new InvalidDataException("Неполная ZIP часть.");
                }
            }
            if (!names.Contains("[Content_Types].xml") || !names.Contains(mainPart))
                throw new InvalidDataException("Не хватает обязательных частей OOXML.");
        }
        return new EuroOfficeNativeArtifactCandidate(
            extension[1..], info.Length, HashFile(candidate));
    }

    public static EuroOfficeNativeArtifactCandidate InspectPdf(
        string stagingRoot, string original, string pdfPath)
    {
        var (_, candidate, info) = ValidatePaths(stagingRoot, original, pdfPath);
        if (!Path.GetExtension(candidate).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PDF-кандидат должен иметь расширение .pdf.");
        using (var stream = File.OpenRead(candidate))
        {
            if (stream.Length < 16)
                throw new InvalidDataException("PDF слишком короткий.");
            Span<byte> prefix = stackalloc byte[5];
            stream.ReadExactly(prefix);
            if (!prefix.SequenceEqual("%PDF-"u8))
                throw new InvalidDataException("Отсутствует PDF заголовок.");
            long tailLength = Math.Min(2048, stream.Length);
            stream.Seek(-tailLength, SeekOrigin.End);
            byte[] tail = new byte[(int)tailLength];
            stream.ReadExactly(tail);
            if (!System.Text.Encoding.ASCII.GetString(tail)
                .Contains("%%EOF", StringComparison.Ordinal))
                throw new InvalidDataException("Нет завершающей отметки PDF.");
        }
        // This checks the PDF envelope only, NOT its rendering/signatures or
        // malicious embedded content. The GET PDF engine must validate further.
        return new EuroOfficeNativeArtifactCandidate("pdf", info.Length, HashFile(candidate));
    }

    private static (string source, string candidate, FileInfo info) ValidatePaths(
        string root, string original, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(original) ||
            string.IsNullOrWhiteSpace(candidatePath) ||
            !Path.IsPathFullyQualified(root) ||
            !Path.IsPathFullyQualified(original) ||
            !Path.IsPathFullyQualified(candidatePath) ||
            root.Contains("://", StringComparison.Ordinal) ||
            original.Contains("://", StringComparison.Ordinal) ||
            candidatePath.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Только абсолютные пути локальной лаборатории.");

        string basePath = Path.GetFullPath(root);
        string source = Path.GetFullPath(original);
        string candidate = Path.GetFullPath(candidatePath);
        if (OperatingSystem.IsWindows() &&
            (basePath.StartsWith(@"\\", StringComparison.Ordinal) ||
             source.StartsWith(@"\\", StringComparison.Ordinal) ||
             candidate.StartsWith(@"\\", StringComparison.Ordinal)))
            throw new ArgumentException("Сетевые пути запрещены.");
        AssertInside(basePath, source);
        AssertInside(basePath, candidate);
        if (Path.GetRelativePath(source, candidate) == ".")
            throw new ArgumentException("Оригинал и новая редакция должны различаться.");
        AssertNoLinks(basePath);
        AssertNoLinks(source);
        AssertNoLinks(candidate);
        if (!File.Exists(source))
            throw new FileNotFoundException("Исходная локальная копия отсутствует.", source);
        var info = new FileInfo(candidate);
        if (!info.Exists || info.Length is <= 0 or > MaxFileBytes)
            throw new InvalidDataException("Результат отсутствует или превышает лимит.");
        return (source, candidate, info);
    }

    private static void AssertInside(string root, string candidate)
    {
        string rel = Path.GetRelativePath(root, candidate);
        if (rel is "." or ".." || Path.IsPathRooted(rel) ||
            rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Файл выходит за пределы staging.");
    }

    private static void AssertNoLinks(string path)
    {
        string current = Path.GetPathRoot(path)!;
        foreach (string component in path[current.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Ссылки/reparse-point в staging запрещены.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
