namespace GetDocument.Core.Platforms;

/// <summary>Pure contracts. Existing Windows PDF printing stays active until adapters pass acceptance.</summary>
public sealed record DocumentPrintRequest(string PdfCopyPath, int Copies = 1, bool Duplex = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PdfCopyPath) ||
            !Path.GetExtension(PdfCopyPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ожидается путь подготовленной PDF-копии.", nameof(PdfCopyPath));
        if (Copies is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(Copies));
    }
}
public interface IPlatformPrintService
{
    Task PrintPdfAsync(DocumentPrintRequest request, CancellationToken cancellationToken = default);
}
public interface IPlatformFileAssociationService
{
    Task OpenWithSystemAsync(string filePath, CancellationToken cancellationToken = default);
}
public interface IPlatformSecretStore
{
    Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default);
    Task<byte[]?> RetrieveAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
