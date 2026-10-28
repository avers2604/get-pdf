using Avalonia.Threading;
using GetDocument.Office.EuroOfficeDesktop;

namespace GetDocument.Desktop.Office;

/// <summary>
/// GD-31J.10: the only bridge from a background Qt diagnostic process to
/// Avalonia MainWindow. UI controls are ALWAYS accessed on the UI dispatcher.
/// No Qt native surfaces, file payloads or printer commands cross this bridge.
/// </summary>
internal sealed class AvaloniaNativeSessionView(MainWindow window) : IEuroOfficeNativeSessionView
{
    private static async Task OnUiAsync(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        // UI dispatch is bounded too: closing the window must not leave a
        // native Qt process waiting for an unprocessed status callback.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken uiToken = deadline.Token;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                uiToken.ThrowIfCancellationRequested();
                action();
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        await completion.Task.WaitAsync(uiToken);
    }

    private static async Task<T> OnUiAsync<T>(Func<T> callback, CancellationToken token)
    {
        T result = default!;
        await OnUiAsync((Action)(() => { result = callback(); }), token);
        return result;
    }

    public Task OpenAsync(Guid tabId, Guid sessionId, string documentId,
        string title, CancellationToken ct) =>
        OnUiAsync(() => window.AddNativeDiagnosticTab(tabId, sessionId, documentId, title), ct);

    public Task ConnectedAsync(Guid tabId, Guid sessionId, CancellationToken ct) =>
        OnUiAsync(() => window.ReportNativeDiagnosticConnected(tabId, sessionId), ct);

    public Task PreviewFrameAsync(Guid tabId, Guid sessionId,
        EuroOfficeNativeDiagnosticFrame frame, CancellationToken ct) =>
        OnUiAsync(() => window.ShowNativeDiagnosticFrame(tabId, sessionId, frame), ct);

    public Task HostLostAsync(Guid tabId, Guid sessionId, CancellationToken ct) =>
        OnUiAsync(() => window.ReportNativeDiagnosticCrash(tabId, sessionId), ct);

    public Task<bool> HasUnverifiedChangesAsync(Guid tabId, Guid sessionId,
        CancellationToken ct) =>
        OnUiAsync(() => window.NativeDiagnosticHasUnverifiedChanges(tabId, sessionId), ct);

    public Task RemoveAsync(Guid tabId, Guid sessionId, CancellationToken ct) =>
        OnUiAsync(() => window.CompleteNativeDiagnosticClose(tabId, sessionId), ct);
}
