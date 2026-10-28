using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GetDocument.Core.Workspace;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class CalcChartAndValidationTests
{
    [TestMethod]
    public void NewCalcFeaturesAreExplicitAndReadOnlySafe()
    {
        string[] operations =
        [
            "calc-chart-column",
            "calc-chart-line",
            "calc-validate-whole-nonnegative",
            "calc-validate-decimal-nonnegative",
            "calc-validate-clear"
        ];
        foreach (string featureId in operations)
        {
            var registered = OfficeFeatureCatalog.Require("calc", featureId, readOnly: false);
            Assert.IsTrue(registered.RequiresEdit);
            Assert.AreEqual(featureId, registered.Id);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                OfficeFeatureCatalog.Require("calc", featureId, readOnly: true));
            Assert.ThrowsExactly<NotSupportedException>(() =>
                OfficeFeatureCatalog.Require("writer", featureId, readOnly: false));
        }
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task ChartsAndDataValidationSurviveXlsxRoundTripWhenLibreOfficeAvailable()
    {
        if (!LibreOfficeEditor.IsSupported)
            Assert.Inconclusive("Нужны Windows и локальный LibreOffice/UNO.");
        string folder = Path.Combine(Path.GetTempPath(), "getdoc-chart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "Показатели.xlsx");
        try
        {
            OfficeDocumentFactory.CreateNew(WorkspaceDocumentKind.Spreadsheet, path);
            using (var package = SpreadsheetDocument.Open(path, true))
            {
                var worksheet = package.WorkbookPart!.WorksheetParts.Single().Worksheet;
                var data = worksheet.GetFirstChild<SheetData>()!;
                data.Append(new Row(
                    TextCell("A1", "Период"),
                    TextCell("B1", "Значение")) { RowIndex = 1U });
                data.Append(new Row(TextCell("A2", "Январь"), NumericCell("B2", 17)) { RowIndex = 2U });
                data.Append(new Row(TextCell("A3", "Февраль"), NumericCell("B3", 22)) { RowIndex = 3U });
                data.Append(new Row(TextCell("A4", "Март"), NumericCell("B4", 27)) { RowIndex = 4U });
                worksheet.Save();
            }

            EditorDocument editor = await LibreOfficeEditor.Shared.OpenAsync(path);
            try
            {
                await SelectAsync(editor, "A1:B4");
                await editor.ExecuteFeatureAsync("calc-chart-column");
                await editor.ExecuteFeatureAsync("calc-chart-line");

                await SelectAsync(editor, "C2:C8");
                await editor.ExecuteFeatureAsync("calc-validate-whole-nonnegative");
                await editor.ExecuteFeatureAsync("calc-validate-decimal-nonnegative");
                await editor.ExecuteFeatureAsync("calc-validate-clear");

                await SelectAsync(editor, "D2:D8");
                await editor.ExecuteFeatureAsync("calc-validate-whole-nonnegative");
                await editor.SaveAsync();
            }
            finally { await editor.CloseAsync(); }

            using var result = SpreadsheetDocument.Open(path, false);
            var sheet = result.WorkbookPart!.WorksheetParts.Single();
            Assert.IsNotNull(sheet.DrawingsPart, "Chart shapes must survive an XLSX save.");
            Assert.IsTrue(sheet.Worksheet.Descendants<DataValidation>().Any(),
                "At least one numeric validation rule must survive the XLSX round trip.");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    private static Task SelectAsync(EditorDocument document, string address) =>
        document.DispatchAsync(".uno:GoToCell",
            new Dictionary<string, object> { ["ToPoint"] = address }, focus: false);

    private static Cell TextCell(string address, string text) =>
        new(new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(text)))
        {
            CellReference = address,
            DataType = CellValues.InlineString
        };

    private static Cell NumericCell(string address, int value) =>
        new(new CellValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            CellReference = address,
            DataType = CellValues.Number
        };
}
