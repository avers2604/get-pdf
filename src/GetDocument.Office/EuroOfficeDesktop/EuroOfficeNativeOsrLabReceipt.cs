using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.12 standalone receipt from a Wayland-only, synthetic-fixture CEF
/// OSR lab. NOT a trusted GET frame, document acknowledgment or editor session.
/// This is NOT passed to the production tab IPC parser.
/// </summary>
public sealed class EuroOfficeNativeOsrLabReceipt
{
    private readonly byte[]? _png;
    private EuroOfficeNativeOsrLabReceipt(bool observed, byte[]? png)
    {
        PaintCallbackObserved = observed;
        _png = png is null ? null : (byte[])png.Clone();
    }

    public bool PaintCallbackObserved { get; }
    public bool FrameAvailable => _png is not null;
    public byte[]? PreviewPng => _png is null ? null : (byte[])_png.Clone();
    public bool OfficeDocumentVerified => false;
    public bool SaveCompleted => false;
    public bool PdfReady => false;
    public bool PrintApproved => false;
    public bool ApprovedForGetTab => false;

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schema", "source", "paintCallbackObserved", "frameAvailable",
        "pixelWidth", "pixelHeight", "payloadBase64", "sha256",
        "documentVerified", "saveVerified", "pdfVerified",
        "printApproved", "laboratoryOnly"
    };

    public static EuroOfficeNativeOsrLabReceipt Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 65_536)
            throw new InvalidDataException("Неверная длина CEF OSR лабораторного отчёта.");
        try
        {
            using var data = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            JsonElement root = data.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Неверный OSR JSON.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                if (!Fields.Contains(property.Name) || !names.Add(property.Name))
                    throw new InvalidDataException("Неожиданное или повторное поле OSR.");
            if (names.Count != Fields.Count ||
                root.GetProperty("schema").GetString() != "getdoc-cef-osr-lab/1" ||
                root.GetProperty("source").GetString() != "cef-osr-wayland-synthetic-only" ||
                root.GetProperty("laboratoryOnly").ValueKind != JsonValueKind.True ||
                root.GetProperty("documentVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("saveVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("pdfVerified").ValueKind != JsonValueKind.False ||
                root.GetProperty("printApproved").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("OSR отчёт попытался выдать лабораторию за редактор.");

            bool observed = ReadFlag(root.GetProperty("paintCallbackObserved"));
            bool present = ReadFlag(root.GetProperty("frameAvailable"));
            int width = root.GetProperty("pixelWidth").GetInt32();
            int height = root.GetProperty("pixelHeight").GetInt32();
            string? encoded = root.GetProperty("payloadBase64").GetString();
            string? hash = root.GetProperty("sha256").GetString();
            if (!present)
            {
                if (width != 0 || height != 0 || encoded != "" || hash != "")
                    throw new InvalidDataException("OSR отчёт без изображения имеет данные.");
                return new EuroOfficeNativeOsrLabReceipt(observed, null);
            }
            if (!observed || width is < 1 or > 256 || height is < 1 or > 144 ||
                encoded is null || encoded.Length > 65_536 ||
                hash is null || hash.Length != 64)
                throw new InvalidDataException("Неверный OSR frame envelope.");
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length is < 24 or > 48 * 1024 ||
                !bytes.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != width ||
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != height ||
                !Convert.ToHexString(SHA256.HashData(bytes))
                    .Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Повреждённая CEF OSR PNG квитанция.");
            return new EuroOfficeNativeOsrLabReceipt(observed, bytes);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or
                                   OverflowException or InvalidOperationException or
                                   KeyNotFoundException)
        {
            throw new InvalidDataException("Невалидный CEF OSR laboratory receipt.", ex);
        }
    }

    private static bool ReadFlag(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException("Ожидалось булево поле CEF OSR.")
    };
}
