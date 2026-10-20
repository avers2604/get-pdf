using System.Security.Cryptography;

namespace GetPdf;

/// <summary>
/// An offline WORM-original integrity check. No cookies, credentials,
/// document IDs, text or hashes are transmitted to GUP-2 by this operation.
/// </summary>
internal static class Gup2PdfIntegrity
{
    internal static async Task<bool> VerifyAsync(string source,
        string expectedSha256, long maxBytes,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new ArgumentException("Укажите SHA-256 оригинала GUP-2.", nameof(expectedSha256));
        string text = expectedSha256.Trim();
        if (text.Length != 64 || !text.All(Uri.IsHexDigit))
            throw new ArgumentException("SHA-256 должен содержать ровно 64 шестнадцатеричных символа.",
                nameof(expectedSha256));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        byte[] expected = Convert.FromHexString(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        using var file = new FileStream(Path.GetFullPath(source), FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        if (file.Length == 0 || file.Length > maxBytes)
            throw new InvalidDataException("Размер PDF превышает ограничение проверки.");
        byte[] actual = await SHA256.HashDataAsync(file, cancellation)
            .ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
