namespace GetDocument.Core.Printing;

/// <summary>
/// Conservative capability check for the CUPS lpoptions -p QUEUE -l output.
/// The old wizard offered A3/duplex even when that specific printer lacked
/// them. Reject unsupported explicit choices BEFORE the lp process starts.
/// </summary>
public static class CupsDeviceCapabilities
{
    public static bool Supports(string? output, PrintPaper paper, PrintDuplex duplex)
    {
        if (!Enum.IsDefined(paper) || !Enum.IsDefined(duplex) ||
            output is { Length: > 65536 }) return false;

        var pages = Choices(output, "PageSize") ?? Choices(output, "media");
        string format = paper == PrintPaper.A3 ? "a3" : "a4";
        if (pages is null)
        {
            // Do not invent A3 on a queue with no size capabilities. A4 is
            // the normal first-run default on the deployment's MFP fleet.
            if (paper != PrintPaper.A4) return false;
        }
        else if (!pages.Any(x => x.Equals(format, StringComparison.OrdinalIgnoreCase) ||
                                 x.StartsWith("iso_" + format + "_",
                                     StringComparison.OrdinalIgnoreCase)))
            return false;

        var sides = Choices(output, "sides");
        if (duplex == PrintDuplex.OneSided)
            return sides is null ||
                sides.Contains("one-sided", StringComparer.OrdinalIgnoreCase);


        string expected = duplex == PrintDuplex.LongEdge
            ? "two-sided-long-edge" : "two-sided-short-edge";
        if (sides is not null)
            return sides.Contains(expected, StringComparer.OrdinalIgnoreCase);

        var legacy = Choices(output, "Duplex");
        if (legacy is null) return false;
        string ppd = duplex == PrintDuplex.LongEdge ? "DuplexNoTumble" : "DuplexTumble";
        return legacy.Contains(ppd, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string>? Choices(string? output, string key)
    {
        if (string.IsNullOrEmpty(output)) return null;
        foreach (string line in output.Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator < 0) continue;
            string name = line[..separator].Split('/', 2)[0].Trim();
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return line[(separator + 1)..]
                .Split([' ', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.TrimStart('*'))
                .Where(x => x.Length is > 0 and <= 90 &&
                            x.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                                or >= '0' and <= '9' or '-' or '_' or '.'))
                .ToArray();
        }
        return null;
    }
}
