namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Math-only conversion for the pinned CEF Wayland OSR mapping: the upstream
/// GetViewRect reports width × DPR physical pixels while GetScreenInfo forces
/// CEF device_scale_factor=1.0. Calculating coordinates is NOT authorization
/// to deliver input to a browser, editor or corporate document.
/// </summary>
public sealed record EuroOfficeOsrPointerCoordinates(
    int PhysicalX, int PhysicalY, int ViewPhysicalWidth, int ViewPhysicalHeight)
{
    public bool AuthorizedToForward => false;
}

public static class EuroOfficeOsrInputMapping
{
    public static EuroOfficeOsrPointerCoordinates MapPointer(
        double logicalX, double logicalY, double viewLogicalWidth,
        double viewLogicalHeight, double devicePixelRatio)
    {
        if (!double.IsFinite(logicalX) || !double.IsFinite(logicalY) ||
            !double.IsFinite(viewLogicalWidth) || !double.IsFinite(viewLogicalHeight) ||
            !double.IsFinite(devicePixelRatio) ||
            viewLogicalWidth < 1 || viewLogicalWidth > 8192 ||
            viewLogicalHeight < 1 || viewLogicalHeight > 8192 ||
            devicePixelRatio < 0.5 || devicePixelRatio > 4.0 ||
            logicalX < 0 || logicalX >= viewLogicalWidth ||
            logicalY < 0 || logicalY >= viewLogicalHeight ||
            viewLogicalWidth * devicePixelRatio > 16384 ||
            viewLogicalHeight * devicePixelRatio > 16384)
            throw new ArgumentOutOfRangeException(nameof(logicalX),
                "Недопустимые размеры, масштаб или координаты OSR.");

        int width = checked((int)Math.Floor(viewLogicalWidth * devicePixelRatio));
        int height = checked((int)Math.Floor(viewLogicalHeight * devicePixelRatio));
        int x = Math.Clamp(checked((int)Math.Floor(logicalX * devicePixelRatio)), 0, width - 1);
        int y = Math.Clamp(checked((int)Math.Floor(logicalY * devicePixelRatio)), 0, height - 1);
        return new EuroOfficeOsrPointerCoordinates(x, y, width, height);
    }

    public static void AuthorizeRemoteKeyboardOrIme() =>
        throw new NotSupportedException(
            "Удалённая отправка клавиатуры и IME в CEF требует отдельного доверенного IPC.");

    public static void AuthorizeClipboardBridge() =>
        throw new NotSupportedException(
            "Обмен буфером обмена в CEF требует проверки содержимого и ОС-изоляции.");
}
