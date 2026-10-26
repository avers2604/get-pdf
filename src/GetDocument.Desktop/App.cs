using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using GetDocument.Core.Instance;

namespace GetDocument.Desktop;

public sealed class App : Application
{
    internal static LocalDocumentInstance? LaunchBroker { get; set; }
    internal static IReadOnlyList<string> InitialPaths { get; set; } = [];
    internal static bool InitialArgumentsInvalid { get; set; }

    public override void Initialize()
    {
        // GET DS 4.1 is a light palette: do not follow a dark Windows theme.
        RequestedThemeVariant = ThemeVariant.Light;
        // PDFsharp core build: stamps and page numbers use Arial from the Windows font folder.
        if (OperatingSystem.IsWindows())
            PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://GetDocument.Desktop/"))
        {
            Source = new Uri("avares://GetDocument.Desktop/Theme/GetTheme.axaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            // Close the embedded editor's documents so LibreOffice removes its lock files and exits.
            desktop.Exit += (_, _) => GetDocument.Office.LibreOffice.LibreOfficeEditor.ShutdownIfStarted();

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
            // Warm up LibreOffice once the window is up: a cold start takes 10–30 s on office
            // PCs, afterwards a Word/Excel/PowerPoint document opens in under a second.
            DispatcherTimer.RunOnce(PrewarmOffice,
                TimeSpan.FromSeconds(3));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void PrewarmOffice()
    {
        if (GetDocument.Office.LibreOffice.LibreOfficeEditor.IsSupported)
            GetDocument.Office.LibreOffice.LibreOfficeEditor.Shared.Prewarm();
        else
            GetDocument.Office.LibreOffice.LibreOfficeRuntime.Prewarm();
    }
}
