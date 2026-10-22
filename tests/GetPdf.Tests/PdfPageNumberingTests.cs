using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfPageNumberingTests
{
    [TestMethod]
    public void FormatsCountTitlePageButHideItsNumber()
    {
        var plain = new PdfPageNumberingOptions(PdfPageNumberFormat.Number,
            PdfPageNumberPosition.BottomCenter, 1, SkipFirstPage: false);
        Assert.AreEqual("1", PdfPageNumbering.Label(0, 12, plain));
        Assert.AreEqual("12", PdfPageNumbering.Label(11, 12, plain));

        var ofTotal = plain with { Format = PdfPageNumberFormat.NumberOfTotal, StartAt = 3 };
        Assert.AreEqual("3 из 14", PdfPageNumbering.Label(0, 12, ofTotal));

        var titled = plain with { Format = PdfPageNumberFormat.PageNumberOfTotal, SkipFirstPage = true };
        Assert.IsNull(PdfPageNumbering.Label(0, 5, titled));
        Assert.AreEqual("Страница 2 из 5", PdfPageNumbering.Label(1, 5, titled));
    }

    [TestMethod]
    public void NumbersAreIndependentOfStampsAndSurviveRestore()
    {
        var plan = new PdfPagePlan(3);
        plan.SetStamp(1, "КОПИЯ");
        plan.SetPageNumbers(i => (i + 1).ToString(), PdfPageNumberPosition.BottomRight);

        Assert.AreEqual("КОПИЯ", plan.Pages[1].Stamp);
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, plan.Pages.Select(p => p.PageNumber).ToArray());
        Assert.IsTrue(plan.HasChanges);

        var snapshot = plan.Pages.ToArray();
        plan.SetPageNumbers(_ => null, PdfPageNumberPosition.BottomCenter);
        Assert.IsTrue(plan.Pages.All(p => p.PageNumber.Length == 0));
        plan.Restore(snapshot);
        Assert.AreEqual("3", plan.Pages[2].PageNumber);
        Assert.AreEqual(PdfPageNumberPosition.BottomRight, plan.Pages[2].NumberPosition);

        Assert.ThrowsExactly<ArgumentException>(() =>
            plan.SetPageNumbers(_ => "bad\nlabel", PdfPageNumberPosition.BottomCenter));
    }

    [TestMethod]
    public void SavedCopyContainsNumbersAndStampSeparately()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-numbers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.pdf");
            string output = Path.Combine(folder, "numbered.pdf");
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage(); pdf.AddPage();
                pdf.Save(source);
            }
            var plan = new PdfPagePlan(2);
            plan.SetStamp(0, "КОПИЯ");
            var options = new PdfPageNumberingOptions(PdfPageNumberFormat.NumberOfTotal,
                PdfPageNumberPosition.BottomCenter, 1, SkipFirstPage: false);
            plan.SetPageNumbers(i => PdfPageNumbering.Label(i, 2, options), options.Position);

            PdfDocumentWriter.SaveAs(source, output, plan.Pages);

            using var check = UglyToad.PdfPig.PdfDocument.Open(output);
            var first = check.GetPage(1);
            string text = first.Text;
            StringAssert.Contains(text, "КОПИЯ");
            StringAssert.Contains(text, "1 из 2");
            StringAssert.Contains(check.GetPage(2).Text, "2 из 2");
            // Bottom-centre: the number sits in the lower part of the page, not over the stamp.
            var numberWords = first.GetWords().Where(w => w.Text is "1" or "из" or "2").ToArray();
            Assert.IsTrue(numberWords.Length > 0);
            Assert.IsTrue(numberWords.All(w => w.BoundingBox.Bottom < first.Height * 0.15));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void CrashDraftKeepsPageNumbersAndOldDraftsStillLoad()
    {
        var plan = new PdfPagePlan(2);
        plan.SetPageNumbers(i => $"{i + 1} из 2", PdfPageNumberPosition.TopRight);
        var state = new PdfDraftStore.Manifest(1, @"C:\docs\a.pdf", new string('A', 64), 2,
            plan.Pages.ToArray(), 0, 1.0);
        string json = System.Text.Json.JsonSerializer.Serialize(state);
        var restored = PdfDraftStore.Validate(
            System.Text.Json.JsonSerializer.Deserialize<PdfDraftStore.Manifest>(json)!, 100);
        Assert.AreEqual("2 из 2", restored.Pages[1].PageNumber);
        Assert.AreEqual(PdfPageNumberPosition.TopRight, restored.Pages[1].NumberPosition);

        // A draft written before numbering existed has no such fields at all.
        string legacy = System.Text.RegularExpressions.Regex.Replace(json,
            ",\"PageNumber\":\"[^\"]*\",\"NumberPosition\":\\d+", "");
        Assert.IsFalse(legacy.Contains("PageNumber"));
        var old = PdfDraftStore.Validate(
            System.Text.Json.JsonSerializer.Deserialize<PdfDraftStore.Manifest>(legacy)!, 100);
        Assert.IsTrue(old.Pages.All(p => p.PageNumber.Length == 0));
    }

    [TestMethod]
    public void SaveWarningOnlyNamesStructuresThatExist()
    {
        var plain = new PdfInteractiveSummary(3, false, 0, 0, 0, 0, 0, 0, 0, false, false, false, false, true);
        Assert.IsNull(PdfViewerWindow.DescribeExportRisk(plain, hasOutline: false));
        Assert.AreEqual("закладки (оглавление)", PdfViewerWindow.DescribeExportRisk(plain, hasOutline: true));

        var signedForm = plain with { HasAcroForm = true, FormFieldNodes = 2, Annotations = 3,
            WidgetAnnotations = 2, TextAnnotations = 1, SignatureByteRanges = 1 };
        Assert.AreEqual("электронные подписи, поля формы, комментарии",
            PdfViewerWindow.DescribeExportRisk(signedForm, hasOutline: false));
    }

    [TestMethod]
    public void PrintingRotatesOnlyWhenPageAndPaperOrientationsDiffer()
    {
        var portrait = new Size(794, 1123);
        Assert.IsTrue(PdfPagePrintPaginator.ShouldRotate(1123, 794, portrait));
        Assert.IsFalse(PdfPagePrintPaginator.ShouldRotate(794, 1123, portrait));
        Assert.IsFalse(PdfPagePrintPaginator.ShouldRotate(500, 500, portrait));
        Assert.IsTrue(PdfPagePrintPaginator.ShouldRotate(794, 1123, new Size(1123, 794)));
    }

    [TestMethod]
    public void WindowsDialogRangeSelectsInclusiveClampedPages()
    {
        var pages = Enumerable.Range(0, 10).Select(i => new PdfPageReference(i, 0)).ToArray();
        var picked = PdfViewerWindow.SelectPrintRange(pages, new PageRange(3, 5));
        CollectionAssert.AreEqual(new[] { 2, 3, 4 }, picked.Select(p => p.SourcePage).ToArray());
        Assert.AreEqual(10, PdfViewerWindow.SelectPrintRange(pages, new PageRange(0, 99)).Count);
        CollectionAssert.AreEqual(new[] { 6, 7 },
            PdfViewerWindow.SelectPrintRange(pages, new PageRange(8, 7)).Select(p => p.SourcePage).ToArray());
    }
}
