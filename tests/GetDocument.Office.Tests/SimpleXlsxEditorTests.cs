using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GetDocument.Core.Workspace;
using GetDocument.Office;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class SimpleXlsxEditorTests
{
    [TestMethod]
    public void SimpleWorkbookRoundTripsCyrillicAndLeadingEqualAsLiteralText()
    {
        using var temp = new Folder();
        string source = Path.Combine(temp.Path, "source.xlsx");
        string revision = Path.Combine(temp.Path, "revision.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, source);
        var snapshot = SimpleXlsxEditor.Load(source);
        Assert.AreEqual("", snapshot.Cells[0][0]);
        snapshot.Cells[0][0] = "ГЭТ";
        snapshot.Cells[1][2] = "=2+2";
        snapshot.Cells[9][9] = "    текст   ";
        SimpleXlsxEditor.SaveNewRevision(source, snapshot.Sha256, revision, snapshot.Cells);
        var result = SimpleXlsxEditor.Load(revision);
        Assert.AreEqual("ГЭТ", result.Cells[0][0]);
        Assert.AreEqual("=2+2", result.Cells[1][2]);
        Assert.AreEqual("    текст   ", result.Cells[9][9]);
        Assert.IsFalse(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(revision)));
        using var document = SpreadsheetDocument.Open(revision, false);
        var sheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet;
        var formula = sheet.Descendants<Cell>().Single(c => c.CellReference!.Value == "C2");
        Assert.IsNull(formula.CellFormula);
        Assert.AreEqual(CellValues.InlineString, formula.DataType!.Value);
    }

    [TestMethod]
    public void ExistingDestinationIsNeverOverwritten()
    {
        using var temp = new Folder();
        string source = Path.Combine(temp.Path, "original.xlsx");
        string target = Path.Combine(temp.Path, "existing.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, source);
        File.WriteAllText(target, "WORM");
        var snapshot = SimpleXlsxEditor.Load(source);
        Assert.ThrowsExactly<IOException>(() =>
            SimpleXlsxEditor.SaveNewRevision(source, snapshot.Sha256, target, snapshot.Cells));
        Assert.AreEqual("WORM", File.ReadAllText(target));
    }

    [TestMethod]
    public void ConcurrentModificationIsDetectedBeforeNewRevisionIsPublished()
    {
        using var temp = new Folder();
        string source = Path.Combine(temp.Path, "original.xlsx");
        string target = Path.Combine(temp.Path, "revision.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, source);
        var snapshot = SimpleXlsxEditor.Load(source);
        using (var doc = SpreadsheetDocument.Open(source, true))
        {
            doc.WorkbookPart!.Workbook.Sheets!.GetFirstChild<Sheet>()!.Name = "НовыйЛист";
            doc.WorkbookPart.Workbook.Save();
        }
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SimpleXlsxEditor.SaveNewRevision(source, snapshot.Sha256, target, snapshot.Cells));
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public void FormulaWorkbookMustNotBeSilentlyFlattened()
    {
        using var temp = new Folder();
        string source = Path.Combine(temp.Path, "formulas.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, source);
        using (var doc = SpreadsheetDocument.Open(source, true))
        {
            var sheet = doc.WorkbookPart!.WorksheetParts.Single().Worksheet;
            var data = sheet.GetFirstChild<SheetData>()!;
            data.Append(new Row(new Cell(new CellFormula("2+2")) { CellReference = "A1" }) { RowIndex = 1U });
            sheet.Save();
        }
        Assert.ThrowsExactly<NotSupportedException>(() => SimpleXlsxEditor.Load(source));
    }

    [TestMethod]
    public void EditorRefusesCellsOutsideSupportedGridOrXmlControls()
    {
        using var temp = new Folder();
        string source = Path.Combine(temp.Path, "original.xlsx");
        string destination = Path.Combine(temp.Path, "revised.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, source);
        var snapshot = SimpleXlsxEditor.Load(source);
        snapshot.Cells[0][0] = "Bad\u0001";
        Assert.ThrowsExactly<ArgumentException>(() =>
            SimpleXlsxEditor.SaveNewRevision(source, snapshot.Sha256, destination, snapshot.Cells));
        Assert.IsFalse(File.Exists(destination));
        var wrong = new[] { new[] { "only one cell" } };
        Assert.ThrowsExactly<ArgumentException>(() =>
            SimpleXlsxEditor.SaveNewRevision(source, snapshot.Sha256, destination, wrong));
    }

    [TestMethod]
    public void SavingNewVersionNeverMutatesOriginalBytes()
    {
        using var temp = new Folder();
        string original = Path.Combine(temp.Path, "source.xlsx");
        string destination = Path.Combine(temp.Path, "new-version.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, original);
        byte[] before = File.ReadAllBytes(original);
        var snapshot = SimpleXlsxEditor.Load(original);
        snapshot.Cells[0][0] = "Изменено";
        SimpleXlsxEditor.SaveNewRevision(original, snapshot.Sha256, destination, snapshot.Cells);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(original));
        Assert.AreEqual("Изменено", SimpleXlsxEditor.Load(destination).Cells[0][0]);
    }

    [TestMethod]
    public void TooLongCellFailsWithoutPublishingPartialFile()
    {
        using var temp = new Folder();
        string original = Path.Combine(temp.Path, "source.xlsx");
        string destination = Path.Combine(temp.Path, "out.xlsx");
        OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, original);
        var snapshot = SimpleXlsxEditor.Load(original);
        snapshot.Cells[0][0] = new string('X', 2049);
        Assert.ThrowsExactly<ArgumentException>(() =>
            SimpleXlsxEditor.SaveNewRevision(original, snapshot.Sha256, destination, snapshot.Cells));
        Assert.IsFalse(File.Exists(destination));
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gd-xlsx-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
