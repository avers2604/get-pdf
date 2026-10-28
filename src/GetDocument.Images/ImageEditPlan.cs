namespace GetDocument.Images;

/// <summary>
/// Pure geometry/validation contract; operations are applied in order:
/// clockwise rotate, horizontal flip, then crop in the transformed coordinates.
/// </summary>
public sealed record ImageCrop(int X, int Y, int Width, int Height);
public sealed record ImageSize(int Width, int Height);

public sealed record ImageEditPlan(
    int ClockwiseQuarterTurns = 0,
    bool FlipHorizontal = false,
    ImageCrop? Crop = null)
{
    public const long MaxPixels = 80_000_000L;
    public const int MaxSide = 20_000;

    public bool HasChanges => ClockwiseQuarterTurns != 0 || FlipHorizontal || Crop is not null;

    public ImageSize ValidateAndCalculate(int sourceWidth, int sourceHeight)
    {
        if (ClockwiseQuarterTurns is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(ClockwiseQuarterTurns));
        if (sourceWidth is < 1 or > MaxSide || sourceHeight is < 1 or > MaxSide)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if ((long)sourceWidth * sourceHeight > MaxPixels)
            throw new InvalidDataException("Изображение превышает лимит 80 мегапикселей.");

        int width = ClockwiseQuarterTurns % 2 == 0 ? sourceWidth : sourceHeight;
        int height = ClockwiseQuarterTurns % 2 == 0 ? sourceHeight : sourceWidth;
        if (Crop is { } crop)
        {
            if (crop.X < 0 || crop.Y < 0 || crop.Width < 1 || crop.Height < 1 ||
                (long)crop.X + crop.Width > width || (long)crop.Y + crop.Height > height)
                throw new ArgumentOutOfRangeException(nameof(Crop),
                    "Область обрезки должна находиться внутри изображения после поворота.");
            width = crop.Width;
            height = crop.Height;
        }
        return new ImageSize(width, height);
    }

    public static void ValidateSourceExtension(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new NotSupportedException("Редактор поддерживает входные PNG/JPEG/WebP. TIFF обрабатывается отдельно.");
    }

    public static void ValidateDestinationExtension(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
            throw new ArgumentException("Новая редакция сохраняется в PNG или JPEG.", nameof(path));
    }
}
