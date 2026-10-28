using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class OfficeFeatureCatalogTests
{
    [TestMethod]
    public void ExposesOnlyFeaturesForActiveDocumentKind()
    {
        CollectionAssert.AreEquivalent(
            new[] { "writer-toc-insert", "writer-toc-update" },
            OfficeFeatureCatalog.ForKind("writer").Select(x => x.Id).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "calc-freeze-top-row", "calc-freeze-first-column", "calc-unfreeze", "calc-recalculate",
                "calc-print-used-area", "calc-clear-print-area",
                "calc-repeat-first-row", "calc-stop-repeat-rows",
                "calc-chart-column", "calc-chart-line",
                "calc-validate-whole-nonnegative", "calc-validate-decimal-nonnegative", "calc-validate-clear" },
            OfficeFeatureCatalog.ForKind("calc").Select(x => x.Id).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "impress-duplicate-slide" },
            OfficeFeatureCatalog.ForKind("impress").Select(x => x.Id).ToArray());
        Assert.AreEqual(0, OfficeFeatureCatalog.ForKind("draw").Count);
        Assert.AreEqual(0, OfficeFeatureCatalog.ForKind("unknown").Count);
    }

    [TestMethod]
    public void ReadOnlyDocumentRejectsEveryContentChangingFeature()
    {
        foreach (OfficeFeatureDefinition feature in OfficeFeatureCatalog.All.Where(x => x.RequiresEdit))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                OfficeFeatureCatalog.Require(feature.Kind, feature.Id, readOnly: true),
                feature.Id);
            Assert.AreEqual(feature.Id, OfficeFeatureCatalog.Require(feature.Kind, feature.Id, readOnly: false).Id);
        }
    }

    [TestMethod]
    public void ReadOnlyDocumentMayChangeLocalSpreadsheetView()
    {
        foreach (string id in new[] { "calc-freeze-top-row", "calc-freeze-first-column", "calc-unfreeze" })
            Assert.AreEqual(id, OfficeFeatureCatalog.Require("calc", id, readOnly: true).Id);
    }

    [TestMethod]
    public void PrintLayoutCommandsRequireWriteAccessAndRemainCalcOnly()
    {
        foreach (string command in new[] { "calc-print-used-area", "calc-clear-print-area",
                     "calc-repeat-first-row", "calc-stop-repeat-rows" })
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => OfficeFeatureCatalog.Require("calc", command, readOnly: true));
            Assert.AreEqual(command, OfficeFeatureCatalog.Require("calc", command, readOnly: false).Id);
            Assert.ThrowsExactly<NotSupportedException>(
                () => OfficeFeatureCatalog.Require("writer", command, readOnly: false));
        }
    }

    [TestMethod]
    public void ChartAndValidationCommandsRequireEditableCalc()
    {
        foreach (string command in new[] { "calc-chart-column", "calc-chart-line",
                     "calc-validate-whole-nonnegative", "calc-validate-decimal-nonnegative",
                     "calc-validate-clear" })
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => OfficeFeatureCatalog.Require("calc", command, readOnly: true));
            Assert.AreEqual(command, OfficeFeatureCatalog.Require("calc", command, readOnly: false).Id);
            Assert.ThrowsExactly<NotSupportedException>(
                () => OfficeFeatureCatalog.Require("writer", command, readOnly: false));
            Assert.ThrowsExactly<NotSupportedException>(
                () => OfficeFeatureCatalog.Require("impress", command, readOnly: false));
        }
    }

    [TestMethod]
    public void RejectsCrossKindAndUnknownOperations()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OfficeFeatureCatalog.Require("calc", "writer-toc-insert", readOnly: false));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OfficeFeatureCatalog.Require("impress", "calc-freeze-top-row", readOnly: false));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OfficeFeatureCatalog.Require("writer", "open-arbitrary-url", readOnly: false));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            OfficeFeatureCatalog.Require("writer", "", readOnly: false));
    }

    [TestMethod]
    public void FeatureIdsAndLabelsAreUniqueAndNonEmpty()
    {
        var ids = OfficeFeatureCatalog.All.Select(x => x.Id).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(OfficeFeatureCatalog.All.All(x => x.Title.Length > 2 && x.Id.Length > 3));
    }

    [TestMethod]
    public void AllCatalogCommandsExistInEmbeddedPythonBridgeWhitelist()
    {
        var assembly = typeof(LibreOfficeEditor).Assembly;
        string name = assembly.GetManifestResourceNames().Single(x => x.EndsWith(".lo_editor.py", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name);
        Assert.IsNotNull(stream);
        using var reader = new StreamReader(stream);
        string python = reader.ReadToEnd();
        foreach (var item in OfficeFeatureCatalog.All)
            StringAssert.Contains(python, "\"" + item.Id + "\":");
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task ExecutesDirectUnoActionsWhenLibreOfficeInstalled()
    {
        if (!LibreOfficeEditor.IsSupported)
            Assert.Inconclusive("Нужен Windows и комплект LibreOffice с Python.");
        string folder = Path.Combine(Path.GetTempPath(), "gd-features-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string docx = Path.Combine(folder, "Служебная записка.docx");
            string xlsx = Path.Combine(folder, "Таблица.xlsx");
            string pptx = Path.Combine(folder, "Презентация.pptx");
            OfficeDocumentFactory.CreateNew(GetDocument.Core.Workspace.WorkspaceDocumentKind.Text, docx);
            OfficeDocumentFactory.CreateNew(GetDocument.Core.Workspace.WorkspaceDocumentKind.Spreadsheet, xlsx);
            OfficeDocumentFactory.CreateNew(GetDocument.Core.Workspace.WorkspaceDocumentKind.Presentation, pptx);

            var writer = await LibreOfficeEditor.Shared.OpenAsync(docx);
            try
            {
                await writer.ExecuteFeatureAsync("writer-toc-insert");
                await writer.ExecuteFeatureAsync("writer-toc-update");
                await writer.SaveAsync();
                Assert.IsTrue(new FileInfo(docx).Length > 0);
            }
            finally { await writer.CloseAsync(); }

            var calc = await LibreOfficeEditor.Shared.OpenAsync(xlsx);
            try
            {
                await calc.ExecuteFeatureAsync("calc-freeze-top-row");
                await calc.ExecuteFeatureAsync("calc-freeze-first-column");
                await calc.ExecuteFeatureAsync("calc-unfreeze");
                await calc.ExecuteFeatureAsync("calc-recalculate");
                await calc.ExecuteFeatureAsync("calc-print-used-area");
                await calc.ExecuteFeatureAsync("calc-repeat-first-row");
                await calc.ExecuteFeatureAsync("calc-stop-repeat-rows");
                await calc.ExecuteFeatureAsync("calc-clear-print-area");
                await calc.ExecuteFeatureAsync("calc-validate-whole-nonnegative");
                await calc.ExecuteFeatureAsync("calc-validate-decimal-nonnegative");
                await calc.ExecuteFeatureAsync("calc-validate-clear");
                await calc.SaveAsync();
                Assert.IsTrue(new FileInfo(xlsx).Length > 0);
                Assert.ThrowsExactly<NotSupportedException>(() =>
                    OfficeFeatureCatalog.Require(calc.Kind, "writer-toc-update", calc.ReadOnly));
            }
            finally { await calc.CloseAsync(); }

            var impress = await LibreOfficeEditor.Shared.OpenAsync(pptx);
            try
            {
                await impress.ExecuteFeatureAsync("impress-duplicate-slide");
                await impress.SaveAsync();
                Assert.IsTrue(new FileInfo(pptx).Length > 0);
            }
            finally { await impress.CloseAsync(); }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }
}
