namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Candidate geometry derived from a REAL editor caret, not mouse position.
/// No GD-31J.18 CEF callback currently supplies this value, so consumers
/// MUST NOT treat a constructed sample as proof of editor caret provenance.
/// Only a separately approved, authenticated native telemetry path can use it.
/// </summary>
public sealed record EuroOfficeImeCaretPosition(
    Guid SessionId, Guid TabId, int FrameNumber,
    double ViewPhysicalWidth, double ViewPhysicalHeight,
    double PhysicalX, double PhysicalY, double PhysicalWidth,
    double PhysicalHeight);

public readonly record struct EuroOfficeImePreviewRect(
    double X, double Y, double Width, double Height);

/// <summary>
/// DPI independent, conservative conversion from editor *physical* viewport
/// coordinates into a Uniform-stretched lab preview. Does not guess an editor
/// caret from a pointer or CEF OnPaint frame.
/// </summary>
public static class EuroOfficeImeCaretGeometry
{
    public static EuroOfficeImePreviewRect MapToPreview(
        EuroOfficeImeCaretPosition caret, double previewLogicalWidth,
        double previewLogicalHeight,
        Guid expectedSession, Guid expectedTab, int expectedFrameNumber)
    {
        ArgumentNullException.ThrowIfNull(caret);
        if (expectedSession == Guid.Empty || expectedTab == Guid.Empty ||
            caret.SessionId != expectedSession || caret.TabId != expectedTab ||
            expectedFrameNumber < 1 || caret.FrameNumber != expectedFrameNumber)
            throw new InvalidDataException("Координаты курсора относятся к чужой вкладке или поколению.");

        bool valid = Finite(caret.ViewPhysicalWidth, 1, 16384) &&
            Finite(caret.ViewPhysicalHeight, 1, 16384) &&
            Finite(previewLogicalWidth, 1, 8192) &&
            Finite(previewLogicalHeight, 1, 8192) &&
            Finite(caret.PhysicalX, 0, caret.ViewPhysicalWidth) &&
            Finite(caret.PhysicalY, 0, caret.ViewPhysicalHeight) &&
            Finite(caret.PhysicalWidth, 0.5, 64) &&
            Finite(caret.PhysicalHeight, 1, 256) &&
            caret.PhysicalX + caret.PhysicalWidth <= caret.ViewPhysicalWidth &&
            caret.PhysicalY + caret.PhysicalHeight <= caret.ViewPhysicalHeight;
        if (!valid)
            throw new InvalidDataException("Недопустимая или неподтверждённая геометрия курсора.");

        double scale = Math.Min(previewLogicalWidth / caret.ViewPhysicalWidth,
                                previewLogicalHeight / caret.ViewPhysicalHeight);
        double drawnWidth = caret.ViewPhysicalWidth * scale;
        double drawnHeight = caret.ViewPhysicalHeight * scale;
        double offsetX = (previewLogicalWidth - drawnWidth) / 2;
        double offsetY = (previewLogicalHeight - drawnHeight) / 2;
        return new EuroOfficeImePreviewRect(
            offsetX + caret.PhysicalX * scale,
            offsetY + caret.PhysicalY * scale,
            Math.Max(1, caret.PhysicalWidth * scale),
            Math.Max(1, caret.PhysicalHeight * scale));
    }

    private static bool Finite(double number, double minimum, double maximum) =>
        double.IsFinite(number) && number >= minimum && number <= maximum;
}

/// <summary>
/// Fail-closed platform policy for the pinned EuroOffice SDK. Windows/X11 use
/// SetAsChild and do NOT provide the Wayland CEF OnPaint OSR used by this lab.
/// </summary>
public static class EuroOfficeCefOsrPlatformPolicy
{
    public const string ProductionFallback = "LibreOffice/UNO";

    public static bool IsSupported(bool operatingSystemLinux, string? qpaPlatform) =>
        operatingSystemLinux &&
        string.Equals(qpaPlatform, "wayland", StringComparison.Ordinal);
}
