namespace GetPdf;

/// <summary>
/// Holds at most one display raster at a time. The source identity is part of the
/// cache key: rotating or replacing a scan invalidates the cached preview even
/// if the OperatorPage object is unchanged. All callers stay on the WPF UI thread.
/// </summary>
internal sealed class SinglePagePreviewCache<TPage, TPreview>
    where TPage : class where TPreview : class
{
    private TPage? _page;
    private object? _source;
    private TPreview? _preview;

    public TPreview GetOrCreate(TPage page, object source, Func<TPreview> create)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(create);
        if (ReferenceEquals(_page, page) && ReferenceEquals(_source, source) && _preview is not null)
            return _preview;

        // Build first; failed rendering must not corrupt a previously visible image.
        var result = create() ?? throw new InvalidOperationException("Preview renderer returned null.");
        _page = page;
        _source = source;
        _preview = result;
        return result;
    }

    public void Clear()
    {
        _preview = null;
        _source = null;
        _page = null;
    }
}
