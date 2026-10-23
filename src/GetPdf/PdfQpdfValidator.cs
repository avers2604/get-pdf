namespace GetPdf;

/// <summary>
/// Optional offline PDF validation through the same bounded child process
/// runner used by OCR. No shell, downloaded binary or unbounded diagnostics.
/// </summary>
internal static class PdfQpdfValidator
{
    internal static string ExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "qpdf", "qpdf.exe");
    internal static string ApprovalManifestPath =>
        PdfQpdfBinaryApproval.ManifestPath(ExecutablePath);

    public static bool IsAvailable
    {
        get
        {
            if (!File.Exists(ExecutablePath)) return false;
            EnsureApproved();
            return true;
        }
    }

    /// <summary>For showing commands: a missing or tampered qpdf hides them instead of failing the window.</summary>
    public static bool IsUsable
    {
        get
        {
            try { return IsAvailable; }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or
                                       IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Failure("pdf-qpdf-approval", ex);
                return false;
            }
        }
    }

    internal static void EnsureApproved() =>
        PdfQpdfBinaryApproval.RequireApproved(ExecutablePath, ApprovalManifestPath);

    /// <returns>
    /// <see cref="PdfQpdfCheck.Unavailable"/> if qpdf is absent; otherwise whether qpdf found the
    /// structure clean or readable with recoverable warnings. Errors throw.
    /// </returns>
    public static async Task<PdfQpdfCheck> ValidateIfAvailableAsync(
        string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return PdfQpdfCheck.Unavailable;
        if (!File.Exists(path))
            throw new FileNotFoundException("PDF не найден для проверки.", path);

        var (code, _) = await PdfQpdfProcessRunner.RunAsync(
            ["--check", Path.GetFullPath(path)], cancellationToken);
        return Interpret(code);
    }

    internal static PdfQpdfCheck Interpret(int exitCode) => exitCode switch
    {
        0 => PdfQpdfCheck.Clean,
        PdfQpdfProcessRunner.ExitWarnings => PdfQpdfCheck.Warnings,
        _ => throw new InvalidDataException(
            "qpdf обнаружил нарушения структуры PDF. Перед эксплуатацией проверьте файл вручную.")
    };
}

internal enum PdfQpdfCheck { Unavailable, Clean, Warnings }
