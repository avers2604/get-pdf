namespace GetDocument.Core.Integration;

/// <summary>
/// In-memory tab association only. It is discarded on close/restart or any
/// change in local file bytes. Never confers server-side access rights.
/// </summary>
public sealed class OfficeBoundTabRegistry
{
    private readonly Dictionary<Guid, (string Path, OfficeTabBindingReceipt Receipt)> _linked = [];

    public void Set(Guid tabId, string path, OfficeTabBindingReceipt receipt)
    {
        if (tabId == Guid.Empty || string.IsNullOrWhiteSpace(path) ||
            receipt.DocumentId == Guid.Empty || !OfficeTabBindingClient.IsOfficeFile(path))
            throw new ArgumentException("Invalid Office tab association.");
        _linked[tabId] = (Path.GetFullPath(path), receipt);
    }

    public OfficeTabBindingReceipt? Get(Guid tabId) =>
        _linked.TryGetValue(tabId, out var value) ? value.Receipt : null;

    public void Remove(Guid tabId) => _linked.Remove(tabId);

    public async Task<OfficeTabBindingReceipt?> RevalidateAsync(
        Guid tabId, CancellationToken cancellationToken = default)
    {
        if (!_linked.TryGetValue(tabId, out var value)) return null;
        try
        {
            string sha = await OfficeTabBindingClient.HashFileAsync(
                value.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (sha.Equals(value.Receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                return value.Receipt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                    ArgumentException or NotSupportedException)
        {
            // Changed, removed or inaccessible local file => invalid.
        }
        _linked.Remove(tabId);
        return null;
    }
}
