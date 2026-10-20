using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace GetPdf;

/// <summary>
/// Read-only internal link inventory. Never visits external actions, reads URL
/// values or invokes embedded actions; only approved /Dest and /GoTo page arrays.
/// </summary>
internal static class PdfInternalLinkReader
{
    private const int MaxLinksPerPage = 128;
    private const int MaxTotalLinks = 4096;
    internal sealed record Link(int SourcePage, int SourceTargetPage, int Ordinal)
    {
        public string Label => $"Ссылка {Ordinal} → стр. {SourceTargetPage + 1}";
    }

    internal static IReadOnlyDictionary<int, IReadOnlyList<Link>> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new Dictionary<int, IReadOnlyList<Link>>();
        int total = 0;
        for (int i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            var annotations = page.Elements.GetArray("/Annots");
            if (annotations is null) continue;
            if (annotations.Elements.Count > MaxLinksPerPage)
                throw new InvalidDataException("Слишком много аннотаций на странице PDF для быстрого чтения ссылок.");
            var links = new List<Link>();
            int ordinal = 0;
            foreach (var raw in annotations.Elements)
            {
                PdfDictionary? ann = raw switch
                {
                    PdfReference reference => reference.Value as PdfDictionary,
                    PdfDictionary dict => dict,
                    _ => null
                };
                if (ann is null || ann.Elements.GetName("/Subtype") != "/Link") continue;
                if (++total > MaxTotalLinks)
                    throw new InvalidDataException("Превышен лимит внутренних ссылок PDF.");
                ordinal++;
                int target = PdfOutlineReader.ResolveTarget(document, ann);
                if (target is >= 0)
                    links.Add(new Link(i, target, ordinal));
            }
            if (links.Count != 0) result.Add(i, links);
        }
        return result;
    }
}
