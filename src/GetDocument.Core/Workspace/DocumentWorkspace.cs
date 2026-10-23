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

    /// <summary>
    /// Connect a successfully saved Office draft to its original tab.
    /// Caller must generate/verify the actual new file before binding.
    /// </summary>
    public WorkspaceDocumentTab BindSavedDraft(Guid id, string destination)
    {
        int index = _tabs.FindIndex(t => t.Id == id);
        if (index < 0) throw new ArgumentException("Вкладка отсутствует.", nameof(id));
        var tab = _tabs[index];
        if (!tab.IsDraft) throw new InvalidOperationException("Редакция уже связана с файлом.");
        string expected = tab.Kind switch
        {
            WorkspaceDocumentKind.Text => ".docx",
            WorkspaceDocumentKind.Spreadsheet => ".xlsx",
            WorkspaceDocumentKind.Presentation => ".pptx",
            _ => throw new NotSupportedException("Этот тип документа нельзя сохранить как Office.")
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string path = Path.GetFullPath(destination);
        if (!Path.GetExtension(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Расширение не соответствует типу документа.", nameof(destination));
        if (_tabs.Any(t => t.Id != id && t.FilePath is not null &&
                           _pathComparer.Equals(t.FilePath, path)))
            throw new InvalidOperationException("Этот файл уже привязан к другой вкладке.");

        var updated = tab with { FilePath = path, Title = SafeTitle(Path.GetFileName(path)), IsDraft = false };
        _tabs[index] = updated;
        ActiveId = updated.Id;
        return updated;
    }


    /// <summary>
    /// Points an already saved tab to a separate new revision; the old source remains unchanged.
    /// The caller must publish a verified file before rebinding.
    /// </summary>
    public WorkspaceDocumentTab BindNewRevision(Guid id, string destination)
    {
        int index = _tabs.FindIndex(t => t.Id == id);
        if (index < 0) throw new ArgumentException("Вкладка отсутствует.", nameof(id));
        var tab = _tabs[index];
        if (tab.IsDraft || tab.FilePath is null || tab.Kind != WorkspaceDocumentKind.Text)
            throw new InvalidOperationException("Новая редакция пока поддерживается только для сохранённого DOCX.");
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string full = Path.GetFullPath(destination);
        if (!Path.GetExtension(full).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Новая редакция должна иметь формат DOCX.", nameof(destination));
        if (_pathComparer.Equals(tab.FilePath, full))
            throw new IOException("Оригинал нельзя перезаписывать.");
        if (_tabs.Any(x => x.Id != id && x.FilePath is not null && _pathComparer.Equals(x.FilePath, full)))
            throw new InvalidOperationException("Этот путь уже открыт в другой вкладке.");
        if (!File.Exists(full)) throw new FileNotFoundException("Сохранённая редакция не найдена.", full);
        var updated = tab with { FilePath = full, Title = SafeTitle(Path.GetFileName(full)) };
        _tabs[index] = updated;
        ActiveId = updated.Id;
        return updated;
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
