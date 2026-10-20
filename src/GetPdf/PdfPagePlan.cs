namespace GetPdf;

/// <summary>
/// The source PDF page order is never lost: edits are non-destructive until Save As.
/// A stamp is an ordinary visible PDF overlay, not a signature or secure redaction.
/// </summary>
/// <summary>A visible vector outline in normalized [0..1] page coordinates, not a PDF /Annot.</summary>
public sealed record PdfRectangleMarkup(double X, double Y, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
        X + Width <= 1 && Y + Height <= 1;
}

/// <summary>Flattened visible sticky note, not an interactive PDF annotation.</summary>
public sealed record PdfTextNote(double X, double Y, string Text)
{
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) &&
        X >= 0 && X <= .6 && Y >= 0 && Y <= .88 &&
        !string.IsNullOrWhiteSpace(Text) && Text.Length <= 120 &&
        !Text.Any(char.IsControl);
}

public sealed record PdfPageReference(
    int SourcePage, int Rotation, string Stamp = "",
    IReadOnlyList<PdfRectangleMarkup>? Rectangles = null,
    IReadOnlyList<PdfTextNote>? Notes = null)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

public sealed class PdfPagePlan
{
    private readonly int _originalPageCount;
    private int _sourcePageCount;
    public int OriginalPageCount => _originalPageCount;
    public int SourcePageCount => _sourcePageCount;
    private readonly List<PdfPageReference> _pages;

    public PdfPagePlan(int pageCount)
    {
        if (pageCount < 1) throw new ArgumentOutOfRangeException(nameof(pageCount));
        _originalPageCount = pageCount;
        _sourcePageCount = pageCount;
        _pages = Enumerable.Range(0, pageCount)
            .Select(index => new PdfPageReference(index, 0)).ToList();
    }

    public IReadOnlyList<PdfPageReference> Pages => _pages.AsReadOnly();

    // New content versions are retained in an append-only private source PDF, so undo never reparses deleted data.
    public int RegisterSourcePage(int maxSourcePages = 10000)
    {
        if (maxSourcePages < 1 || maxSourcePages > 10000)
            throw new ArgumentOutOfRangeException(nameof(maxSourcePages));
        if (_sourcePageCount >= maxSourcePages)
            throw new InvalidOperationException(
                "Достигнут лимит версий страниц. Сохраните и откройте новый PDF.");
        return _sourcePageCount++;
    }

    /// <summary>
    /// Insert an already verified, append-only private source page.
    /// External PDF content must be appended before calling this method.
    /// Undo/Redo snapshots preserve its source index and unique page identity.
    /// </summary>
    public void InsertSourcePageAfter(int index, int sourcePage)
    {
        ValidateIndex(index);
        if (sourcePage < 0 || sourcePage >= _sourcePageCount)
            throw new ArgumentOutOfRangeException(nameof(sourcePage));
        if (_pages.Count >= WorkstationPolicy.Current.MaxDocumentPages)
            throw new InvalidOperationException("Превышен лимит страниц PDF.");
        _pages.Insert(index + 1, new PdfPageReference(sourcePage, 0));
    }
    public void ReplaceSourcePage(int index, int sourcePage)
    {
        ValidateIndex(index);
        if (sourcePage < 0 || sourcePage >= _sourcePageCount) throw new ArgumentOutOfRangeException(nameof(sourcePage));
        _pages[index] = _pages[index] with { SourcePage = sourcePage };
    }

    public bool HasChanges
    {
        get
        {
            if (_pages.Count != _originalPageCount) return true;
            for (int i = 0; i < _pages.Count; i++)
                if (_pages[i].SourcePage != i || _pages[i].Rotation != 0 || _pages[i].Stamp.Length != 0 || _pages[i].Rectangles is { Count: > 0 } ||
                    _pages[i].Notes is { Count: > 0 })
                    return true;
            return false;
        }
    }

    public void RotateClockwise(int index)
    {
        ValidateIndex(index);
        if (_pages[index].Rectangles is { Count: > 0 } || _pages[index].Notes is { Count: > 0 })
            throw new InvalidOperationException(
                "Сначала удалите рамки и текстовые заметки перед поворотом страницы.");
        var old = _pages[index];
        _pages[index] = old with { Rotation = (old.Rotation + 90) % 360 };
    }

    public void RotateCounterclockwise(int index)
    {
        ValidateIndex(index);
        if (_pages[index].Rectangles is { Count: > 0 } || _pages[index].Notes is { Count: > 0 })
            throw new InvalidOperationException(
                "Сначала удалите рамки и текстовые заметки перед поворотом страницы.");
        var old = _pages[index];
        _pages[index] = old with { Rotation = (old.Rotation + 270) % 360 };
    }

    public void SetStamp(int index, string? text)
    {
        ValidateIndex(index);
        var stamp = (text ?? string.Empty).Trim();
        if (stamp.Length > 80 || stamp.Any(char.IsControl))
            throw new ArgumentException("Штамп допускает до 80 печатных символов в одной строке.", nameof(text));
        _pages[index] = _pages[index] with { Stamp = stamp };
    }

    /// <summary>Add a visible rectangle to the selected source-page reference.</summary>
    public void AddRectangle(int index, PdfRectangleMarkup rectangle)
    {
        ValidateIndex(index);
        if (rectangle is null || !rectangle.IsValid)
            throw new ArgumentException("Неверная геометрия рамки PDF.", nameof(rectangle));
        var previous = _pages[index];
        if (previous.Rectangles is { Count: >= 128 })
            throw new InvalidOperationException("На странице допускается не более 128 рамок.");
        var updated = (previous.Rectangles ?? Array.Empty<PdfRectangleMarkup>()).ToList();
        updated.Add(rectangle);
        _pages[index] = previous with { Rectangles = updated.AsReadOnly() };
    }

    public void ClearRectangles(int index)
    {
        ValidateIndex(index);
        _pages[index] = _pages[index] with { Rectangles = null };
    }

    /// <summary>Add a bounded, one-paragraph visible comment to the current page.</summary>
    public void AddNote(int index, PdfTextNote note)
    {
        ValidateIndex(index);
        if (note is null || !note.IsValid)
            throw new ArgumentException("Некорректная позиция или текст заметки.", nameof(note));
        var original = _pages[index];
        if (original.Notes is { Count: >= 32 })
            throw new InvalidOperationException("На странице допускается не более 32 заметок.");
        var updated = (original.Notes ?? Array.Empty<PdfTextNote>()).ToList();
        updated.Add(note);
        _pages[index] = original with { Notes = updated.AsReadOnly() };
    }

    public void ClearNotes(int index)
    {
        ValidateIndex(index);
        _pages[index] = _pages[index] with { Notes = null };
    }

    public void Move(int from, int to)
    {
        ValidateIndex(from);
        ValidateIndex(to);
        if (from == to) return;
        var page = _pages[from];
        _pages.RemoveAt(from);
        _pages.Insert(to, page);
    }

    /// <summary>Insert a second reference to the same original page after the selected page.</summary>
    public void Duplicate(int index)
    {
        ValidateIndex(index);
        _pages.Insert(index + 1, _pages[index] with { Id = Guid.NewGuid() });
    }

    /// <summary>Immutable undo frame. The original PDF is never changed.</summary>
    public PdfPageReference[] Snapshot() => _pages.ToArray();

    /// <summary>Restore a validated page sequence, including duplicate source pages.</summary>
    public void Restore(IReadOnlyList<PdfPageReference> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Count < 1) throw new ArgumentException("PDF не может быть пустым.", nameof(snapshot));
        // Build the independent validated replacement *before* clearing the
        // existing list. Restore(plan.Pages) must not erase the page sequence.
        var replacement = new List<PdfPageReference>(snapshot.Count);
        foreach (var item in snapshot)
        {
            if (item is null)
                throw new ArgumentException("Пустая запись страницы PDF.", nameof(snapshot));
            if (item.SourcePage < 0 || item.SourcePage >= _sourcePageCount)
                throw new ArgumentOutOfRangeException(nameof(snapshot), "Недопустимая исходная страница.");
            if (item.Rotation is not (0 or 90 or 180 or 270))
                throw new ArgumentException("Недопустимый поворот страницы.", nameof(snapshot));
            if (item.Stamp is null || item.Stamp.Length > 80 || item.Stamp.Any(char.IsControl))
                throw new ArgumentException("Недопустимый текст штампа.", nameof(snapshot));
            if (item.Rectangles is { Count: > 128 } || item.Rectangles?.Any(r => r is null || !r.IsValid) == true)
                throw new ArgumentException("Недопустимая векторная разметка страницы.", nameof(snapshot));
            if (item.Notes is { Count: > 32 } || item.Notes?.Any(n => n is null || !n.IsValid) == true)
                throw new ArgumentException("Недопустимые текстовые заметки страницы.", nameof(snapshot));
            if (item.Rotation != 0 &&
                (item.Rectangles is { Count: > 0 } || item.Notes is { Count: > 0 }))
                throw new ArgumentException("Разметка не поддерживается на повёрнутых страницах.", nameof(snapshot));

            // Record with { } does not clone nested lists. Snapshot history
            // must be independent of externally mutable collections.
            replacement.Add(item with
            {
                Rectangles = item.Rectangles?.ToList().AsReadOnly(),
                Notes = item.Notes?.ToList().AsReadOnly()
            });
        }
        _pages.Clear();
        _pages.AddRange(replacement);
    }

    public void Delete(int index)
    {
        ValidateIndex(index);
        if (_pages.Count == 1) throw new InvalidOperationException("Документ должен содержать хотя бы одну страницу.");
        _pages.RemoveAt(index);
    }

    private void ValidateIndex(int index)
    {
        if (index < 0 || index >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}
