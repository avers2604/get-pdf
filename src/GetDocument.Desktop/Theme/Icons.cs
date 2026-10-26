using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace GetDocument.Desktop;

/// <summary>
/// GET DS 4.1 functional icons: 24×24 outline, stroke 1.75, round caps — the same
/// geometry set as the GET PDF WPF theme, plus a few document-type glyphs.
/// </summary>
internal static class Icons
{
    public static readonly Geometry Home = Parse("M3,10.5 L12,3 L21,10.5 V20 A1,1 0 0 1 20,21 H15 V14 H9 V21 H4 A1,1 0 0 1 3,20 Z");
    public static readonly Geometry Folder = Parse("M3,7 A2,2 0 0 1 5,5 H10 L12,7 H19 A2,2 0 0 1 21,9 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 Z");
    public static readonly Geometry FileText = Parse("M13,3 H6 A2,2 0 0 0 4,5 V19 A2,2 0 0 0 6,21 H18 A2,2 0 0 0 20,19 V10 Z M13,3 V10 H20 M8,14 H16 M8,17 H14");
    public static readonly Geometry Table = Parse("M5,4 H19 A2,2 0 0 1 21,6 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 V6 A2,2 0 0 1 5,4 Z M3,10 H21 M3,15 H21 M10,4 V20");
    public static readonly Geometry Presentation = Parse("M2,4 H22 M4,4 V15 A1,1 0 0 0 5,16 H19 A1,1 0 0 0 20,15 V4 M12,16 V20 M8,21 L12,20 L16,21");
    public static readonly Geometry Template = Parse("M9,5 H18 A2,2 0 0 1 20,7 V19 A2,2 0 0 1 18,21 H9 A2,2 0 0 1 7,19 V7 A2,2 0 0 1 9,5 Z M16,5 V4 A2,2 0 0 0 14,2 H6 A2,2 0 0 0 4,4 V16 M11,10 H16 M11,14 H16");
    public static readonly Geometry Network = Parse("M5,3 H19 A2,2 0 0 1 21,5 V9 A2,2 0 0 1 19,11 H5 A2,2 0 0 1 3,9 V5 A2,2 0 0 1 5,3 Z M5,13 H19 A2,2 0 0 1 21,15 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V15 A2,2 0 0 1 5,13 Z M7,7 H7.01 M7,17 H7.01");
    public static readonly Geometry History = Parse("M3,12 a9,9 0 1 0 18,0 a9,9 0 1 0 -18,0 M12,7 V12 L15,14");
    public static readonly Geometry Send = Parse("M22,2 L15,22 L11,13 L2,9 Z M22,2 L11,13");
    public static readonly Geometry Printer = Parse("M6,9 V3 H18 V9 M6,18 H4 A2,2 0 0 1 2,16 V11 A2,2 0 0 1 4,9 H20 A2,2 0 0 1 22,11 V16 A2,2 0 0 1 20,18 H18 M6,15 H18 V21 H6 Z M18,12 H18.01");
    public static readonly Geometry Scanner = Parse("M4,13 H20 A1,1 0 0 1 21,14 V19 A1,1 0 0 1 20,20 H4 A1,1 0 0 1 3,19 V14 A1,1 0 0 1 4,13 Z M5,13 L19,5 M7,16.5 H13");
    public static readonly Geometry Users = Parse("M16,21 V19 A4,4 0 0 0 12,15 H6 A4,4 0 0 0 2,19 V21 M5,7 a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0 M22,21 V19 A4,4 0 0 0 19,15.13 M16,3.13 A4,4 0 0 1 16,10.87");
    public static readonly Geometry Edit = Parse("M12,20 H21 M16.5,3.5 A2.1,2.1 0 0 1 19.5,6.5 L7,19 L3,20 L4,16 Z");
    public static readonly Geometry Refresh = Parse("M21,8 A9,9 0 1 0 22,15 M21,3 V9 H15");
    public static readonly Geometry Check = Parse("M5,12 L9.5,16.5 L19,7");
    public static readonly Geometry Alert = Parse("M3,12 a9,9 0 1 0 18,0 a9,9 0 1 0 -18,0 M12,7 V13 M12,17 H12.01");
    public static readonly Geometry Search = Parse("M4.1,10.8 a6.7,6.7 0 1 0 13.4,0 a6.7,6.7 0 1 0 -13.4,0 M16,16 l5,5");
    public static readonly Geometry ChevronLeft = Parse("M15,5 L8,12 L15,19");
    public static readonly Geometry ChevronRight = Parse("M9,5 L16,12 L9,19");
    public static readonly Geometry ChevronUp = Parse("M5,15 L12,8 L19,15");
    public static readonly Geometry ChevronDown = Parse("M5,9 L12,16 L19,9");
    public static readonly Geometry Plus = Parse("M12,5 V19 M5,12 H19");
    public static readonly Geometry Minus = Parse("M5,12 H19");
    public static readonly Geometry X = Parse("M6,6 L18,18 M18,6 L6,18");
    public static readonly Geometry Star = Parse("M12,2.8 L14.8,8.5 L21.1,9.4 L16.5,13.8 L17.6,20.1 L12,17.1 L6.4,20.1 L7.5,13.8 L2.9,9.4 L9.2,8.5 Z");
    public static readonly Geometry Columns = Parse("M5,4 H19 A2,2 0 0 1 21,6 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 V6 A2,2 0 0 1 5,4 Z M10,4 V20");
    public static readonly Geometry Sliders = Parse("M4,7 H20 M4,17 H20 M7,7 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M14,17 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0");
    public static readonly Geometry Files = Parse("M9,5 H18 A2,2 0 0 1 20,7 V19 A2,2 0 0 1 18,21 H9 A2,2 0 0 1 7,19 V7 A2,2 0 0 1 9,5 Z M16,5 V4 A2,2 0 0 0 14,2 H6 A2,2 0 0 0 4,4 V16");
    public static readonly Geometry ShieldCheck = Parse("M12,2 L20,5 V11 C20,16 16.5,19 12,22 C7.5,19 4,16 4,11 V5 Z M8.5,12 L11,14.5 L15.5,9.5");
    public static readonly Geometry Undo = Parse("M9,14 L4,9 L9,4 M4,9 H14.5 A5.5,5.5 0 0 1 14.5,20 H11");
    public static readonly Geometry Redo = Parse("M15,14 L20,9 L15,4 M20,9 H9.5 A5.5,5.5 0 0 0 9.5,20 H13");
    public static readonly Geometry Save = Parse("M5,3 H16 L21,8 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z M7,3 V8 H15 V3 M7,21 V14 H17 V21");
    public static readonly Geometry Cursor = Parse("M9,4 H15 M9,20 H15 M12,4 V20");
    public static readonly Geometry ZoomIn = Parse("M4.1,10.8 a6.7,6.7 0 1 0 13.4,0 a6.7,6.7 0 1 0 -13.4,0 M16,16 l5,5 M10.8,8 V13.6 M8,10.8 H13.6");
    public static readonly Geometry RotateLeft = Parse("M3,8 A9,9 0 1 1 2,15 M3,3 V9 H9");
    public static readonly Geometry Trash = Parse("M3,6 H21 M8,6 V4 A1,1 0 0 1 9,3 H15 A1,1 0 0 1 16,4 V6 M5,6 L6,20 A1,1 0 0 0 7,21 H17 A1,1 0 0 0 18,20 L19,6 M10,11 V17 M14,11 V17");
    public static readonly Geometry ListNumbers = Parse("M10,6 H21 M10,12 H21 M10,18 H21 M4,6 H5 V10 M4,10 H6 M6,18 H4 L6,15.5 A1,1 0 0 0 4.5,14");
    public static readonly Geometry Square = Parse("M5,4 H19 A1,1 0 0 1 20,5 V19 A1,1 0 0 1 19,20 H5 A1,1 0 0 1 4,19 V5 A1,1 0 0 1 5,4 Z");
    public static readonly Geometry Note = Parse("M4,4 H20 V15 L15,20 H4 Z M15,20 V15 H20 M8,9 H16 M8,12 H13");
    public static readonly Geometry Minimize = Parse("M4,14 H10 V20 M20,10 H14 V4 M14,10 L21,3 M3,21 L10,14");

    private static Geometry Parse(string data) => Geometry.Parse(data);

    /// <summary>Square icon of <paramref name="size"/> px drawn with a theme brush.</summary>
    public static Control Create(Geometry geometry, double size, string brushKey = "")
    {
        var path = new Path
        {
            Data = geometry,
            StrokeThickness = 1.75,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
            Stretch = Stretch.None
        };
        if (brushKey.Length > 0 && Application.Current?.FindResource(brushKey) is IBrush brush)
            path.Stroke = brush;
        else
            path[!Shape.StrokeProperty] = new Avalonia.Data.Binding("Foreground")
            {
                RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor)
                {
                    AncestorType = typeof(ContentPresenter)
                }
            };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
    }
}
