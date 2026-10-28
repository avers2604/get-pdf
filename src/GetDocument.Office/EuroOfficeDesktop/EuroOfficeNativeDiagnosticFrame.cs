using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// A bounded diagnostic image from the pinned, isolated Qt-only process.
/// It is not CEF OSR, not the current office document, and never authorizes
/// file save, export or printing. PNG bytes are an UNTRUSTED image input.
/// </summary>
public sealed record EuroOfficeNativeDiagnosticFrame(
    Guid SessionId, Guid TabId, int Width, int Height, byte[] PngBytes)
{
    public bool CefRendered => false;
    public bool OfficeDocumentVerified => false;
    public bool RevisionCommitted => false;
    public bool PrintApproved => false;

    public const int MaximumReplyCharacters = 65_536;
    public const int MaximumPngBytes = 48 * 1024;

    private static readonly HashSet<string> ExpectedFields = new(StringComparer.Ordinal)
    {
        "command", "encoding", "height", "payloadBase64", "protocol",
        "sequence", "sessionId", "sha256", "source", "state", "tabId", "width"
    };

    public static EuroOfficeNativeDiagnosticFrame Parse(
        string json, Guid sessionId, Guid tabId, int sequence)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaximumReplyCharacters ||
            sessionId == Guid.Empty || tabId == Guid.Empty || sequence < 1)
            throw new InvalidDataException("Некорректный размер или идентификатор Qt-кадра.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ожидается JSON объект кадра.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!ExpectedFields.Contains(property.Name) || !seen.Add(property.Name))
                    throw new InvalidDataException("Незнакомое или дублированное поле кадра.");
            if (seen.Count != ExpectedFields.Count ||
                root.GetProperty("command").GetString() != "frame" ||
                root.GetProperty("encoding").GetString() != "png-base64" ||
                root.GetProperty("protocol").GetString() != EuroOfficeNativeTabIpc.Protocol ||
                root.GetProperty("state").GetString() != "preview" ||
                root.GetProperty("source").GetString() != "qt-painter-test-only" ||
                root.GetProperty("sessionId").GetString() != sessionId.ToString("N") ||
                root.GetProperty("tabId").GetString() != tabId.ToString("N") ||
                root.GetProperty("sequence").GetInt32() != sequence)
                throw new InvalidDataException("Недоверенный источник, сеанс или порядок Qt-кадра.");

            int width = root.GetProperty("width").GetInt32();
            int height = root.GetProperty("height").GetInt32();
            if (width is < 1 or > 512 || height is < 1 or > 512 ||
                (long)width * height > 262_144)
                throw new InvalidDataException("Слишком большое изображение Qt.");

            string? encoded = root.GetProperty("payloadBase64").GetString();
            if (encoded is null || encoded.Length > (MaximumPngBytes + 2) / 3 * 4 + 4)
                throw new InvalidDataException("Слишком длинный кадр.");
            byte[] png = Convert.FromBase64String(encoded);
            if (png.Length is < 24 or > MaximumPngBytes ||
                !png.AsSpan(0, 8).SequenceEqual(new byte[]
                    { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != width ||
                BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != height)
                throw new InvalidDataException("Неподходящий PNG заголовок.");

            string? hash = root.GetProperty("sha256").GetString();
            string actual = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            if (hash is null || hash.Length != 64 ||
                !string.Equals(actual, hash, StringComparison.Ordinal))
                throw new InvalidDataException("Нарушена целостность Qt-кадра.");

            return new EuroOfficeNativeDiagnosticFrame(
                sessionId, tabId, width, height, png);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or
                                   InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Некорректный формат диагностического кадра.", ex);
        }
    }
}
