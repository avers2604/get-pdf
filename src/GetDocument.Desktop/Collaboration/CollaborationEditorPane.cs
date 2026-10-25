using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GetDocument.Core.Collaboration;

namespace GetDocument.Desktop.Collaboration;

/// <summary>
/// A single-window, single-tab native web canvas for an already-authorized
/// internal Collabora/ONLYOFFICE launch. The user interface around the canvas
/// is owned by GET; the Office server owns the actual editing operations.
/// Never load a user-supplied file path or arbitrary URL into the webview.
/// </summary>
internal sealed class CollaborationEditorPane : UserControl, IDisposable
{
    private readonly CollaborationLaunchDescriptor _launch;
    private readonly NativeWebView _webView;
    private readonly TextBlock _status;
    private bool _disposed;

    public CollaborationEditorPane(CollaborationLaunchDescriptor launch)
    {
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _status = new TextBlock
        {
            Text = "Подключение к внутреннему редактору...",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#284460")),
            VerticalAlignment = VerticalAlignment.Center
        };

        _webView = new NativeWebView
        {
            Source = new Uri("about:blank")
        };
        // Source is a HTTPS broker launch path, never a direct bearer URL.
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(16, 10)
        };
        heading.Children.Add(new TextBlock
        {
            Text = "Совместное редактирование",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        heading.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        root.Children.Add(_webView);
        Content = root;

        _webView.Source = launch.LaunchPage;
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args) =>
        args.EnableDevTools = false;

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        if (_disposed || !_launch.IsAllowedNavigation(args.Request))
        {
            args.Cancel = true;
            _status.Text = "Переход на недоверенный адрес заблокирован.";
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        // Never open separate windows or an uncontrolled system browser.
        args.Handled = true;
        _status.Text = "Внешнее окно заблокировано политикой ГЭТ.";
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        if (_disposed) return;
        _status.Text = args.IsSuccess
            ? "Редактор подключён. Сохранение подтверждается внутренним сервером."
            : "Не удалось открыть внутренний редактор — проверьте сервер и TLS.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _webView.Stop();
        // Release the live page without exposing its state in desktop logs.
        _webView.Source = new Uri("about:blank");
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        _webView.EnvironmentRequested -= OnEnvironmentRequested;
    }
}
