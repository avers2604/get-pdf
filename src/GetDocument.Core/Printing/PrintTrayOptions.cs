namespace GetDocument.Core.Printing;

/// <summary>
/// Printer-owned physical input bin; values are always rediscovered from the
/// selected local queue before spool. Never include arbitrary shell tokens.
/// </summary>
public sealed record PrintTrayOption(string Option, string Value, string Label);

public static class PrintTrayOptions
{
    private static readonly string[] AllowedKeys = ["InputSlot", "media-source", "InputBin"];

    public static PrintTrayOption Make(string option, string value)
    {
        if (!AllowedKeys.Contains(option, StringComparer.Ordinal) ||
            !ValidValue(value))
            throw new ArgumentException("Недопустимый идентификатор лотка.");
        return new PrintTrayOption(option, value, value);
    }

    public static bool IsSupported(IEnumerable<PrintTrayOption> available, PrintTrayOption selected) =>
        selected is not null && available.Any(x => x.Option == selected.Option &&
            x.Value == selected.Value);

    public static IReadOnlyList<PrintTrayOption> ParseCups(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length > 65536)
            return [];
        // Prefer exact PPD InputSlot for legacy queues. Driverless queues may
        // expose media-source; neither is guessed from a printer's model name.
        foreach (string key in new[] { "InputSlot", "media-source" })
        {
            foreach (var line in output.Split('\n'))
            {
                int delimiter = line.IndexOf(':');
                if (delimiter < 0) continue;
                string candidate = line[..delimiter].Split('/', 2)[0].Trim();
                if (!candidate.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                var result = new List<PrintTrayOption>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string raw in line[(delimiter + 1)..]
                    .Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string value = raw.TrimStart('*');
                    if (!ValidValue(value) || !seen.Add(value))
                        return []; // fail closed on malformed driver-advertised bin list
                    result.Add(Make(key, value));
                    if (result.Count > 32) return [];
                }
                return result;
            }
        }
        return [];
    }

    private static bool ValidValue(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 &&
        value[0] != '-' && value != "." && value != ".." &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '-' or '_' or '.');
}
