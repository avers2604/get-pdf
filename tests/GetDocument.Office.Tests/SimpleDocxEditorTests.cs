using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GetDocument.Core.Workspace;
using GetDocument.Office;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class SimpleDocxEditorTests
{
    [TestMethod]
    public void BlankDocxCanBeEditedAndSavedAsNewRevisionWithoutTouchingOriginal()
    {
        using var folder = TestFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        string revised = Path.Combine(folder.Path, "revision.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);

        var snapshot = SimpleDocxEditor.Load(original);
        Assert.AreEqual("", snapshot.Text);
        var before = File.ReadAllBytes(original);

        SimpleDocxEditor.SaveNewRevision(original, snapshot.Sha256, revised, "Привет, ГЭТ!\nВторая строка");
        Assert.IsTrue(File.Exists(revised));
        Assert.IsTrue(before.SequenceEqual(File.ReadAllBytes(original)));
        Assert.AreEqual("Привет, ГЭТ!\nВторая строка", SimpleDocxEditor.Load(revised).Text);
        using var document = WordprocessingDocument.Open(revised, false);
        Assert.AreEqual(2, document.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Count());
    }

    [TestMethod]
    public void RejectsComplexDocumentWithoutModifyingAnything()
    {
        using var folder = TestFolder.Create();
        string original = Path.Combine(folder.Path, "rich.docx");
        string revised = Path.Combine(folder.Path, "out.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        using (var document = WordprocessingDocument.Open(original, true))
        {
            var paragraph = document.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Single();
            paragraph.AppendChild(new Run(new RunProperties(new Bold()), new Text("Styled")));
            document.MainDocumentPart.Document.Save();
        }
        var before = File.ReadAllBytes(original);
        Assert.ThrowsExactly<NotSupportedException>(() => SimpleDocxEditor.Load(original));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            SimpleDocxEditor.SaveNewRevision(original, "not-a-real-hash", revised, "Строка"));
        Assert.IsFalse(File.Exists(revised));
        Assert.IsTrue(before.SequenceEqual(File.ReadAllBytes(original)));
    }

    [TestMethod]
    public void NeverOverwritesExistingRevision()
    {
        using var folder = TestFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        string revised = Path.Combine(folder.Path, "revision.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        File.WriteAllText(revised, "НЕ ТРОГАТЬ");
        var snapshot = SimpleDocxEditor.Load(original);
        Assert.ThrowsExactly<IOException>(() =>
            SimpleDocxEditor.SaveNewRevision(original, snapshot.Sha256, revised, "Тест"));
        Assert.AreEqual("НЕ ТРОГАТЬ", File.ReadAllText(revised));
    }

    [TestMethod]
    public void RefusesStaleSourceAfterAnotherEditorChangesDocument()
    {
        using var folder = TestFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        string revised = Path.Combine(folder.Path, "revision.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        var snapshot = SimpleDocxEditor.Load(original);
        using (var document = WordprocessingDocument.Open(original, true))
        {
            document.MainDocumentPart!.Document.Body!.Append(new Paragraph(new Run(new Text("Изменено"))));
            document.MainDocumentPart.Document.Save();
        }
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SimpleDocxEditor.SaveNewRevision(original, snapshot.Sha256, revised, "Старый текст"));
        Assert.IsFalse(File.Exists(revised));
    }

    [TestMethod]
    public void RejectsInvalidXmlControlCharactersAndOversizedEdits()
    {
        using var folder = TestFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        string revised = Path.Combine(folder.Path, "revision.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        var hash = SimpleDocxEditor.Load(original).Sha256;
        Assert.ThrowsExactly<ArgumentException>(() =>
            SimpleDocxEditor.SaveNewRevision(original, hash, revised, "abc\0xyz"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SimpleDocxEditor.SaveNewRevision(original, hash, revised, new string('a', 600000)));
        Assert.IsFalse(File.Exists(revised));
    }

    [TestMethod]
    public void RejectsSavingToOriginalPath()
    {
        using var folder = TestFolder.Create();
        string original = Path.Combine(folder.Path, "original.docx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Text, original);
        var hash = SimpleDocxEditor.Load(original).Sha256;
        Assert.ThrowsExactly<IOException>(() =>
            SimpleDocxEditor.SaveNewRevision(original, hash, original, "Обновление"));
        Assert.AreEqual("", SimpleDocxEditor.Load(original).Text);
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; }
        private TestFolder(string path) { Path = path; Directory.CreateDirectory(path); }
        public static TestFolder Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "getdoc-test-" + Guid.NewGuid().ToString("N")));
        public void Dispose() => Directory.Delete(Path, true);
    }
}
