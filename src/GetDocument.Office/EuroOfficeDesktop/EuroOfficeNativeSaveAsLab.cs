using System.Diagnostics;
using System.Security.Cryptography;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// A strict synthetic-only native Save As flow. The pinned host opens a
/// disposable OOXML working copy and answers the SDK dialog with an internal
/// QTemporaryDir path. No user paths, network or production WORM operations.
/// </summary>
public static class EuroOfficeNativeSaveAsLab
{
    public sealed record Review(
        EuroOfficeNativeSaveAsReceipt Receipt,
        EuroOfficeNativeArtifactCandidate? Candidate)
    {
        public bool CandidateValidatedAsOoxml => Candidate is not null;
        public EuroOfficeNativeSaveAsLifecycle.Result Lifecycle =>
            EuroOfficeNativeSaveAsLifecycle.Classify(Receipt, Candidate is not null);
        public bool DocumentVerified => false;
        public bool SaveCompleted => false;
        public bool AuthorizedForPublication => false;
    }

    public static ProcessStartInfo CreateStartInfo(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        string selectedFormat, Guid sessionId, Guid tabId, bool labApproved)
    {
        string format = EuroOfficeNativeSaveAsReceipt.ValidateFormat(
            fixtureName, selectedFormat);
        ProcessStartInfo info = EuroOfficeOsrStreamProcessLab.CreateStartInfo(
            host, stagingDirectory, fixtureName, sessionId, tabId, labApproved);
        info.ArgumentList[0] = "--open-staged-guarded-saveas-lab";
        info.ArgumentList.Add(format); // last, strictly allowlisted CLI arg
        return info;
    }

    public static async Task<Review> RunAsync(
        EuroOfficeNativeLabHostPolicy host,
        string stagingDirectory, string fixtureName,
        string selectedFormat, Guid sessionId, Guid tabId, bool labApproved,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo info = CreateStartInfo(host, stagingDirectory,
            fixtureName, selectedFormat, sessionId, tabId, labApproved);
        string root = Path.GetFullPath(stagingDirectory);
        string source = Path.Combine(root, fixtureName);
        string candidateName = EuroOfficeNativeSaveAsReceipt.CandidateNameFor(
            sessionId, selectedFormat);
        string candidatePath = Path.Combine(root, candidateName);
        // Refuse a conflict BEFORE starting an unsandboxed native process:
        // the Qt host separately uses no-overwrite QFile::copy afterwards.
        if (File.Exists(candidatePath) || Directory.Exists(candidatePath))
            throw new IOException("Конфликт: карантинная тестовая редакция уже существует.");
        // A second, independent local SHA before native execution. Host
        // reports do not confer authenticity or permission to save.
        string before = HashBoundedFile(source);
        string raw = await EuroOfficeNativeBindingProbeLab.RunRawLabAsync(
            info, cancellationToken);
        var receipt = EuroOfficeNativeSaveAsReceipt.Parse(
            raw, sessionId, tabId, fixtureName, selectedFormat);
        if (!SameSha(before, receipt.SourceBeforeSha256))
            throw new InvalidDataException("Тестовый OOXML источник изменён за время CEF Save As.");
        return ReviewCandidate(stagingDirectory, fixtureName, receipt);
    }

    public static Review ReviewCandidate(
        string stagingDirectory, string fixtureName,
        EuroOfficeNativeSaveAsReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory) ||
            stagingDirectory.Contains("://", StringComparison.Ordinal) ||
            receipt.SessionId == Guid.Empty ||
            receipt.FixtureName != fixtureName ||
            receipt.SelectedFormat != EuroOfficeNativeSaveAsReceipt.ValidateFormat(
                fixtureName, receipt.SelectedFormat))
            throw new InvalidDataException("Неподтверждённая лабораторная сессия/формат.");

        string root = Path.GetFullPath(stagingDirectory);
        string source = Path.Combine(root, fixtureName);
        string before = HashBoundedFile(source);
        if (!SameSha(before, receipt.SourceBeforeSha256) ||
            !SameSha(receipt.SourceAfterSha256, receipt.SourceBeforeSha256))
            throw new InvalidDataException("Исходный fixture изменился.");
        if (!receipt.CandidateWritten)
        {
            if (receipt.CandidateName.Length != 0 ||
                receipt.CandidateLength != 0 ||
                receipt.CandidateSha256.Length != 0)
                throw new InvalidDataException("Невозможное отсутствие Save As результата.");
            return new Review(receipt, null);
        }
        if (!receipt.SaveAsRequestSeen || !receipt.SaveAsTargetAccepted ||
            !receipt.SaveNotification || receipt.SaveCancelled ||
            receipt.CandidateName != EuroOfficeNativeSaveAsReceipt.CandidateNameFor(
                receipt.SessionId, receipt.SelectedFormat))
            throw new InvalidDataException("Save As в приватную директорию не подтверждён.");

        string candidate = Path.Combine(root, receipt.CandidateName);
        var inspected = EuroOfficeNativeArtifactCandidate.InspectRevision(
            root, source, candidate);
        if (inspected.Length != receipt.CandidateLength ||
            !SameSha(inspected.Sha256, receipt.CandidateSha256) ||
            SameSha(inspected.Sha256, before))
            throw new InvalidDataException("ООXML Save As-кандидат не прошёл структурную SHA-проверку.");
        return new Review(receipt, inspected);
    }

    private static string HashBoundedFile(string path)
    {
        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 128L * 1024 * 1024)
            throw new InvalidDataException("Недопустимый размер лабораторного OOXML.");
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static bool SameSha(string first, string second)
    {
        if (first.Length != 64 || second.Length != 64) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(first), Convert.FromHexString(second));
        }
        catch (FormatException) { return false; }
    }
}
