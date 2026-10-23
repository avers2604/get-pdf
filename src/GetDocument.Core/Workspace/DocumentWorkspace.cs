using System.Globalization;
using GetDocument.Core.DocumentFiles;

namespace GetDocument.Core.Workspace;

public enum WorkspaceDocumentKind { Text, Spreadsheet, Presentation, Pdf, Image }
public sealed record WorkspaceDocumentTab(
    Guid Id, WorkspaceDocumentKind Kind, string Title, string? FilePath, bool IsDraft);

/// <summary>
/// Platform-neutral tab/session model. File type by extension is only a routing hint:
/// actual parsers must validate contents and enforce limits before opening.
/// This registry NEVER saves file paths to disk.
/// </summary>
public sealed class DocumentWorkspace
{
    private readonly List<WorkspaceDocumentTab> _tabs = [];
    private readonly Dictionary<WorkspaceDocumentKind, int> _draftNumbers = [];
    private readonly StringComparer _pathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyList<WorkspaceDocumentTab> Tabs => _tabs.AsReadOnly();
    public Guid? ActiveId { get; private set; }

    public WorkspaceDocumentTab CreateDraft(WorkspaceDocumentKind kind)
    {
        if (kind is not (WorkspaceDocumentKind.Text or WorkspaceDocumentKind.Spreadsheet
            or WorkspaceDocumentKind.Presentation))
            throw new NotSupportedException("Этот тип документа пока нельзя создать из лаунчера.");

        int number = _draftNumbers.TryGetValue(kind, out var previous) ? previous + 1 : 1;
        _draftNumbers[kind] = number;
        string title = kind switch
        {
            WorkspaceDocumentKind.Text => "Документ",
            WorkspaceDocumentKind.Spreadsheet => "Таблица",
            _ => "Презентация"
        };
        var tab = new WorkspaceDocumentTab(Guid.NewGuid(), kind, $"{title} {number}", null, true);
        _tabs.Add(tab);
        ActiveId = tab.Id;
        return tab;
    }

    public WorkspaceDocumentTab OpenExisting(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        var fileType = DocumentFileClassifier.Classify(fullPath);
        if (fileType.Kind == DocumentFileKind.Unsupported)
            throw new NotSupportedException("Этот формат пока не предусмотрен в рабочем пространстве.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Файл не найден или сетевой ресурс недоступен.", fullPath);

        var existing = _tabs.FirstOrDefault(t =>
            t.FilePath is not null && _pathComparer.Equals(t.FilePath, fullPath));
        if (existing is not null)
        {
            ActiveId = existing.Id;
            return existing;
        }

        WorkspaceDocumentKind kind = fileType.Kind switch
        {
            DocumentFileKind.Pdf => WorkspaceDocumentKind.Pdf,
            DocumentFileKind.Text => WorkspaceDocumentKind.Text,
            DocumentFileKind.Spreadsheet => WorkspaceDocumentKind.Spreadsheet,
            DocumentFileKind.Presentation => WorkspaceDocumentKind.Presentation,
            DocumentFileKind.Image => WorkspaceDocumentKind.Image,
            _ => throw new NotSupportedException("Формат не поддерживается.")
        };
        string title = SafeTitle(Path.GetFileName(fullPath));
        var tab = new WorkspaceDocumentTab(Guid.NewGuid(), kind, title, fullPath, false);
        _tabs.Add(tab);
        ActiveId = tab.Id;
        return tab;
    }

    public bool Select(Guid id)
    {
        if (!_tabs.Any(t => t.Id == id)) return false;
        ActiveId = id;
        return true;
    }

    public bool Close(Guid id)
    {
        int index = _tabs.FindIndex(t => t.Id == id);
        if (index < 0) return false;
        _tabs.RemoveAt(index);
        if (ActiveId == id)
            ActiveId = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)].Id;
        return true;
    }

    public static string SafeTitle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Документ";
        var chars = name.Where(ch => !char.IsControl(ch) && !char.IsSurrogate(ch) &&
            char.GetUnicodeCategory(ch) != UnicodeCategory.Format)
            .Take(120).ToArray();
        string result = new(chars);
        return string.IsNullOrWhiteSpace(result) ? "Документ" : result.Trim();
    }
}
