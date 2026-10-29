using System.Diagnostics;
using System.Security.Cryptography;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Isolated lab runner for *synthetic* native save evidence. It opens a
/// disposable copy only. A validated OOXML ZIP is still an UNTRUSTED lab
/// candidate, never a publication/GET WORM/production save permission.
/// </summary>
public static class EuroOfficeNativeRoundTripLab
{
    public sealed record Review(
        EuroOfficeNativeRoundTripReceipt Receipt,
        EuroOfficeNativeArtifactCandidate? Candidate)
    {
        public bool SaveCompleted => false;
        public bool AuthorizedForPublication => false;
        public bool DocumentVerified => false;
        public bool CandidateValidatedAsOoxml => Candidate is not null;
    }

    public static ProcessStartInfo CreateStartInfo(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved)
    {
        // Exact local synthetic manifest, SHA-pinned executable, Linux and
        // all existing symlink/no-network controls from the CEF OSR lab.
        ProcessStartInfo info = EuroOfficeOsrStreamProcessLab.CreateStartInfo(
            host, stagingDirectory, fixtureName, sessionId, tabId, labApproved);
        info.ArgumentList[0] = "--open-staged-private-save-lab";
        return info;
    }

    public static async Task<Review> RunAsync(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        Guid sessionId, Guid tabId, bool labApproved,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo info = CreateStartInfo(
            host, stagingDirectory, fixtureName, sessionId, tabId, labApproved);
        string source = Path.Combine(Path.GetFullPath(stagingDirectory), fixtureName);
        string before = HashBoundedFile(source);
        string raw = await EuroOfficeNativeBindingProbeLab.RunRawLabAsync(
            info, cancellationToken);
        var receipt = EuroOfficeNativeRoundTripReceipt.Parse(
            raw, sessionId, tabId, fixtureName);
        // The source must agree with the baseline measured BEFORE running
        // the CEF host; native receipt alone cannot assert WORM invariance.
        if (!SameSha(before, receipt.SourceBeforeSha256))
            throw new InvalidDataException("Источник изменён до/во время CEF синтетической лаборатории.");
        return ReviewCandidate(stagingDirectory, fixtureName, receipt);
    }

    /// <summary>
    /// Allows isolated deterministic unit tests without executing native CEF.
    /// No publication, moves, or deletes; only inspection of a quarantined
    /// synthetic file that the native host already finished writing.
    /// </summary>
    public static Review ReviewCandidate(
        string stagingDirectory, string fixtureName,
        EuroOfficeNativeRoundTripReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (fixtureName is not ("gd31j6_writer.docx" or
            "gd31j6_calc.xlsx" or "gd31j6_impress.pptx") ||
            string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory))
            throw new ArgumentException("Только локальный синтетический staging/fixture.");
        string root = Path.GetFullPath(stagingDirectory);
        string source = Path.Combine(root, fixtureName);
        // InspectRevision performs a second full nested-link and traversal
        // check on source/candidate. This root still needs an offline VM;
        // the path source alone does not authenticate provenance.
        string currentSha = HashBoundedFile(source);
        if (!SameSha(currentSha, receipt.SourceBeforeSha256) ||
            !SameSha(receipt.SourceAfterSha256, receipt.SourceBeforeSha256))
            throw new InvalidDataException("Эталонный OOXML изменён.");
        if (!receipt.CandidateWritten)
            return new Review(receipt, null);
        string expected = "gd31j25-native-candidate-" +
            ExtractSessionPart(receipt.CandidateName) +
            Path.GetExtension(fixtureName);
        if (!string.Equals(expected, receipt.CandidateName, StringComparison.Ordinal))
            throw new InvalidDataException("Имя карантинного файла подменено.");
        string candidate = Path.Combine(root, receipt.CandidateName);
        EuroOfficeNativeArtifactCandidate actual =
            EuroOfficeNativeArtifactCandidate.InspectRevision(root, source, candidate);
        if (actual.Length != receipt.CandidateLength ||
            !SameSha(actual.Sha256, receipt.CandidateSha256) ||
            SameSha(actual.Sha256, currentSha))
            throw new InvalidDataException("Нативный OOXML-кандидат повреждён или не изменён.");
        return new Review(receipt, actual);
    }

    private static string ExtractSessionPart(string fileName)
    {
        const string prefix = "gd31j25-native-candidate-";
        string without = Path.GetFileNameWithoutExtension(fileName);
        if (!without.StartsWith(prefix, StringComparison.Ordinal) ||
            without.Length != prefix.Length + 32)
            throw new InvalidDataException("Кандидат не связан с session ID.");
        string session = without[prefix.Length..];
        if (!Guid.TryParseExact(session, "N", out _))
            throw new InvalidDataException("Неверный идентификатор синтетической сессии.");
        return session;
    }

    private static string HashBoundedFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 128L * 1024 * 1024)
            throw new InvalidDataException("Недопустимый размер файла синтетической лаборатории.");
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static bool SameSha(string one, string two)
    {
        if (one.Length != 64 || two.Length != 64)
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(one), Convert.FromHexString(two));
        }
        catch (FormatException) { return false; }
    }
}
