namespace GetPdf;

/// <summary>
/// GET Document stand-in for GET PDF's managed workstation policy: only the PDF limits
/// the linked operations read. GET Document has no GUP-2 portal or print-spool policy here.
/// </summary>
internal sealed record WorkstationPolicy
{
    public int MaxDocumentPages { get; init; } = 1000;
    public int MaxPdfInputMiB { get; init; } = 1024;
    public static WorkstationPolicy Current { get; } = new();
}

/// <summary>
/// Diagnostics for the linked GET PDF operations: operation and exception type only —
/// never document content, file names or paths (same rule as GET PDF's Serilog log).
/// </summary>
internal static class DiagnosticLog
{
    public static void Failure(string operation, Exception exception) =>
        System.Diagnostics.Trace.TraceWarning("{0} failed with {1}", operation, exception.GetType().Name);
}
