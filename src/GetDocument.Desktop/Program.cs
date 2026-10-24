using Avalonia;
using GetDocument.Core.Instance;

namespace GetDocument.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Never accept credentials, URLs or remote code via the command line.
        // Browser downloads arrive as ordinary local paths and are opened in
        // the current user's tabbed application, not a new process window.
        IReadOnlyList<string> paths;
        bool invalidArgs = false;
        try { paths = LocalDocumentInstance.ValidateFiles(args); }
        catch (Exception ex) when (ex is ArgumentException or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            paths = [];
            invalidArgs = true;
        }

        var instance = LocalDocumentInstance.ForCurrentProfile();
        try
        {
            if (!instance.TryBecomePrimary())
            {
                // Fail closed if the owner is busy/crashed or pipe permissions
                // deny connection: never spin up an independent second window.
                instance.ForwardAsync(paths).GetAwaiter().GetResult();
                return;
            }

            App.LaunchBroker = instance;
            App.InitialPaths = paths;
            App.InitialArgumentsInvalid = invalidArgs;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
