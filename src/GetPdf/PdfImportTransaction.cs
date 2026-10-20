namespace GetPdf;

/// <summary>
/// Provides an all-or-nothing page append for multi-file PDF imports.
/// On rollback only newly appended pages are disposed; previous pages are left intact.
/// Does not modify original source PDFs.
/// </summary>
internal sealed class PdfImportTransaction<T> : IDisposable where T : IDisposable
{
    private readonly List<T> _pages;
    private readonly HashSet<string> _paths;
    private readonly HashSet<string> _originalPaths;
    private readonly int _initialPageCount;
    private bool _completed;

    public PdfImportTransaction(List<T> pages, HashSet<string> paths)
    {
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _initialPageCount = pages.Count;
        _originalPaths = new HashSet<string>(paths, paths.Comparer);
    }

    public void Commit() => _completed = true;

    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        for (var i = _pages.Count - 1; i >= _initialPageCount; i--)
        {
            var page = _pages[i];
            _pages.RemoveAt(i);
            page.Dispose();
        }
        _paths.RemoveWhere(path => !_originalPaths.Contains(path));
    }
}
