namespace GetPdf;

/// <summary>
/// Layout thresholds are WPF device-independent pixels, not physical display pixels.
/// Explicit operator preference survives resizing only within the current window.
/// </summary>
public static class PdfWorkspaceLayout
{
    public const double SidebarBreakpoint = 960;
    // A visible editor inspector uses 230–280 DIPs: prevent a narrow document
    // canvas when both side panels would be open at normal 125%/150% DPI.
    public const double EditorSidebarBreakpoint = 1250;

    public static bool ShowPages(double width, bool? operatorOverride = null,
        bool editorPanelVisible = false) =>
        operatorOverride ?? (double.IsFinite(width) &&
            width >= (editorPanelVisible ? EditorSidebarBreakpoint : SidebarBreakpoint));

    public static double ResultsHeight(double windowHeight)
    {
        if (!double.IsFinite(windowHeight) || windowHeight <= 0) return 72;
        return Math.Clamp(windowHeight * .22, 72, 185);
    }
}
