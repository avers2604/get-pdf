using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GetDocument.Office.EuroOfficeDesktop;

namespace GetDocument.Desktop.Office;

/// <summary>
/// Passive content for a future EuroOffice tab in the existing GET tab strip.
/// Does not launch Qt/CEF, access files or display an unverified editor.
/// It is intentionally not reachable from the normal production launcher.
/// </summary>
internal sealed class NativeOfficeDiagnosticPane : Border
{
    private readonly TextBlock _state = new()
    {
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly TextBlock _details = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.8
    };

    // A decoded, bounded Qt painter preview, not a CEF screenshot or editor.
    private readonly Image _preview = new()
    {
        Stretch = Stretch.Uniform,
        MaxWidth = 640,
        MaxHeight = 360,
        IsVisible = false
    };

    private readonly TextBlock _connection = new()
    {
        Text = "Qt IPC: соединение не подтверждено; CEF не подключён",
        TextWrapping = TextWrapping.Wrap
    };

    public NativeOfficeDiagnosticPane(EuroOfficeNativeWorkspaceTabState initial)
    {
        Padding = new Thickness(28);
        var layout = new StackPanel
        {
            Spacing = 15,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        layout.Children.Add(new TextBlock
        {
            Text = "EuroOffice DesktopEditors — лабораторный сеанс",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(_state);
        layout.Children.Add(_connection);
        layout.Children.Add(_preview);
        layout.Children.Add(_details);
        layout.Children.Add(new TextBlock
        {
            Text = "Это диагностическая вкладка, а не открытый офисный документ. " +
                   "Сохранение, PDF и печать недоступны до проверки нативного редактора, " +
                   "WORM/CAS и собственного мастера печати ГЭТ.",
            TextWrapping = TextWrapping.Wrap
        });
        Child = new ScrollViewer { Content = layout };
        Update(initial);
    }

    /// <summary>Qt transport ACK is NOT a document-loaded event.</summary>
    public void SetTransportConnected() =>
        _connection.Text = "Qt IPC подключён. Редактор CEF и документ не подтверждены.";

    public void SetTransportLost()
    {
        _connection.Text = "Связь с Qt-хостом потеряна; правки не восстановлены.";
        _preview.IsVisible = false;
        if (_preview.Source is IDisposable old) old.Dispose();
        _preview.Source = null;
    }

    /// <summary>
    /// Display only the validated, bounded Qt painter image on UI thread.
    /// No native HWND/XID or Qt QWidget* is attached to Avalonia.
    /// </summary>
    public void SetDiagnosticFrame(EuroOfficeNativeDiagnosticFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.CefRendered || frame.OfficeDocumentVerified ||
            frame.RevisionCommitted || frame.PrintApproved)
            throw new InvalidDataException("Нельзя показывать неподтверждённый офисный кадр.");
        using var input = new MemoryStream(frame.PngBytes, writable: false);
        var bitmap = new Bitmap(input);
        if (bitmap.PixelSize.Width != frame.Width ||
            bitmap.PixelSize.Height != frame.Height)
        {
            bitmap.Dispose();
            throw new InvalidDataException("Размеры диагностического кадра не совпадают.");
        }
        if (_preview.Source is IDisposable previous) previous.Dispose();
        _preview.Source = bitmap;
        _preview.IsVisible = true;
        _connection.Text = "Доставлен тестовый кадр Qt (без CEF и офисного документа).";
    }

    public void Update(EuroOfficeNativeWorkspaceTabState state)
    {
        _state.Text = state.Status switch
        {
            EuroOfficeNativeDocumentStatus.Requested =>
                "Соединение с нативным редактором не подтверждено",
            EuroOfficeNativeDocumentStatus.ViewLoadedUnverified =>
                "Получено событие окна CEF. Документ не проверен",
            EuroOfficeNativeDocumentStatus.Failed =>
                "Нативный процесс завершился с ошибкой. Сеанс не восстановлен",
            EuroOfficeNativeDocumentStatus.Closed =>
                "Диагностический сеанс закрыт",
            _ => "Неподтверждённый сеанс"
        };
        _details.Text = $"Документ: {state.Title}. Поколение сеанса: {state.Generation}. " +
                        (state.ModifiedObserved
                            ? "Есть уведомление об изменении; сохранённая редакция не подтверждена."
                            : "Подтверждённых изменений нет.") +
                        " Открытие, сохранение и печать не подтверждены.";
    }
}
