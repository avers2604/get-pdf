using Serilog;

namespace GetPdf;

/// <summary>PDF diagnostics NEVER contain document content, filenames or file paths.</summary>
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
public static void Failure(string operation, Exception exception) =>
        Log.Warning("{Operation} failed with {ErrorType}", operation, exception.GetType().Name);
    public static void Close() => Log.CloseAndFlush();
}
