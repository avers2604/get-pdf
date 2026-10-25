namespace GetDocument.Core.Preview;

/// <summary>
/// Bounded display scaling of an already-rendered PDF bitmap. No native
/// re-render is triggered by a zoom click, preserving performance on old PCs.
/// Percentages are relative to the document's default fitted reading size,
/// not physical DPI or PDF print scale.
/// </summary>
public static class PdfViewScale
{
    private static readonly int[] Percents = [60, 75, 100, 125, 150];
    private const double BaseFitFactor = 0.62;

    public const int DefaultStep = 2;
    public static int StepCount => Percents.Length;

    public static int Percent(int step)
    {
        if (step < 0 || step >= StepCount)
            throw new ArgumentOutOfRangeException(nameof(step));
        return Percents[step];
    }

    public static int Move(int step, int offset)
    {
        _ = Percent(step);
        return (int)Math.Clamp((long)step + offset, 0, StepCount - 1L);
    }

    public static (int Width, int Height) GetDisplaySize(
        int rasterWidth, int rasterHeight, int step)
    {
        int percent = Percent(step);
        if (rasterWidth is < 1 or > PdfPreviewPlan.DefaultLongSide * 2 ||
            rasterHeight is < 1 or > PdfPreviewPlan.DefaultLongSide * 2)
            throw new ArgumentOutOfRangeException(nameof(rasterWidth));
        double factor = BaseFitFactor * percent / 100;
        return (Math.Max(1, (int)Math.Round(rasterWidth * factor)),
                Math.Max(1, (int)Math.Round(rasterHeight * factor)));
    }
}
