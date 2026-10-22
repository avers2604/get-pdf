namespace GetPdf;

/// <summary>Recognises only print/recovery PNGs created by <see cref="RecoverySession"/>.</summary>
internal static class RecoverySnapshotRetention
{
    private const string Prefix = "recovery-";
    private const string Extension = ".png";
    private const int GuidHexLength = 32;

    /// <summary>True for exactly <c>recovery-{Guid:N}.png</c>; foreign files never count against the quota.</summary>
    internal static bool IsOwnedSnapshotName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            fileName.Length != Prefix.Length + GuidHexLength + Extension.Length ||
            !fileName.StartsWith(Prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return false;
        var hex = fileName.AsSpan(Prefix.Length, GuidHexLength);
        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigitLower(c)) return false;
        }
        return true;
    }
}
