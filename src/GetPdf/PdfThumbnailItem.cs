using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace GetPdf;

/// <summary>Small per-visible-container state: no PDF raster is created by the constructor.</summary>
internal sealed class PdfThumbnailItem(int index, PdfPageReference reference) : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _failed;

    public int Index { get; } = index;
    public int SourcePage { get; } = reference.SourcePage;
    public int Rotation { get; } = reference.Rotation;
    public string Label => $"Страница {Index + 1}";
    public bool HasMarkup { get; } = reference.Stamp.Length > 0 ||
        reference.Rectangles is { Count: > 0 } || reference.Notes is { Count: > 0 };
    public string AnnotationLabel => HasMarkup ? "Есть разметка" : string.Empty;
    public (int SourcePage, int Rotation) CacheKey => (SourcePage, Rotation);

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnChanged();
        }
    }
    public bool Failed
    {
        get => _failed;
        set
        {
            if (_failed == value) return;
            _failed = value;
            OnChanged();
        }
    }

    public void Clear()
    {
        Thumbnail = null;
        Failed = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class PdfThumbnailBounds
{
    public const int MaxSide = 160;
    public const long MaxPixels = 20000;

    public static (int Width, int Height) Fit(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (width <= MaxSide && height <= MaxSide && (long)width * height <= MaxPixels)
            return (width,height);
        double scale = Math.Min(Math.Min((double)MaxSide / width,(double)MaxSide / height),
            Math.Sqrt((double)MaxPixels / ((long)width * height)));
        return (Math.Max(1,(int)Math.Floor(width * scale)),
            Math.Max(1,(int)Math.Floor(height * scale)));
    }
}
