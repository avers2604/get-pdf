using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using System.Text;

namespace GetPdf;

/// <summary>
/// Bounded read-only PDF outline navigation. Accepts direct /Dest and /GoTo array
/// targets only: refuses JavaScript, remote destinations, URI and launch actions.
/// Does not read any embedded files or launch any external process.
/// </summary>
internal static class PdfOutlineReader
{
    private const int MaxEntries = 2048;
    private const int MaxDepth = 32;
    private const int MaxTitleLength = 180;

    internal sealed record Node(string Title, int SourcePageIndex,
        IReadOnlyList<Node> Children);

    internal static IReadOnlyList<Node> Read(string pdfPath)
    {
        using var document = PdfReader.Open(Path.GetFullPath(pdfPath),
            PdfDocumentOpenMode.Import);
        return Read(document);
    }

    internal static IReadOnlyList<Node> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Internals.Catalog.Elements.GetDictionary("/Outlines");
        if (root is null) return Array.Empty<Node>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var total = 0;
        return ReadSiblings(document, root.Elements.GetDictionary("/First"), 0, visited, ref total);
    }

    private static IReadOnlyList<Node> ReadSiblings(PdfDocument document,
        PdfDictionary? current, int depth, HashSet<PdfDictionary> visited, ref int total)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("Оглавление PDF имеет слишком большую вложенность.");
        var output = new List<Node>();
        while (current is not null)
        {
            if (!visited.Add(current))
                throw new InvalidDataException("Циклическое дерево оглавления PDF.");
            if (++total > MaxEntries)
                throw new InvalidDataException("Слишком много элементов оглавления PDF.");

            string title = SafeTitle(current.Elements.GetString("/Title"));
            if (title.Length == 0) title = "Раздел без названия";
            int page = ResolveTarget(document, current);
            var children = ReadSiblings(document,
                current.Elements.GetDictionary("/First"), depth + 1, visited, ref total);
            output.Add(new Node(title, page, children));
            current = current.Elements.GetDictionary("/Next");
        }
        return output;
    }

    private static int ResolveTarget(PdfDocument document, PdfDictionary node)
    {
        // External /URI, /GoToR, /Launch, /JavaScript, /SubmitForm actions
        // are NEVER dereferenced or executed by navigation.
        PdfArray? destination = node.Elements.GetArray("/Dest");
        if (destination is null && node.Elements.GetDictionary("/A") is { } action &&
            action.Elements.GetName("/S") == "/GoTo")
            destination = action.Elements.GetArray("/D");
        if (destination is null || destination.Elements.Count == 0) return -1;

        if (destination.Elements[0] is PdfReference pageRef)
        {
            for (int i = 0; i < document.PageCount; i++)
                if (document.Pages[i].ObjectNumber == pageRef.ObjectNumber)
                    return i;
        }
        else if (destination.Elements[0] is PdfDictionary dictionary)
        {
            for (int i = 0; i < document.PageCount; i++)
                if (ReferenceEquals(document.Pages[i], dictionary))
                    return i;
        }
        return -1; // Unresolved named/remote destinations are intentionally inert.
    }

    internal static int VisibleIndex(IReadOnlyList<PdfPageReference> currentPages,
        int sourcePageIndex)
    {
        ArgumentNullException.ThrowIfNull(currentPages);
        if (sourcePageIndex < 0) return -1;
        for (int i = 0; i < currentPages.Count; i++)
            if (currentPages[i].SourcePage == sourcePageIndex) return i;
        return -1;
    }

    internal static string SafeTitle(string? title)
    {
        if (title is null) return string.Empty;
        var text = new StringBuilder();
        bool space = false;
        foreach (char c in title)
        {
            if (text.Length >= MaxTitleLength) break;
            if (char.IsControl(c) && !char.IsWhiteSpace(c)) continue;
            if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && !space) { text.Append(' '); space = true; }
            }
            else { text.Append(c); space = false; }
        }
        return text.ToString().Trim();
    }
}
