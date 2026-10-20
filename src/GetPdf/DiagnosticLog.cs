using Serilog;

namespace GetPdf;

/// <summary>Diagnostics NEVER include document content, filenames, emails, paths, IPs or scanner names.</summary>
internal static class DiagnosticLog
{
    public static void Initialize()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GET-PDF", "Logs");
        Directory.CreateDirectory(dir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(dir, "diagnostics-.log"), rollingInterval: RollingInterval.Day,
                          retainedFileCountLimit: 14, fileSizeLimitBytes: 2 * 1024 * 1024,
                          rollOnFileSizeLimit: true, shared: false)
            .CreateLogger();
        Log.Information("Application started, .NET {Runtime}", Environment.Version.Major);
    }
    public static void ScanStage(string stage, int pages) =>
        Log.Information("Scan stage {Stage}, pages {PageCount}", stage, pages);
    public static void Failure(string operation, Exception exception) =>
        Log.Warning("{Operation} failed with {ErrorType}", operation, exception.GetType().Name);
    public static string BuildReport(string? backend) =>
        $"GET-PDF diagnostics\nVersion: 0.2.0\n.NET: {Environment.Version}\nOS version: {Environment.OSVersion.Version}\n64-bit process: {Environment.Is64BitProcess}\nConfigured backend: {backend ?? "none"}\nNo document, file, username or device identifiers included.";
    public static void Close() => Log.CloseAndFlush();
}
