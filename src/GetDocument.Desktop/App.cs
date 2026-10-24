using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using GetDocument.Core.Instance;

namespace GetDocument.Desktop;

public sealed class App : Application
{
    internal static LocalDocumentInstance? LaunchBroker { get; set; }
    internal static IReadOnlyList<string> InitialPaths { get; set; } = [];
    internal static bool InitialArgumentsInvalid { get; set; }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // The IPC callback never touches Avalonia controls off the UI
            // thread. Initial files also open after window initialization.
            LaunchBroker?.Start(paths => Dispatcher.UIThread.Post(
                () => window.OpenFromLaunch(paths)));
            Dispatcher.UIThread.Post(() =>
            {
                if (InitialArgumentsInvalid)
                    window.ReportRejectedLaunch();
                window.OpenFromLaunch(InitialPaths);
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}
