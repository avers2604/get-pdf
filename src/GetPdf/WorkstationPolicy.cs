using System.Text.Json;
using System.Text.Json.Serialization;

namespace GetPdf;

/// <summary>
/// Read-only machine policy administered by IT (deploy with ACLs/GPO; users must not be
/// able to modify the policy directory). A malformed existing policy prevents startup.
/// Absence of a policy retains legacy standalone defaults.
/// </summary>
internal sealed record WorkstationPolicy
{
    public int Version { get; init; } = 1;
    public bool AllowEmailDrafts { get; init; } = true;
    public bool AllowWatchedFolder { get; init; } = true;
    public bool AllowPdfViewer { get; init; } = true;
    public bool AllowPrinting { get; init; } = true;
    public bool RequireMachineDeviceMap { get; init; } = false;
    public bool RequireIsolatedScannerBackend { get; init; } = false;
    // Never opt in silently: even without policy.json GET-PDF remains standalone.
    public bool AllowGup2Handoff { get; init; } = false;
    public int MaxPdfInputMiB { get; init; } = 1024;
    public int MaxDocumentPages { get; init; } = 1000;

    public int MaxRecoverySessionMiB { get; init; } = 8192;
    public int RecoveryReserveMiB { get; init; } = 256;

    internal static string PolicyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GET-PDF", "policy.json");

    public static WorkstationPolicy Current => _current.Value;
    private static readonly Lazy<WorkstationPolicy> _current = new(() => Load(PolicyPath));

    internal static WorkstationPolicy Load(string path)
    {
        try
        {
            // File.Exists swallows access errors: treat unreadable policy as an error instead.
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return new WorkstationPolicy(); }
        catch (DirectoryNotFoundException) { return new WorkstationPolicy(); }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32768)
            throw new InvalidDataException("Слишком большой файл корпоративной политики.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    internal static WorkstationPolicy Parse(string json)
    {
        WorkstationPolicy policy;
        try
        {
            policy = JsonSerializer.Deserialize<WorkstationPolicy>(json,
                new JsonSerializerOptions
                {
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                    PropertyNameCaseInsensitive = false
                }) ?? throw new InvalidDataException("Корпоративная политика пуста.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Ошибка структуры корпоративной политики.", ex);
        }
        if (policy.Version != 1 || policy.MaxPdfInputMiB is < 1 or > 1024 ||
            policy.MaxDocumentPages is < 1 or > 1000 ||
            policy.MaxRecoverySessionMiB is < 128 or > 65536 ||
            policy.RecoveryReserveMiB is < 64 or > 16384)
            throw new InvalidDataException("Недопустимая версия или ограничения корпоративной политики.");
        return policy;
    }

    internal bool AcceptPdfSize(long bytes) => bytes is > 0 && bytes <= MaxPdfInputMiB * 1024L * 1024L;
}
