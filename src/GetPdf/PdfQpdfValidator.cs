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

    internal static void EnsureApproved() =>
        PdfQpdfBinaryApproval.RequireApproved(ExecutablePath, ApprovalManifestPath);

    /// <returns>False if optional qpdf is absent, true when qpdf checked the PDF.</returns>
    public static async Task<bool> ValidateIfAvailableAsync(
        string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return false;
        if (!File.Exists(path))
            throw new FileNotFoundException("PDF не найден для проверки.", path);

        var (code, _) = await PdfQpdfProcessRunner.RunAsync(
            ["--check", Path.GetFullPath(path)], cancellationToken);
        if (code != 0)
            throw new InvalidDataException(
                "qpdf обнаружил предупреждения или нарушения структуры PDF. " +
                "Перед эксплуатацией проверьте файл вручную.");
        return true;
    }
}
