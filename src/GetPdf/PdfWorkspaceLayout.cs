namespace GetPdf;

/// <summary>
/// Layout thresholds are WPF device-independent pixels, not physical display pixels.
/// Explicit operator preference survives resizing only within the current window.
/// </summary>
public static class PdfWorkspaceLayout
{
    public const double SidebarBreakpoint = 960;

    public static bool ShowPages(double width, bool? operatorOverride = null) =>
        operatorOverride ?? (double.IsFinite(width) && width >= SidebarBreakpoint);

    public static double ResultsHeight(double windowHeight)
    {
        if (!double.IsFinite(windowHeight) || windowHeight <= 0) return 72;
        return Math.Clamp(windowHeight * .22, 72, 185);
    }
}
