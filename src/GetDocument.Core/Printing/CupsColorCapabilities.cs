namespace GetDocument.Core.Printing;

/// <summary>
/// Strictly reads the IPP Everywhere print-color-mode choices reported by
/// lpoptions -p QUEUE -l. Legacy PPD ColorModel is intentionally not
/// translated to print-color-mode without confirmed driver behavior.
/// </summary>
public static class CupsColorCapabilities
{
    public static bool Supports(string? output, PrintColorMode requested)
    {
        if (requested == PrintColorMode.PrinterDefault) return true;
        if (string.IsNullOrEmpty(output) || output.Length > 65536 ||
            !Enum.IsDefined(requested)) return false;

        foreach (string line in output.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string name = line[..colon].Split('/', 2)[0].Trim();
            if (!name.Equals("print-color-mode", StringComparison.OrdinalIgnoreCase))
                continue;
            string target = requested == PrintColorMode.Color ? "color" : "monochrome";
            return line[(colon + 1)..]
                .Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.TrimStart('*'))
                .Any(x => x.Equals(target, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }
}
