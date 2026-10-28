using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private EuroOfficeOsrLabInputChannel? _labInput;
    private bool _labFocused;
    private int _imageWidth, _imageHeight;
    private long _lastMoveTimestamp;
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
        // Pointer-only CEF input is enabled exclusively by the lab process
        // supervisor. A pointer press grants focus, NOT a browser click.
        _preview.PointerPressed += OnLabPointerPressed;
        _preview.PointerMoved += OnLabPointerMoved;
        _preview.PointerWheelChanged += OnLabPointerWheel;
        _preview.PointerExited += OnLabPointerExited;
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
        DetachOsrLabInput();
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

    /// <summary>
    /// A genuine upstream CEF OnPaint frame from a synthetic-only isolated
    /// Wayland laboratory. STILL NOT a verified office document or editor.
    /// </summary>
    public void SetCefOsrLabFrame(EuroOfficeOsrStreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.OfficeDocumentVerified || frame.SaveCompleted ||
            frame.PdfReady || frame.PrintApproved || frame.ApprovedForProduction)
            throw new InvalidDataException("Лабораторный CEF кадр не даёт офисных полномочий.");
        using var input = new MemoryStream(frame.PngBytes, writable: false);
        var image = new Bitmap(input);
        if (image.PixelSize.Width != frame.Width ||
            image.PixelSize.Height != frame.Height)
        {
            image.Dispose();
            throw new InvalidDataException("PNG кадр CEF не соответствует заявленным размерам.");
        }
        if (_preview.Source is IDisposable previous) previous.Dispose();
        _preview.Source = image;
        _preview.IsVisible = true;
        _imageWidth = frame.Width;
        _imageHeight = frame.Height;
        _connection.Text = $"Реальный CEF OnPaint: кадр {frame.FrameNumber}. " +
                           "Только Wayland-лаборатория, документ и сохранение не подтверждены.";
    }

    /// <summary>Explicitly attached only to a SHA-approved synthetic OSR process.</summary>
    public void AttachOsrLabInput(EuroOfficeOsrLabInputChannel input)
    {
        ArgumentNullException.ThrowIfNull(input);
        DetachOsrLabInput();
        _labInput = input;
        _connection.Text = "Лабораторная мышь: нажмите на изображение для фокуса. " +
            "Клики, клавиатура, IME и буфер обмена заблокированы.";
    }

    public void DetachOsrLabInput()
    {
        _labFocused = false;
        _labInput = null;
        _lastMoveTimestamp = 0;
    }

    private async void OnLabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_labInput is not { } input || !_preview.IsVisible) return;
        try
        {
            await input.SendFocusAsync(true, CancellationToken.None);
            _labFocused = true;
            // The press event must NOT become a CEF mouse click.
            e.Handled = true;
        }
        catch (Exception ex) { FailLabInput(ex); }
    }

    private async void OnLabPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_labFocused || _labInput is not { } input) return;
        _labFocused = false;
        try { await input.SendFocusAsync(false, CancellationToken.None); }
        catch (Exception ex) { FailLabInput(ex); }
    }

    private async void OnLabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_labFocused || _labInput is not { } input) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastMoveTimestamp != 0 &&
            (now - _lastMoveTimestamp) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency < 75)
            return; // <= 13 pointer moves/sec, avoids exhausting native rate cap
        if (!TryNormalizedPosition(e.GetPosition(_preview), out double x, out double y))
            return;
        _lastMoveTimestamp = now;
        try { await input.SendPointerMoveAsync(x, y, CancellationToken.None); }
        catch (Exception ex) { FailLabInput(ex); }
    }

    private async void OnLabPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_labFocused || _labInput is not { } input ||
            !TryNormalizedPosition(e.GetPosition(_preview), out double x, out double y))
            return;
        int deltaX = Math.Clamp((int)Math.Round(e.Delta.X * 120), -120, 120);
        int deltaY = Math.Clamp((int)Math.Round(e.Delta.Y * 120), -120, 120);
        if (deltaX == 0 && deltaY == 0) return;
        try
        {
            await input.SendWheelAsync(x, y, deltaX, deltaY, CancellationToken.None);
            e.Handled = true;
        }
        catch (Exception ex) { FailLabInput(ex); }
    }

    private bool TryNormalizedPosition(Point position, out double x, out double y)
    {
        x = y = 0;
        if (_imageWidth < 1 || _imageHeight < 1 ||
            _preview.Bounds.Width <= 0 || _preview.Bounds.Height <= 0)
            return false;
        // Stretch.Uniform may letterbox the frame inside Image.Bounds.
        // Exclude the letterbox; never send pointer coordinates from outside
        // the actually visible page preview.
        double ratio = Math.Min(_preview.Bounds.Width / _imageWidth,
                                _preview.Bounds.Height / _imageHeight);
        double drawnWidth = _imageWidth * ratio;
        double drawnHeight = _imageHeight * ratio;
        double left = (_preview.Bounds.Width - drawnWidth) / 2;
        double top = (_preview.Bounds.Height - drawnHeight) / 2;
        x = (position.X - left) / drawnWidth;
        y = (position.Y - top) / drawnHeight;
        return double.IsFinite(x) && double.IsFinite(y) &&
               x >= 0 && x < 1 && y >= 0 && y < 1;
    }

    private void FailLabInput(Exception exception)
    {
        // Never claim a CEF action succeeded after a broken pipe.
        DetachOsrLabInput();
        _connection.Text = "Лабораторный ввод отключён: " +
            exception.GetType().Name + ". Документ не проверен.";
    }

    public void SetCefOsrLabCompleted(int frames) =>
        _connection.Text = $"Лабораторный CEF поток завершён ({frames} кадров). " +
                           "Содержимое документа и сохранение не проверены.";

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
