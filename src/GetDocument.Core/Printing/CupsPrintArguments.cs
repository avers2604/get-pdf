namespace GetDocument.Core.Printing;

/// <summary>Creates argv, NEVER a shell command. Printer is independently
/// checked against actual discovered queues just before invoking lp.</summary>
public static class CupsPrintArguments
{
    public static IReadOnlyList<string> Create(PrintJobPlan plan, string pdfPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(pdfPath) || !Path.IsPathFullyQualified(pdfPath) ||
            !string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase) ||
            pdfPath.Any(char.IsControl))
            throw new ArgumentException("Нужен полный путь к исходному PDF.", nameof(pdfPath));

        return
        [
            "-d", plan.PrinterName,
            "-n", plan.Copies.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-P", plan.CupsPageRange,
            "-o", plan.Paper == PrintPaper.A3 ? "media=A3" : "media=A4",
            "-o", plan.Duplex switch
            {
                PrintDuplex.LongEdge => "sides=two-sided-long-edge",
                PrintDuplex.ShortEdge => "sides=two-sided-short-edge",
                _ => "sides=one-sided"
            },
            "--", pdfPath
        ];
    }
}
