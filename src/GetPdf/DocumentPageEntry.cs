namespace GetPdf;

public enum DocumentMode { PdfEditor }
public enum DocumentSourceKind { Pdf, Image }

public sealed record DocumentPageSource(DocumentSourceKind Kind, string Path);

public sealed record DocumentPageEntry(Guid Id, DocumentPageSource Source, int SourcePage, int Rotation = 0)
{
    public static DocumentPageEntry FromPdf(string path, int zeroBasedPage) =>
        new(Guid.NewGuid(), new(DocumentSourceKind.Pdf, System.IO.Path.GetFullPath(path)), zeroBasedPage);
    public static DocumentPageEntry FromImage(string key) =>
        new(Guid.NewGuid(), new(DocumentSourceKind.Image, key), 0);
}

public sealed record DocumentViewState(Guid? PageId, double RelativeOffset = 0,
    double Zoom = 1, DocumentMode Mode = DocumentMode.PdfEditor);
