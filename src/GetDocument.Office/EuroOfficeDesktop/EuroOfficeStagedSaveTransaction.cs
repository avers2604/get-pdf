using System.Security.Cryptography;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// A LOCAL, one-shot Save As transaction for a previously approved synthetic
/// EuroOffice laboratory. It does not call CEF, enable a save command, change
/// an opened document, write to WORM or publish into a corporate store.
/// The caller must supply an independently produced native candidate file.
/// </summary>
public sealed record EuroOfficeStagedRevisionResult(
    string LocalPath, string Format, long Length, string Sha256)
{
    public bool WormCommitted => false;
    public bool AuthorizedForCorporatePublication => false;
}

/// <summary>
/// This closes one gap between native Save As *intent* and trustworthy local
/// staging: verify the original snapshot, validate the candidate OOXML,
/// copy to a private temporary file, revalidate the copied bytes and move
/// with no-overwrite. This is NOT a CAS/WORM revision sink or a native editor.
/// </summary>
public sealed class EuroOfficeStagedSaveTransaction
{
    private const long MaximumBytes = 128L * 1024 * 1024;

    private readonly string _root;
    private readonly string _source;
    private readonly string _candidate;
    private readonly string _target;
    private readonly Guid _session;
    private readonly Guid _tab;
    private readonly byte[] _sourceDigest;
    private int _attempted;

    private EuroOfficeStagedSaveTransaction(
        string root, string source, string candidate, string target,
        Guid session, Guid tab, byte[] sourceDigest)
    {
        _root = root;
        _source = source;
        _candidate = candidate;
        _target = target;
        _session = session;
        _tab = tab;
        _sourceDigest = sourceDigest;
    }

    public static EuroOfficeStagedSaveTransaction Prepare(
        string stagingRoot, string editableSource, string expectedNativeCandidate,
        string newLocalRevision, Guid sessionId, Guid tabId, bool labApproved = false)
    {
        if (!labApproved)
            throw new NotSupportedException(
                "Нативный Save As доступен только в явно согласованной локальной лаборатории.");
        if (sessionId == Guid.Empty || tabId == Guid.Empty)
            throw new ArgumentException("Нужны реальные sessionId и tabId лаборатории.");

        // Reuse the strict rooted, local-only, no-symlink / no-existing-target
        // policy from the previously reviewed EuroOffice request protocol.
        _ = EuroOfficeDesktopProbeProtocol.CreateOpenRequest(stagingRoot, editableSource);
        _ = EuroOfficeDesktopProbeProtocol.CreateSaveRequest(
            stagingRoot, editableSource, expectedNativeCandidate, wormOriginal: false);
        _ = EuroOfficeDesktopProbeProtocol.CreateSaveRequest(
            stagingRoot, editableSource, newLocalRevision, wormOriginal: false);

        string root = Path.GetFullPath(stagingRoot);
        string source = Path.GetFullPath(editableSource);
        string candidate = Path.GetFullPath(expectedNativeCandidate);
        string target = Path.GetFullPath(newLocalRevision);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(candidate, target, pathComparison))
            throw new ArgumentException("Кандидат и опубликованная локальная редакция должны различаться.");

        return new EuroOfficeStagedSaveTransaction(
            root, source, candidate, target, sessionId, tabId, HashFile(source));
    }

    public async Task<EuroOfficeStagedRevisionResult> PublishAsync(
        Guid sessionId, Guid tabId, CancellationToken cancellationToken)
    {
        if (_session != sessionId || _tab != tabId)
            throw new InvalidDataException("Результат Save As относится к другой вкладке или сессии.");
        if (Interlocked.Exchange(ref _attempted, 1) != 0)
            throw new InvalidOperationException("Попытка Save As уже была выполнена.");

        cancellationToken.ThrowIfCancellationRequested();

        // The native host supplies a file, not an authorization. Its OnSave
        // event, a preview frame and even a structurally valid file are NOT
        // proof of an authorized corporate revision.
        _ = EuroOfficeDesktopProbeProtocol.CreateOpenRequest(_root, _source);
        EnsureSourceUnchanged();
        EuroOfficeNativeArtifactCandidate inspected =
            EuroOfficeNativeArtifactCandidate.InspectRevision(
                _root, _source, _candidate);
        _ = EuroOfficeDesktopProbeProtocol.CreateSaveRequest(
            _root, _source, _target, wormOriginal: false);

        string temporary = Path.Combine(Path.GetDirectoryName(_target)!,
            ".getdoc-save-" + Guid.NewGuid().ToString("N") +
            Path.GetExtension(_target));
        bool created = false;
        try
        {
            // CreateNew keeps another process from supplying our private
            // temporary file. We never delete a non-owned destination.
            await using (var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                using var input = new FileStream(_candidate, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536];
                long length = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (length > MaximumBytes - read)
                        throw new InvalidDataException("Локальная редакция превышает ограничение размера.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    digest.AppendData(buffer, 0, read);
                    length += read;
                }
                if (length != inspected.Length ||
                    !CryptographicOperations.FixedTimeEquals(
                        digest.GetHashAndReset(), Convert.FromHexString(inspected.Sha256)))
                    throw new InvalidDataException("Содержимое CEF-кандидата изменилось во время копирования.");
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            // Second structural pass uses the copied bytes, not a possibly
            // mutable original candidate. This does NOT authenticate CEF.
            var checkedCopy = EuroOfficeNativeArtifactCandidate.InspectRevision(
                _root, _source, temporary);
            if (checkedCopy.Length != inspected.Length ||
                !string.Equals(checkedCopy.Sha256, inspected.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Копия не совпадает с проверенным OOXML кандидатом.");

            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceUnchanged();
            _ = EuroOfficeDesktopProbeProtocol.CreateSaveRequest(
                _root, _source, _target, wormOriginal: false);
            File.Move(temporary, _target, overwrite: false);
            created = false;
            return new EuroOfficeStagedRevisionResult(
                _target, checkedCopy.Format, checkedCopy.Length, checkedCopy.Sha256);
        }
        finally
        {
            // Never touch the source, native candidate or published output.
            if (created)
            {
                try { File.Delete(temporary); }
                catch (IOException) { /* preserve original error; cleanup is best-effort */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void EnsureSourceUnchanged()
    {
        byte[] actual = HashFile(_source);
        if (!CryptographicOperations.FixedTimeEquals(_sourceDigest, actual))
            throw new InvalidDataException("Исходная рабочая копия изменилась после открытия сеанса.");
    }

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }
}
