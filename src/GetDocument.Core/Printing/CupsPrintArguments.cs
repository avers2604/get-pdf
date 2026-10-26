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

        var argv = new List<string>
        {
            "-d", plan.PrinterName,
            "-n", plan.Copies.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        // CUPS -P addresses OUTPUT sheets, not original PDF pages when
        // number-up is active. The N-up policy already allows all pages only;
        // omitting -P is required to avoid accidental truncation of the job.
        if (plan.PagesPerSheet == PrintPagesPerSheet.One)
        {
            argv.Add("-P");
            argv.Add(plan.CupsPageRange);
        }
        argv.Add("-o");
        argv.Add(plan.Paper == PrintPaper.A3 ? "media=A3" : "media=A4");
        argv.Add("-o");
        argv.Add(plan.Duplex switch
        {
            PrintDuplex.LongEdge => "sides=two-sided-long-edge",
            PrintDuplex.ShortEdge => "sides=two-sided-short-edge",
            _ => "sides=one-sided"
        });
        if (plan.Orientation != PrintOrientation.Automatic)
        {
            argv.Add("-o");
            argv.Add(plan.Orientation == PrintOrientation.Landscape
                ? "orientation-requested=4" : "orientation-requested=3");
        }
        if (plan.Color != PrintColorMode.PrinterDefault)
        {
            argv.Add("-o");
            argv.Add(plan.Color == PrintColorMode.Color
                ? "print-color-mode=color" : "print-color-mode=monochrome");
        }
        if (plan.PagesPerSheet != PrintPagesPerSheet.One)
        {
            argv.Add("-o");
            argv.Add("number-up=" + ((int)plan.PagesPerSheet)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (plan.Tray is { } tray)
        {
            if (tray.Option is not ("InputSlot" or "media-source"))
                throw new ArgumentException("CUPS не поддерживает выбранный идентификатор лотка.");
            argv.Add("-o");
            argv.Add(tray.Option + "=" + tray.Value);
        }
        argv.Add("--");
        argv.Add(pdfPath);
        return argv;
    }
}
