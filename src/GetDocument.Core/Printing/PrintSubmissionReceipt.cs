namespace GetDocument.Core.Printing;

/// <summary>
/// A process exit code is insufficient to prove queue acceptance. CUPS lp
/// prints an unambiguous request identifier when successfully enqueued; the
/// headless Windows worker sends its own fixed local ACK.
/// Neither response means that the paper has physically left the printer.
/// </summary>
public static class PrintSubmissionReceipt
{
    public static string? CupsJobId(string? output, string printer)
    {
        if (string.IsNullOrWhiteSpace(output) || output.Length > 512 ||
            string.IsNullOrWhiteSpace(printer) || printer.Length > 160 ||
            printer.Any(char.IsControl) || printer.Any(char.IsWhiteSpace))
            return null;

        string response = output.TrimEnd('\r', '\n');
        if (response.Contains('\r') || response.Contains('\n')) return null;

        const string prefix = "request id is ";
        if (!response.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string remainder = response[prefix.Length..];
        const string suffix = " (1 file(s))";
        if (!remainder.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string identifier = remainder[..^suffix.Length];
        string expectedPrefix = printer + "-";
        if (!identifier.StartsWith(expectedPrefix, StringComparison.Ordinal))
            return null;

        string number = identifier[expectedPrefix.Length..];
        if (number.Length is < 1 or > 12 ||
            number.Any(c => c is < '0' or > '9'))
            return null;
        return identifier;
    }

    public static bool WindowsAccepted(string? response) =>
        response is not null &&
        response.TrimEnd('\r', '\n').Equals("submitted", StringComparison.Ordinal);
}
