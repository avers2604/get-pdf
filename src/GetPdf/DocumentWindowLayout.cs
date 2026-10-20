using System.Windows;
namespace GetPdf;
internal static class DocumentWindowLayout
{
    internal static void Clamp(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Width = Math.Min(window.Width, area.Width);
        window.Height = Math.Min(window.Height, area.Height);
        window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width));
        window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height));
    }
}
