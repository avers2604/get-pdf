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
    private static Task OnUiAsync(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                action();
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task.WaitAsync(token);
    }

    private static async Task<T> OnUiAsync<T>(Func<T> callback, CancellationToken token)
    {
        T result = default!;
        await OnUiAsync(() => result = callback(), token);
        return result;
    }

    public Task OpenAsync(Guid tabId, Guid sessionId, string documentId,
        string title, CancellationToken ct) =>
        OnUiAsync(() => window.AddNativeDiagnosticTab(tabId, sessionId, documentId, title), ct);

    public Task ConnectedAsync(Guid tabId, Guid sessionId, CancellationToken ct) =>
        OnUiAsync(() => window.ReportNativeDiagnosticConnected(tabId, sessionId), ct);

    public Task HostLostAsync(Guid tabId, Guid sessionId, CancellationToken ct) =>
        OnUiAsync(() => window.ReportNativeDiagnosticCrash(tabId, sessionId), ct);

    public Task<bool> HasUnverifiedChangesAsync(Guid tabId, Guid sessionId,
        CancellationToken ct) =>
        OnUiAsync(() => window.NativeDiagnosticHasUnverifiedChanges(tabId, sessionId), ct);

    public Task RemoveAsync(Guid tabId, Guid sessionId, CancellationToken ct) =>
        OnUiAsync(() => window.CompleteNativeDiagnosticClose(tabId, sessionId), ct);
}
