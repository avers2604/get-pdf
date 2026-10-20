using System.Text.Json;
using System.Text.Json.Serialization;
namespace GetPdf;
/// <summary>Independent offline PDF policy; no scanner or worker configuration.</summary>
internal sealed record WorkstationPolicy
{
    public int Version { get; init; } = 1;
    public bool AllowPdfViewer { get; init; } = true;
    public bool AllowPrinting { get; init; } = true;
    // Corporate privacy controls: paths may disclose internal document names.
    public bool RememberRecentDocuments { get; init; } = true;
    public bool RestoreTabs { get; init; } = true;
    // Independent, opt-in local JSON contract; NEVER includes PDF paths or contents.
    public bool ExportGetSystemStatus { get; init; } = false;
    // Separate GUP-2 browser integration, not GET-system.
    public bool AllowGup2Portal { get; init; } = false;
    public string Gup2PortalUrl { get; init; } = "";
    public int MaxPdfInputMiB { get; init; } = 1024;
    public int MaxDocumentPages { get; init; } = 1000;
    public int MaxRecoverySessionMiB { get; init; } = 8192;
    public int RecoveryReserveMiB { get; init; } = 256;
    internal static string PolicyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GET-PDF", "policy.json");
    private static readonly Lazy<WorkstationPolicy> CurrentPolicy = new(() => Load(PolicyPath));
    public static WorkstationPolicy Current => CurrentPolicy.Value;
    internal static WorkstationPolicy Load(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32768) throw new InvalidDataException("Слишком большой файл политики.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
    internal static WorkstationPolicy Parse(string json)
    {
        WorkstationPolicy value;
        try { value = JsonSerializer.Deserialize<WorkstationPolicy>(json,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Пустая политика."); }
        catch (JsonException ex) { throw new InvalidDataException("Некорректная политика PDF.", ex); }
        if (!string.IsNullOrWhiteSpace(value.Gup2PortalUrl))
            _ = Gup2PortalLink.Validate(value.Gup2PortalUrl);
        if (value.AllowGup2Portal && string.IsNullOrWhiteSpace(value.Gup2PortalUrl))
            throw new InvalidDataException("Укажите HTTPS-адрес портала GUP-2.");
        if (value.Version != 1 || value.MaxPdfInputMiB is < 1 or > 1024 ||
            value.MaxDocumentPages is < 1 or > 1000 ||
            value.MaxRecoverySessionMiB is < 128 or > 65536 ||
            value.RecoveryReserveMiB is < 64 or > 16384)
            throw new InvalidDataException("Недопустимые ограничения PDF-политики.");
        return value;
    }
    internal bool AcceptPdfSize(long bytes) => bytes is > 0 && bytes <= MaxPdfInputMiB * 1024L * 1024;
}
