using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspose.Pdf.Facades;

namespace GetPdf;

/// <summary>
/// Offline signature inspection: PDF cryptographic check + Windows local
/// certificate chain with offline CRL data. Does not contact the Internet.
/// A locally trusted result is NOT a legal KЭП conclusion or trusted timestamp.
/// </summary>
internal static class PdfSignatureInspector
{
    private const int MaxSignatures = 32;
    private const long MaxPdfBytes = 256L * 1024 * 1024;

    internal static PdfSignatureTrust Classify(bool cryptographic,
        bool chainTrusted, bool coversWholeDocument) =>
        !cryptographic ? PdfSignatureTrust.Failed :
        !chainTrusted ? PdfSignatureTrust.Unverified :
        !coversWholeDocument ? PdfSignatureTrust.RevisionOnly :
        PdfSignatureTrust.LocallyTrusted;

    internal static IReadOnlyList<PdfSignatureCheck> VerifyOffline(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        string path = Path.GetFullPath(source);
        long size = new FileInfo(path).Length;
        if (size <= 0 || size > MaxPdfBytes)
            throw new InvalidDataException("PDF превышает лимит автономной проверки подписи.");
        using var inspector = new PdfFileSignature();
        inspector.BindPdf(path);
        var names = inspector.GetSignatureNames();
        if (names.Count > MaxSignatures)
            throw new InvalidDataException("Слишком много подписей в PDF.");
        var result = new List<PdfSignatureCheck>();
        int number = 0;
        foreach (var name in names)
        {
            number++;
            bool cryptographic = false, trusted = false, covered = false;
            string warning = string.Empty;
            try
            {
                // PDF signature digest/CMS verification. In older PDF revisions
                // "covers whole document" can be false even with a valid digest.
                cryptographic = inspector.VerifySignature(name);
                covered = inspector.CoversWholeDocument(name);

                if (inspector.TryExtractCertificate(name,
                        out X509Certificate2 certificate) && certificate is not null)
                {
                    using (certificate)
                    using (var chain = new X509Chain())
                    {
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.System;
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.Offline;
                        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(1);
                        trusted = chain.Build(certificate);
                    }
                }
                else warning = "Не удалось получить сертификат подписанта.";
            }
            catch (Exception ex) when (ex is CryptographicException or
                                       InvalidDataException or NotSupportedException or
                                       ArgumentException or InvalidOperationException)
            {
                // Fail closed: invalid/unsupported algorithms and broken CMS do
                // not become verified simply because /ByteRange is present.
                cryptographic = false;
                trusted = false;
                warning = "Алгоритм подписи или сертификат не удалось проверить.";
            }
            result.Add(new PdfSignatureCheck(number,
                Classify(cryptographic, trusted, covered),
                cryptographic, trusted, covered, warning));
        }
        return result;
    }
}

internal enum PdfSignatureTrust
{
    Failed, Unverified, RevisionOnly, LocallyTrusted
}

internal sealed record PdfSignatureCheck(int Sequence, PdfSignatureTrust State,
    bool CryptographicIntegrity, bool LocalChainTrusted,
    bool CoversWholeDocument, string Warning)
{
    internal string ToRussianSummary()
    {
        var label = State switch
        {
            PdfSignatureTrust.Failed => "КРИПТОГРАФИЧЕСКАЯ ПРОВЕРКА НЕ ПРОЙДЕНА",
            PdfSignatureTrust.Unverified =>
                "Целостность подтверждена; цепочка доверия/отзыв не подтверждены автономно",
            PdfSignatureTrust.RevisionOnly =>
                "Целостность и локальная цепочка подтверждены, но есть неподписанные изменения PDF",
            PdfSignatureTrust.LocallyTrusted =>
                "Целостность и локальная цепочка подтверждены",
            _ => "Не проверено"
        };
        var result = new StringBuilder();
        result.AppendLine($"Подпись #{Sequence}: {label}.");
        result.AppendLine(CoversWholeDocument
            ? "Покрывает текущую редакцию документа."
            : "Не покрывает всю текущую редакцию документа.");
        if (!string.IsNullOrEmpty(Warning)) result.AppendLine(Warning);
        result.Append("Это автономная проверка на текущую дату. Юридический статус КЭП, " +
            "время доверенной метки и онлайн-CRL/OCSP не удостоверены.");
        return result.ToString();
    }
}
