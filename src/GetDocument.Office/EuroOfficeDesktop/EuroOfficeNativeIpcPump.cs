namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Consumes one future native host's JSONL stdout, routing its observations
/// only to the registered GET tab. No native executable is started here and
/// no document/print command is sent to the process.
/// </summary>
public sealed class EuroOfficeNativeIpcPump(EuroOfficeNativeTabRegistry tabs)
{
    public async Task ConsumeAsync(string tabId, TextReader stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = await EuroOfficeNativeIpc.ReadFrameAsync(stream, cancellationToken);
                tabs.Apply(tabId, frame);
                if (tabs.Snapshot(tabId).Status == EuroOfficeNativeDocumentStatus.Closed)
                    return;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
                                   EndOfStreamException or OperationCanceledException or
                                   InvalidOperationException)
        {
            // A failed native stream must not leave an interactive-looking tab.
            // Never disclose document contents, paths or raw process output.
            try
            {
                if (tabs.Snapshot(tabId).Status is not (
                    EuroOfficeNativeDocumentStatus.Closed or EuroOfficeNativeDocumentStatus.Failed))
                    tabs.Abort(tabId, ex is OperationCanceledException
                        ? "native-stream-cancelled"
                        : "native-stream-failed");
            }
            catch (KeyNotFoundException) { /* caller already removed tab */ }
            throw;
        }
    }
}
