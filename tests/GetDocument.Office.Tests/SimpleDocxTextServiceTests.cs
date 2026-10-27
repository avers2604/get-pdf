using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GetDocument.Core.Workspace;
using GetDocument.Office;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class SimpleDocxTextServiceTests
{
    [TestMethod]
    public void CanEditCreatedDocxAndSaveNewVersionWithoutChangingOriginal()
    {
        using var folder = TempFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        string edited = Path.Combine(folder.Path, "edited.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        byte[] before = File.ReadAllBytes(original);
        var snapshot = SimpleDocxTextService.Open(original);
        Assert.AreEqual("", snapshot.Text);
        SimpleDocxTextService.SaveCopy(snapshot, "Первая строка\nВторая строка", edited);
        Assert.IsTrue(File.Exists(edited));
        Assert.IsTrue(before.SequenceEqual(File.ReadAllBytes(original)));
        Assert.AreEqual("Первая строка\nВторая строка",
            SimpleDocxTextService.Open(edited).Text);
    }

    [TestMethod]
    public void PreservesBlankParagraphsAndWhitespace()
    {
        using var folder = TempFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        string edited = Path.Combine(folder.Path, "edited.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        var snapshot = SimpleDocxTextService.Open(original);
        SimpleDocxTextService.SaveCopy(snapshot, "  Текст  \n\nКонец", edited);
        Assert.AreEqual("  Текст  \n\nКонец",
            SimpleDocxTextService.Open(edited).Text);
    }

    [TestMethod]
    public void RejectsComplexDocumentRatherThanDestroyingFormatting()
    {
        using var folder = TempFolder.Create();
        string original = Path.Combine(folder.Path, "complex.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        using (var doc = WordprocessingDocument.Open(original, true))
        {
            doc.MainDocumentPart!.Document.Body!.AppendChild(new Table(new TableRow()));
            doc.MainDocumentPart.Document.Save();
        }
        Assert.ThrowsExactly<NotSupportedException>(() => SimpleDocxTextService.Open(original));
    }

    [TestMethod]
    public void RefusesSourceModifiedAfterEditorOpened()
    {
        using var folder = TempFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        string edited = Path.Combine(folder.Path, "edited.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        var snapshot = SimpleDocxTextService.Open(original);
        File.AppendAllText(original, "changed");
        Assert.ThrowsExactly<IOException>(() =>
            SimpleDocxTextService.SaveCopy(snapshot, "Несохранённая правка", edited));
        Assert.IsFalse(File.Exists(edited));
    }

    [TestMethod]
    public void RefusesOverwritingSourceOrOtherExistingFile()
    {
        using var folder = TempFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        string other = Path.Combine(folder.Path, "other.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        File.WriteAllText(other, "WORM");
        var snapshot = SimpleDocxTextService.Open(original);
        Assert.ThrowsExactly<IOException>(() =>
            SimpleDocxTextService.SaveCopy(snapshot, "Updated", original));
        Assert.ThrowsExactly<IOException>(() =>
            SimpleDocxTextService.SaveCopy(snapshot, "Updated", other));
        Assert.AreEqual("WORM", File.ReadAllText(other));
    }

    [TestMethod]
    public void InvalidDestinationOrControlCharacterCannotCreateFile()
    {
        using var folder = TempFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        var snapshot = SimpleDocxTextService.Open(original);
        Assert.ThrowsExactly<ArgumentException>(() =>
            SimpleDocxTextService.SaveCopy(snapshot, "Normal", Path.Combine(folder.Path, "wrong.xlsx")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SimpleDocxTextService.SaveCopy(snapshot, "bad\ttext", Path.Combine(folder.Path, "other.docx")));
        Assert.IsFalse(File.Exists(Path.Combine(folder.Path, "other.docx")));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; }
        private TempFolder(string path) { Path = path; Directory.CreateDirectory(Path); }
        public static TempFolder Create() => new(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "getdocument-ed-" + Guid.NewGuid().ToString("N")));
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
