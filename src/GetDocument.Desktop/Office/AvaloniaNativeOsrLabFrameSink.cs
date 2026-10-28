using Avalonia.Threading;
using GetDocument.Office.EuroOfficeDesktop;

namespace GetDocument.Desktop.Office;

/// <summary>
/// Lab-only marshal for real upstream CEF OnPaint PREVIEWS, NEVER document
/// save, keyboard/IME forwarding or printer commands. All Avalonia changes
/// stay on the UI dispatcher. No production launcher references this type.
/// </summary>
internal sealed class AvaloniaNativeOsrLabFrameSink(MainWindow window)
{
    private static async Task OnUiAsync(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        await tcs.Task.WaitAsync(timeout.Token);
    }

    public Task PreviewAsync(Guid tabId, Guid sessionId,
        EuroOfficeOsrStreamFrame frame, CancellationToken cancellationToken) =>
        OnUiAsync(() => window.ShowNativeOsrLabFrame(tabId, sessionId, frame),
            cancellationToken);

    public Task CompletedAsync(Guid tabId, Guid sessionId, int count,
        CancellationToken cancellationToken) =>
        OnUiAsync(() => window.ReportNativeOsrLabCompleted(tabId, sessionId, count),
            cancellationToken);

    public Task FailedAsync(Guid tabId, Guid sessionId) =>
        OnUiAsync(() => window.ReportNativeDiagnosticCrash(tabId, sessionId),
            CancellationToken.None);
}
