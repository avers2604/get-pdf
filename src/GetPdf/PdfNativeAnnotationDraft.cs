namespace GetPdf;

/// <summary>
/// Preview-only native PDF annotation draft in normalized TOP-LEFT coordinates.
/// An instance never changes a PDF and is not included in PdfPagePlan.SaveAs.
/// </summary>
internal sealed record PdfNativeAnnotationDraft(
    string Kind, int PageIndex,
    double Left, double Top, double Right, double Bottom,
    string Text = "")
{
    internal bool IsValid =>
        Kind is "text" or "square" or "highlight" &&
        PageIndex is >= 0 and < 100 &&
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Right) && double.IsFinite(Bottom) &&
        Left >= 0 && Top >= 0 && Right <= 1 && Bottom <= 1 &&
        Right > Left && Bottom > Top &&
        (Kind == "text"
            ? !string.IsNullOrWhiteSpace(Text) && Text.Length <= 500 &&
              !Text.Any(c => char.IsControl(c) || char.IsSurrogate(c))
            : Text.Length == 0);

    /// <summary>Drag WPF pixels (top-left origin) to a bounded preview rectangle.</summary>
    internal static PdfNativeAnnotationDraft? FromDrag(
        string kind, int pageIndex, double x0, double y0,
        double x1, double y1, double visualWidth, double visualHeight)
    {
        if (kind is not ("square" or "highlight") || pageIndex is < 0 or >= 100 ||
            !ValidScreen(visualWidth, visualHeight, x0, y0, x1, y1))
            throw new ArgumentException("Невозможно определить область аннотации.");
        x0 = Math.Clamp(x0, 0, visualWidth);
        x1 = Math.Clamp(x1, 0, visualWidth);
        y0 = Math.Clamp(y0, 0, visualHeight);
        y1 = Math.Clamp(y1, 0, visualHeight);
        if (Math.Abs(x1 - x0) < 6 || Math.Abs(y1 - y0) < 6)
            return null;
        var result = new PdfNativeAnnotationDraft(kind, pageIndex,
            Math.Min(x0, x1) / visualWidth, Math.Min(y0, y1) / visualHeight,
            Math.Max(x0, x1) / visualWidth, Math.Max(y0, y1) / visualHeight);
        if (!result.IsValid) throw new ArgumentException("Недопустимая область аннотации.");
        return result;
    }

    /// <summary>Text popup marker: 24 WPF pixels, anchored inside page boundaries.</summary>
    internal static PdfNativeAnnotationDraft FromClick(
        int pageIndex, double x, double y, double visualWidth,
        double visualHeight, string text)
    {
        if (pageIndex is < 0 or >= 100 ||
            !ValidScreen(visualWidth, visualHeight, x, y) ||
            string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Недопустимый комментарий или позиция.");

        string comment = text.Trim();
        double width = Math.Min(24, visualWidth);
        double height = Math.Min(24, visualHeight);
        double left = Math.Clamp(x, 0, visualWidth - width);
        double top = Math.Clamp(y, 0, visualHeight - height);
        var draft = new PdfNativeAnnotationDraft(
            "text", pageIndex, left / visualWidth, top / visualHeight,
            (left + width) / visualWidth, (top + height) / visualHeight, comment);
        if (!draft.IsValid)
            throw new ArgumentException("Слишком длинный или недопустимый комментарий.");
        return draft;
    }

    private static bool ValidScreen(double width, double height, params double[] points) =>
        double.IsFinite(width) && double.IsFinite(height) &&
        width >= 6 && height >= 6 && points.All(double.IsFinite);

    /// <summary>
    /// Convert top-left WPF preview coordinates into bottom-left PDF POINTS.
    /// Only unrotated origin-zero media boxes can be mapped this way.
    /// This conversion must never be used to authorize production PDF writes.
    /// </summary>
    internal PdfNativeAnnotationSpec ToPdfSpec(double widthPoints, double heightPoints)
    {
        if (!IsValid || !double.IsFinite(widthPoints) || !double.IsFinite(heightPoints) ||
            widthPoints is <= 0 or > 14400 || heightPoints is <= 0 or > 14400)
            throw new ArgumentException("Недопустимая геометрия PDF.");
        return new PdfNativeAnnotationSpec(
            Kind, PageIndex,
            Left * widthPoints,
            (1 - Bottom) * heightPoints,
            Right * widthPoints,
            (1 - Top) * heightPoints,
            Text);
    }
}

/// <summary>
/// Independent in-memory annotation preview history; never mutates the PDF,
/// PdfPagePlan, main Undo/Redo stack, file system or a serialised draft file.
/// </summary>
internal sealed class PdfNativeAnnotationDraftSession
{
    private const int MaxDrafts = 24;
    private readonly int _historyCapacity;
    private readonly List<PdfNativeAnnotationDraft> _entries = [];
    private readonly List<PdfNativeAnnotationDraft[]> _undo = [];
    private readonly List<PdfNativeAnnotationDraft[]> _redo = [];

    internal PdfNativeAnnotationDraftSession(int historyCapacity = 50)
    {
        if (historyCapacity is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        _historyCapacity = historyCapacity;
    }

    internal int Count => _entries.Count;
    internal bool CanUndo => _undo.Count != 0;
    internal bool CanRedo => _redo.Count != 0;

    internal IReadOnlyList<PdfNativeAnnotationDraft> ForPage(int pageIndex) =>
        _entries.Where(x => x.PageIndex == pageIndex).ToList().AsReadOnly();

    internal void Add(PdfNativeAnnotationDraft draft)
    {
        if (draft is null || !draft.IsValid)
            throw new ArgumentException("Недопустимый черновик аннотации.", nameof(draft));
        if (_entries.Count >= MaxDrafts)
            throw new InvalidOperationException("Допустимо не более 24 черновиков аннотаций.");
        Push(_undo, _entries.ToArray());
        _redo.Clear();
        _entries.Add(draft);
    }

    internal void Clear()
    {
        if (_entries.Count == 0) return;
        Push(_undo, _entries.ToArray());
        _redo.Clear();
        _entries.Clear();
    }

    internal bool Undo()
    {
        if (!CanUndo) return false;
        Push(_redo, _entries.ToArray());
        Restore(Pop(_undo));
        return true;
    }

    internal bool Redo()
    {
        if (!CanRedo) return false;
        Push(_undo, _entries.ToArray());
        Restore(Pop(_redo));
        return true;
    }

    internal void Discard()
    {
        _entries.Clear();
        _undo.Clear();
        _redo.Clear();
    }

    private void Restore(PdfNativeAnnotationDraft[] snapshot)
    {
        _entries.Clear();
        _entries.AddRange(snapshot);
    }

    private static PdfNativeAnnotationDraft[] Pop(
        List<PdfNativeAnnotationDraft[]> frames)
    {
        int index = frames.Count - 1;
        var frame = frames[index];
        frames.RemoveAt(index);
        return frame;
    }

    private void Push(List<PdfNativeAnnotationDraft[]> frames,
        PdfNativeAnnotationDraft[] frame)
    {
        if (frames.Count == _historyCapacity) frames.RemoveAt(0);
        frames.Add(frame);
    }
}
