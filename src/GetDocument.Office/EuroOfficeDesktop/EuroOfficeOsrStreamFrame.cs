using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// One bounded, synthetic-only CEF OnPaint preview from the opt-in Wayland
/// lab. This type is NOT accepted by the regular Qt-only editor IPC and
/// CANNOT grant save, print, WORM or document-open permissions.
/// </summary>
public sealed class EuroOfficeOsrStreamFrame
{
    public const string Protocol = "getdoc-cef-osr-stream/1";
    public const int MaximumFrameCharacters = 65_536;
    public const int MaximumPngBytes = 48 * 1024;
    public const int MaximumFrames = 8;

    private static readonly HashSet<string> FrameFields = new(StringComparer.Ordinal)
    {
        "command", "documentVerified", "frameNumber", "laboratoryOnly",
        "payloadBase64", "pdfVerified", "pixelHeight", "pixelWidth",
        "printApproved", "protocol", "saveVerified", "sessionId",
        "sha256", "source", "tabId"
    };

    private readonly byte[] _bytes;
    private EuroOfficeOsrStreamFrame(Guid sessionId, Guid tabId, int frameNumber,
        int width, int height, byte[] bytes)
    {
        SessionId = sessionId;
        TabId = tabId;
        FrameNumber = frameNumber;
        Width = width;
        Height = height;
        _bytes = (byte[])bytes.Clone();
    }

    public Guid SessionId { get; }
    public Guid TabId { get; }
    public int FrameNumber { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] PngBytes => (byte[])_bytes.Clone();
    public bool CefPaintObserved => true;
    public bool OfficeDocumentVerified => false;
    public bool SaveCompleted => false;
    public bool PdfReady => false;
    public bool PrintApproved => false;
    public bool ApprovedForProduction => false;

    public static EuroOfficeOsrStreamFrame Parse(string json, Guid session,
        Guid tab, int expectedFrameNumber)
    {
        if (session == Guid.Empty || tab == Guid.Empty ||
            expectedFrameNumber is < 1 or > MaximumFrames ||
            string.IsNullOrWhiteSpace(json) || json.Length > MaximumFrameCharacters)
            throw new InvalidDataException("Неверные идентификаторы или длина OSR frame.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            var root = document.RootElement;
            VerifyFields(root, FrameFields);
            if (root.GetProperty("command").GetString() != "frame" ||
                root.GetProperty("protocol").GetString() != Protocol ||
                root.GetProperty("source").GetString() != "cef-osr-wayland-synthetic-only" ||
                root.GetProperty("sessionId").GetString() != session.ToString("N") ||
                root.GetProperty("tabId").GetString() != tab.ToString("N") ||
                root.GetProperty("frameNumber").GetInt32() != expectedFrameNumber ||
                root.GetProperty("laboratoryOnly").ValueKind != JsonValueKind.True ||
                root.GetProperty("documentVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("saveVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("pdfVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("printApproved").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Чужой, повторный или недоверенный OSR frame.");

            int width = root.GetProperty("pixelWidth").GetInt32();
            int height = root.GetProperty("pixelHeight").GetInt32();
            if (width is < 1 or > 256 || height is < 1 or > 144)
                throw new InvalidDataException("Превышен размер OSR preview.");

            string? encoded = root.GetProperty("payloadBase64").GetString();
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 65_536)
                throw new InvalidDataException("Превышен объём OSR PNG.");
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length is < 24 or > MaximumPngBytes ||
                !bytes.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != width ||
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != height)
                throw new InvalidDataException("Неверный формат CEF OSR кадра.");

            string? expectedDigest = root.GetProperty("sha256").GetString();
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, expectedDigest, StringComparison.Ordinal))
                throw new InvalidDataException("SHA-256 OSR кадра не совпадает.");

            return new EuroOfficeOsrStreamFrame(
                session, tab, expectedFrameNumber, width, height, bytes);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or
                                   InvalidOperationException or KeyNotFoundException or
                                   OverflowException)
        {
            throw new InvalidDataException("Неправильный JSON кадра EuroOffice OSR.", ex);
        }
    }

    internal static void VerifyFields(JsonElement element, HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ожидается объект JSON.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!expected.Contains(field.Name) || !seen.Add(field.Name))
                throw new InvalidDataException("Неожиданное/повторное поле CEF OSR.");
        if (seen.Count != expected.Count)
            throw new InvalidDataException("Не хватает полей CEF OSR.");
    }
}

/// <summary>
/// Strict finite JSONL reader of the isolated CEF lab stdout. Never reads an
/// unbounded line, accepts a missing end marker or continues after a replay.
/// The caller MUST bind the streams to a SHA-approved child process on a
/// separately approved offline VM; GUID/PNG SHA alone do not authenticate it.
/// </summary>
public sealed class EuroOfficeOsrStreamReader(Guid sessionId, Guid tabId)
{
    private static readonly HashSet<string> EndFields = new(StringComparer.Ordinal)
    {
        "command", "framesProduced", "laboratoryOnly",
        "protocol", "reason", "sessionId", "tabId"
    };

    public async Task<int> ReadAsync(TextReader input,
        Func<EuroOfficeOsrStreamFrame, Task> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(consume);
        if (sessionId == Guid.Empty || tabId == Guid.Empty)
            throw new ArgumentException("Нужны sessionId/tabId нативного потока.");

        int count = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = await ReadOneLineAsync(input, cancellationToken);
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Кадр OSR не является JSON объектом.");
                string? command = root.GetProperty("command").GetString();

                if (command == "end")
                {
                    EuroOfficeOsrStreamFrame.VerifyFields(root, EndFields);
                    if (root.GetProperty("protocol").GetString() != EuroOfficeOsrStreamFrame.Protocol ||
                        root.GetProperty("sessionId").GetString() != sessionId.ToString("N") ||
                        root.GetProperty("tabId").GetString() != tabId.ToString("N") ||
                        root.GetProperty("reason").GetString() != "time-limit" ||
                        root.GetProperty("laboratoryOnly").ValueKind != JsonValueKind.True ||
                        root.GetProperty("framesProduced").GetInt32() != count)
                        throw new InvalidDataException("Неподтверждённое завершение OSR потока.");
                    return count;
                }
                if (command != "frame" || count >= EuroOfficeOsrStreamFrame.MaximumFrames)
                    throw new InvalidDataException("Нежелательная команда в CEF OSR потоке.");
                var frame = EuroOfficeOsrStreamFrame.Parse(line, sessionId, tabId, count + 1);
                await consume(frame);
                ++count;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or
                                       KeyNotFoundException or OverflowException or FormatException)
            {
                throw new InvalidDataException("Недопустимое сообщение CEF OSR потока.", ex);
            }
        }
    }

    private static async Task<string> ReadOneLineAsync(TextReader input,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var sb = new StringBuilder(128);
        var ch = new char[1];
        for (int i = 0; i < EuroOfficeOsrStreamFrame.MaximumFrameCharacters; i++)
        {
            int read = await input.ReadAsync(ch.AsMemory(), timeout.Token);
            if (read == 0)
                throw new InvalidDataException("CEF OSR поток оборван без end receipt.");
            if (ch[0] == '\n') return sb.ToString();
            if (ch[0] == '\r' || ch[0] == '\0')
                throw new InvalidDataException("Недопустимый символ CEF OSR.");
            sb.Append(ch[0]);
        }
        throw new InvalidDataException("Превышен лимит JSONL CEF OSR.");
    }
}
