using System.Collections.ObjectModel;

namespace GetPdf;

/// <summary>Metadata and reversible page identity. Image resources remain owned by their source adapter.</summary>
public sealed class DocumentSession
{
    private readonly ObservableCollection<DocumentPageEntry> _pages = [];
    private readonly Stack<State> _undo = new();
    private readonly Stack<State> _redo = new();
    private Guid _stateId = Guid.NewGuid();
    private Guid _savedStateId;
    private sealed record State(DocumentPageEntry[] Pages, Guid? Selection, Guid Id);
    public DocumentSession() { Pages = new(_pages); _savedStateId = _stateId; }
    public ReadOnlyObservableCollection<DocumentPageEntry> Pages { get; }
    public Guid? SelectedPageId { get; set; }
    public DocumentViewState ViewState { get; set; } = new(null);
    public DocumentMode Mode { get => ViewState.Mode; set => ViewState = ViewState with { Mode = value }; }
    public long Revision { get; private set; }
    public bool HasChanges => _stateId != _savedStateId;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private State Capture() => new(_pages.ToArray(), SelectedPageId, _stateId);
    private void BeginEdit()
    {
        _undo.Push(Capture());
        if (_undo.Count > 50)
        {
            var retained = _undo.Take(50).Reverse().ToArray();
            _undo.Clear(); foreach (var state in retained) _undo.Push(state);
        }
        _redo.Clear();
        _stateId = Guid.NewGuid();
        Revision++;
    }
    public void Append(IEnumerable<DocumentPageEntry> pages)
    {
        var entries = pages.ToArray();
        if (entries.Length == 0) return;
        if (entries.Any(p => p.SourcePage < 0 || p.Rotation % 90 != 0) ||
            entries.Select(p => p.Id).Distinct().Count() != entries.Length ||
            entries.Any(p => _pages.Any(existing => existing.Id == p.Id)))
            throw new ArgumentException("Неверные или повторяющиеся страницы.", nameof(pages));
        BeginEdit();
        foreach (var page in entries) _pages.Add(page);
        SelectedPageId ??= entries[0].Id;
    }
    public void Synchronize(IReadOnlyList<DocumentPageEntry> pages, Guid? selection)
    {
        if (!_pages.SequenceEqual(pages))
        {
            BeginEdit();
            _pages.Clear();
            foreach (var page in pages) _pages.Add(page);
        }
        SelectedPageId = selection;
    }
    private int Index(Guid id)
    {
        for (int i = 0; i < _pages.Count; i++) if (_pages[i].Id == id) return i;
        throw new ArgumentException("Страница отсутствует в документе.", nameof(id));
    }
    public void Duplicate(Guid id)
    {
        int index = Index(id);
        BeginEdit();
        _pages.Insert(index + 1, _pages[index] with { Id = Guid.NewGuid() });
    }
    public void Move(Guid id, int target)
    {
        int index = Index(id);
        if (target < 0 || target >= _pages.Count) throw new ArgumentOutOfRangeException(nameof(target));
        if (index == target) return;
        BeginEdit();
        _pages.Move(index, target);
    }
    public void Remove(Guid id)
    {
        int index = Index(id);
        BeginEdit();
        _pages.RemoveAt(index);
        if (SelectedPageId == id) SelectedPageId = _pages.Count == 0 ? null : _pages[Math.Min(index, _pages.Count - 1)].Id;
    }
    public void Replace(Guid id, DocumentPageEntry replacement)
    {
        int index = Index(id);
        if (replacement.Id != id) throw new ArgumentException("Замена должна сохранить идентификатор страницы.");
        BeginEdit();
        _pages[index] = replacement;
    }
    private void Restore(State state)
    {
        _pages.Clear();
        foreach (var page in state.Pages) _pages.Add(page);
        SelectedPageId = state.Selection;
        _stateId = state.Id;
        Revision++;
    }
    public void Undo() { if (_undo.Count > 0) { _redo.Push(Capture()); Restore(_undo.Pop()); } }
    public void Redo() { if (_redo.Count > 0) { _undo.Push(Capture()); Restore(_redo.Pop()); } }
    public void MarkSaved(long revision) { if (revision == Revision) _savedStateId = _stateId; }
}
