using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GetPdf;

/// <summary>Local page-plan recovery. Never opens a path taken from a manifest.</summary>
internal static class PdfDraftStore
{
    internal const string FileName = "pdf-draft.json";
    internal const int MaxManifestBytes = 4 * 1024 * 1024;
    internal sealed record Manifest(int Version, string OriginalPath, string SourceSha256,
        int SourcePageCount, PdfPageReference[] Pages, int SelectedPage, double Zoom,
        int OriginalPageCount = 0, string? WorkingSourceFile = null, string? WorkingSourceHash = null);

    internal sealed class Candidate : IDisposable
    {
        private readonly SessionStorage.SessionLease _lease;
        public Manifest State { get; }
        public string? WorkingSourcePath => State.WorkingSourceFile is null ? null : Path.Combine(_lease.Folder, State.WorkingSourceFile);
        internal Candidate(SessionStorage.SessionLease lease, Manifest state)
        { _lease = lease; State = state; }
        public void Consume() => SessionStorage.DeleteOwned(_lease);
        public void Dispose() => _lease.Dispose();
    }

    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    internal static string HashSource(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(source));
    }

    internal static PdfPagePlan Validate(Manifest state, int maxPages)
    {
        bool textVersion = state.Version == 2;
        if (state.Version is not (1 or 2) || state.SourcePageCount < 1 ||
            (!textVersion && (state.SourcePageCount > 1000 || state.SourcePageCount > maxPages)) ||
            (textVersion && (state.OriginalPageCount < 1 || state.OriginalPageCount > maxPages || state.SourcePageCount < state.OriginalPageCount ||
                state.SourcePageCount > 10000 || state.WorkingSourceFile is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(state.WorkingSourceFile, "^text-master-[a-zA-Z0-9-]+\\.pdf$") ||
                state.WorkingSourceHash is null || state.WorkingSourceHash.Length != 64 || !state.WorkingSourceHash.All(Uri.IsHexDigit))) || state.Pages is null ||
            state.Pages.Length < 1 || state.Pages.Length > maxPages ||
            state.OriginalPath is null || state.OriginalPath.Length > 32767 ||
            !Path.IsPathFullyQualified(state.OriginalPath) ||
            state.SourceSha256 is null || state.SourceSha256.Length != 64 ||
            !state.SourceSha256.All(Uri.IsHexDigit) ||
            state.SelectedPage < 0 || state.SelectedPage >= state.Pages.Length ||
            !double.IsFinite(state.Zoom) || state.Zoom is < .05 or > 10)
            throw new InvalidDataException("Повреждён или несовместим черновик PDF.");
        var plan = new PdfPagePlan(textVersion ? state.OriginalPageCount : state.SourcePageCount);
        while (plan.SourcePageCount < state.SourcePageCount) plan.RegisterSourcePage();
        plan.Restore(state.Pages); // Fully validate before touching any live plan.
        return plan;
    }

    internal static bool MatchesSource(Manifest state, string sourceHash, int pageCount) =>
        (state.Version == 2 ? state.OriginalPageCount : state.SourcePageCount) == pageCount &&
        string.Equals(state.SourceSha256, sourceHash, StringComparison.OrdinalIgnoreCase);

    internal static void Write(string folder, Manifest state, int maxPages)
    {
        RequireOwned(folder);
        Validate(state, maxPages);
        ValidateWorkingSource(folder, state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        if (bytes.Length > MaxManifestBytes)
            throw new InvalidDataException("Черновик PDF превышает допустимый размер.");
        var temp = Path.Combine(folder, ".pdfdraft-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temp, Path.Combine(folder, FileName), overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void Clear(string folder)
    {
        RequireOwned(folder);
        File.Delete(Path.Combine(folder, FileName));
    }

    private static void RequireOwned(string folder)
    {
        if (!SessionStorage.IsOwned(folder) ||
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Черновик PDF требует собственного защищённого сеанса.");
    }

    internal static Manifest? ReadOwned(string folder, int maxPages)
    {
        RequireOwned(folder);
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Ссылка вместо черновика PDF запрещена.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > MaxManifestBytes)
            throw new InvalidDataException("Недопустимый размер черновика PDF.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateNames(json.RootElement);
        var state = JsonSerializer.Deserialize<Manifest>(bytes, Options)
            ?? throw new InvalidDataException("Пустой черновик PDF.");
        Validate(state, maxPages);
        ValidateWorkingSource(folder, state);
        return state;
    }

    private static void ValidateWorkingSource(string folder, Manifest state)
    {
        if (state.Version != 2) return;
        string path = Path.Combine(folder, state.WorkingSourceFile!);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > WorkstationPolicy.Current.MaxPdfInputMiB * 1024L * 1024L ||
            HashSource(path) != state.WorkingSourceHash)
            throw new InvalidDataException("Повреждена сохранённая версия исходного текста PDF.");
        PdfOutputVerifier.Verify(path, state.SourcePageCount);
    }

    private static void RejectDuplicateNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Повторный ключ черновика PDF.");
                RejectDuplicateNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateNames(item);
    }

    internal static Candidate? FindLatest(string originalPath, int maxPages)
    {
        var root = SessionStorage.RootPath;
        if (!Directory.Exists(root)) return null;
        try
        {
            var folders = Directory.EnumerateDirectories(root, "session-*", SearchOption.TopDirectoryOnly)
                .Where(folder => File.Exists(Path.Combine(folder, FileName)))
                .OrderByDescending(folder => File.GetLastWriteTimeUtc(Path.Combine(folder, FileName))).ToArray();
            foreach (var folder in folders)
            {
                var lease = SessionStorage.TryAcquire(folder);
                if (lease is null) continue;
                try
                {
                    var state = ReadOwned(folder, maxPages);
                    if (state is not null && string.Equals(Path.GetFullPath(state.OriginalPath),
                        Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase))
                    {
                        var candidate = new Candidate(lease, state);
                        lease = null; // Ownership transfers to the candidate until decision/commit.
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    JsonException or ArgumentException or InvalidOperationException)
                { /* Keep malformed drafts for manual handling; try another candidate. */ }
                finally { lease?.Dispose(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { /* Another app may close a session while it is being enumerated. */ }
        return null;
    }
}
