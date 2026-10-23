using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GetDocument.Core.Workspace;
using GetDocument.Office;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class OfficeDocumentFactoryTests
{
    [TestMethod]
    public void CreatesValidBlankDocxPackage()
    {
        using var folder = TempDirectory.Create();
        string path = Path.Combine(folder.Path, "Пустой документ.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, path);
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.IsNotNull(doc.MainDocumentPart?.Document?.Body);
        Assert.IsTrue(new FileInfo(path).Length > 0);
    }

    [TestMethod]
    public void CreatesValidBlankXlsxWithOneSheet()
    {
        using var folder = TempDirectory.Create();
        string path = Path.Combine(folder.Path, "Таблица.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, path);
        using var doc = SpreadsheetDocument.Open(path, false);
        Assert.AreEqual(1, doc.WorkbookPart?.Workbook?.Sheets?.ChildElements.Count);
        Assert.AreEqual(1, doc.WorkbookPart?.WorksheetParts.Count());
    }

    [TestMethod]
    public void CreatesPptxWithSlideMasterLayoutThemeAndSlide()
    {
        using var folder = TempDirectory.Create();
        string path = Path.Combine(folder.Path, "Презентация.pptx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Presentation, path);
        using var doc = PresentationDocument.Open(path, false);
        Assert.AreEqual(1, doc.PresentationPart?.SlideParts.Count());
        Assert.AreEqual(1, doc.PresentationPart?.SlideMasterParts.Count());
        var master = doc.PresentationPart!.SlideMasterParts.Single();
        Assert.IsTrue(master.SlideLayoutParts.Any());
        Assert.IsTrue(master.ThemePart is not null);
    }

    [TestMethod]
    public void NeverOverwritesExistingDocumentOrWormCopy()
    {
        using var folder = TempDirectory.Create();
        string path = Path.Combine(folder.Path, "immutable.docx");
        File.WriteAllText(path, "ОРИГИНАЛ");
        Assert.ThrowsExactly<IOException>(() => OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, path));
        Assert.AreEqual("ОРИГИНАЛ", File.ReadAllText(path));
    }

    [TestMethod]
    public void RejectsMismatchedExtensionsAndDisallowedKinds()
    {
        using var folder = TempDirectory.Create();
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, Path.Combine(folder.Path, "x.xlsx")));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Pdf, Path.Combine(folder.Path, "x.pdf")));
        Assert.IsFalse(File.Exists(Path.Combine(folder.Path, "x.xlsx")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        private TempDirectory(string path) { Path = path; Directory.CreateDirectory(path); }
        public static TempDirectory Create() => new(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "getdocument-" + Guid.NewGuid().ToString("N")));
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
