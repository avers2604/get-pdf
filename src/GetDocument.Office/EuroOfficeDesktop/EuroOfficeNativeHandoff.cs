using System.Text.RegularExpressions;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// Describes an UNCOMMITTED revision candidate for a future GUP-2/GET-system
/// CAS endpoint. It is not a signed receipt and carries no filesystem path,
/// credentials or authorization token across a session boundary.
/// </summary>
public sealed record EuroOfficeNativeRevisionProposal(
    string DocumentId, string ExpectedBaseVersion,
    string CandidateSha256, long CandidateLength, string Format)
{
    public bool Committed => false;
    public bool WormOriginalModified => false;
    public string State => "blocked-no-trusted-server";
}

public sealed record EuroOfficeNativePrintProposal(
    string DocumentId, string PdfSha256, long PdfLength)
{
    public bool PrintApproved => false;
    public string State => "blocked-no-get-print-adapter";
}

/// <summary>
/// Explicit unavailable adapters: GUP-2/GET-system and branded GET print
/// wizard MUST be connected via their own ACL, immutable CAS, audit and
/// artifact transfer. This class must never invent successful commits.
/// </summary>
public sealed class EuroOfficeNativeHandoff
{
    private const long MaxDocumentBytes = 128L * 1024 * 1024;

    public EuroOfficeNativeRevisionProposal PrepareGup2Revision(
        string documentId, string expectedBaseVersion,
        EuroOfficeNativeArtifactCandidate candidate)
    {
        ValidateId(documentId, nameof(documentId));
        ValidateId(expectedBaseVersion, nameof(expectedBaseVersion));
        Check(candidate);
        if (candidate.Format is not ("docx" or "xlsx" or "pptx"))
            throw new ArgumentException("Новая редакция должна иметь формат DOCX/XLSX/PPTX.");
        return new(documentId, expectedBaseVersion, candidate.Sha256,
            candidate.Length, candidate.Format);
    }

    public EuroOfficeNativePrintProposal PrepareGetPrintPdf(
        string documentId, EuroOfficeNativeArtifactCandidate candidate)
    {
        ValidateId(documentId, nameof(documentId));
        Check(candidate);
        if (candidate.Format != "pdf")
            throw new ArgumentException("Собственный мастер печати принимает только подготовленный PDF.");
        return new(documentId, candidate.Sha256, candidate.Length);
    }

    public Task CommitRevisionAsync(EuroOfficeNativeRevisionProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new NotSupportedException(
            "Серверный GUP-2/GET-system CAS и WORM-аудит ещё не подключены."));
    }

    public Task OpenInGetPrintWizardAsync(EuroOfficeNativePrintProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new NotSupportedException(
            "Защищённая передача PDF собственному мастеру печати ГЭТ ещё не подключена."));
    }

    private static void ValidateId(string input, string parameter)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 128 ||
            input.Any(char.IsControl))
            throw new ArgumentException("Не указан корректный идентификатор документа или версии.",
                parameter);
    }

    private static void Check(EuroOfficeNativeArtifactCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Length is <= 0 or > MaxDocumentBytes ||
            !Regex.IsMatch(candidate.Sha256,
                @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant) ||
            candidate.AuthorizedForPublication || candidate.WormUpdated)
            throw new ArgumentException("Неверный или неподтверждённый результат редактора.");
    }
}
