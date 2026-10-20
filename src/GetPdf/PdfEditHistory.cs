namespace GetPdf;

/// <summary>
/// Bounded, offline edit history. It only records page references, never copied PDF data.
/// Each command either completes or restores the previous state if it fails.
/// </summary>
public sealed class PdfEditHistory
{
    private readonly PdfPagePlan _plan;
    private readonly int _capacity;
    private readonly List<PdfPageReference[]> _undo = [];
    private readonly List<PdfPageReference[]> _redo = [];

    public PdfEditHistory(PdfPagePlan plan, int capacity = 50)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Execute(Action<PdfPagePlan> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var before = _plan.Snapshot();
        try { change(_plan); }
        catch
        {
            _plan.Restore(before);
            throw;
        }
        if (before.SequenceEqual(_plan.Pages)) return;
        Push(_undo, before);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var old = Pop(_undo);
        Push(_redo, _plan.Snapshot());
        _plan.Restore(old);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var next = Pop(_redo);
        Push(_undo, _plan.Snapshot());
        _plan.Restore(next);
        return true;
    }

    private static PdfPageReference[] Pop(List<PdfPageReference[]> entries)
    {
        int last = entries.Count - 1;
        var result = entries[last];
        entries.RemoveAt(last);
        return result;
    }

    private void Push(List<PdfPageReference[]> entries, PdfPageReference[] frame)
    {
        if (entries.Count == _capacity) entries.RemoveAt(0);
        entries.Add(frame);
    }
}
