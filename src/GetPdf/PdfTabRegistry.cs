using System.Text.Json;

namespace GetPdf;

/// <summary>A tab carries an independent live workspace; never stores PDF bytes.</summary>
internal sealed record PdfTabItem(Guid Id, string? Path);
internal sealed record PdfTabSnapshot(IReadOnlyList<string> Paths, int ActiveIndex);

/// <summary>Pure workspace ordering/selection, independent of WPF tab visuals.</summary>
internal sealed class PdfTabRegistry
{
    private readonly List<PdfTabItem> _items = [];
    internal IReadOnlyList<PdfTabItem> Items => _items.AsReadOnly();
    internal Guid? ActiveId { get; private set; }

    private static string? Normalize(string? path)
    {
        if (path is null) return null;
        var full = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetExtension(full).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Допускается только PDF.", nameof(path));
        return full;
    }

    internal PdfTabItem Open(string? path)
    {
        string? full = Normalize(path);
        var existing = full is null ? null :
            _items.FirstOrDefault(x => string.Equals(x.Path, full,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveId = existing.Id;
            return existing;
        }
        var item = new PdfTabItem(Guid.NewGuid(), full);
        _items.Add(item);
        ActiveId = item.Id;
        return item;
    }

    internal void Attach(PdfTabItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_items.Any(x => x.Id == item.Id) ||
            item.Path is not null && _items.Any(x =>
                string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Вкладка уже существует.");
        var normalized = Normalize(item.Path);
        _items.Add(item with { Path = normalized });
        ActiveId = item.Id;
    }

    internal void AssignPath(Guid id, string path)
    {
        var full = Normalize(path)!;
        int index = Index(id);
        if (_items.Any(x => x.Id != id && string.Equals(x.Path, full,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Документ уже открыт в другой вкладке.");
        _items[index] = _items[index] with { Path = full };
    }

    internal void Select(Guid id) { _ = Index(id); ActiveId = id; }

    internal void Move(Guid id, int index)
    {
        int old = Index(id);
        if (index < 0 || index >= _items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var item = _items[old];
        _items.RemoveAt(old);
        _items.Insert(index, item);
    }

    internal void Close(Guid id) => Detach(id);

    internal PdfTabItem Detach(Guid id)
    {
        int index = Index(id);
        var removed = _items[index];
        _items.RemoveAt(index);
        if (ActiveId == id)
            ActiveId = _items.Count == 0 ? null :
                _items[Math.Min(index, _items.Count - 1)].Id;
        return removed;
    }

    internal PdfTabSnapshot Snapshot()
    {
        var opened = _items.Where(x => x.Path is not null).ToArray();
        int active = ActiveId is Guid id ? Array.FindIndex(opened, x => x.Id == id) : -1;
        return new PdfTabSnapshot(opened.Select(x => x.Path!).ToArray(),
            active < 0 ? 0 : active);
    }

    private int Index(Guid id)
    {
        int found = _items.FindIndex(x => x.Id == id);
        return found >= 0 ? found : throw new ArgumentException("Вкладка не найдена.", nameof(id));
    }
}

/// <summary>Bounded user-local session metadata; never stores PDF contents or credentials.</summary>
internal static class PdfTabSessionStore
{
    private const int MaxTabs = 16;
    private const int MaxBytes = 32768;
    private static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GET-PDF", "tabs.json");

    internal static PdfTabSnapshot Load() => LoadFrom(DefaultPath);

    internal static PdfTabSnapshot LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes)
                return new PdfTabSnapshot([], 0);
            var loaded = JsonSerializer.Deserialize<PdfTabSnapshot>(File.ReadAllText(path));
            if (loaded is null || loaded.Paths is null) return new PdfTabSnapshot([], 0);
            var paths = loaded.Paths.Where(IsValid).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTabs).ToArray();
            return new PdfTabSnapshot(paths, Math.Clamp(loaded.ActiveIndex, 0,
                Math.Max(0, paths.Length - 1)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   JsonException or ArgumentException or NotSupportedException)
        { return new PdfTabSnapshot([], 0); }
    }

    internal static void Save(PdfTabSnapshot state) => SaveTo(DefaultPath, state);
    internal static void Clear() => ClearFrom(DefaultPath);
    internal static void ClearFrom(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    internal static void SaveTo(string path, PdfTabSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Paths);
        if (state.Paths.Count > MaxTabs || state.Paths.Any(x => !IsValid(x)) ||
            state.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Paths.Count ||
            state.ActiveIndex < 0 || state.ActiveIndex >= Math.Max(1, state.Paths.Count))
            throw new ArgumentException("Некорректная сессия PDF.", nameof(state));
        string dest = System.IO.Path.GetFullPath(path);
        string directory = System.IO.Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory,
            ".tabs-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, dest, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsValid(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 8192 &&
        System.IO.Path.IsPathFullyQualified(path) &&
        System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
}
