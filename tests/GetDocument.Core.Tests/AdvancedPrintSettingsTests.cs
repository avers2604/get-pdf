using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class AdvancedPrintSettingsTests
{
    [TestMethod]
    public void ExistingCallersRetainSafeDeviceDefaults()
    {
        var plan = PrintJobPlan.Create("Queue", 9, "", 2, PrintDuplex.OneSided, PrintPaper.A4);
        Assert.AreEqual(PrintOrientation.Automatic, plan.Orientation);
        Assert.AreEqual(PrintColorMode.PrinterDefault, plan.Color);
        Assert.AreEqual(PrintPagesPerSheet.One, plan.PagesPerSheet);
        Assert.AreEqual(18, plan.PageCountToPrint);
        Assert.AreEqual(18, plan.SheetSidesToPrint);
    }

    [TestMethod]
    public void ExplicitLandscapeMonochromeFourUpIsPreserved()
    {
        var plan = PrintJobPlan.Create("Queue", 9, "", 2,
            PrintDuplex.LongEdge, PrintPaper.A3,
            PrintOrientation.Landscape, PrintColorMode.Monochrome, PrintPagesPerSheet.Four);
        Assert.AreEqual(PrintOrientation.Landscape, plan.Orientation);
        Assert.AreEqual(PrintColorMode.Monochrome, plan.Color);
        Assert.AreEqual(PrintPagesPerSheet.Four, plan.PagesPerSheet);
        Assert.AreEqual(18, plan.PageCountToPrint);
        Assert.AreEqual(6, plan.SheetSidesToPrint);
    }

    [TestMethod]
    public void SelectedPagesWithNUpMustNotSilentlyPrintWrongSheetsInCups()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            PrintJobPlan.Create("Queue", 10, "2-5", 1,
                PrintDuplex.OneSided, PrintPaper.A4,
                PrintOrientation.Portrait, PrintColorMode.PrinterDefault, PrintPagesPerSheet.Two));
    }

    [TestMethod]
    public void UnsupportedValuesCannotReachDevice()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            PrintJobPlan.Create("Queue", 3, "", 1, PrintDuplex.OneSided, PrintPaper.A4,
                (PrintOrientation)98));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PrintJobPlan.Create("Queue", 3, "", 1, PrintDuplex.OneSided, PrintPaper.A4,
                color: (PrintColorMode)98));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PrintJobPlan.Create("Queue", 3, "", 1, PrintDuplex.OneSided, PrintPaper.A4,
                pagesPerSheet: (PrintPagesPerSheet)3));
    }

    [TestMethod]
    public void ExplicitCupsOptionsHaveStableLiteralValues()
    {
        var plan = PrintJobPlan.Create("Queue", 4, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4,
            PrintOrientation.Landscape, PrintColorMode.Monochrome, PrintPagesPerSheet.Two);
        string path = OperatingSystem.IsWindows() ? @"C:\test\a.pdf" : "/tmp/a.pdf";
        var options = CupsPrintArguments.Create(plan, path).ToArray();
        CollectionAssert.Contains(options, "orientation-requested=4");
        CollectionAssert.Contains(options, "print-color-mode=monochrome");
        CollectionAssert.Contains(options, "number-up=2");
    }

    [TestMethod]
    public void DefaultsDoNotOverrideDriverColorOrientationOrNUp()
    {
        var plan = PrintJobPlan.Create("Queue", 2, "", 1, PrintDuplex.OneSided, PrintPaper.A4);
        string path = OperatingSystem.IsWindows() ? @"C:\test\a.pdf" : "/tmp/a.pdf";
        var args = CupsPrintArguments.Create(plan, path);
        Assert.IsFalse(args.Any(x => x.StartsWith("print-color-mode=", StringComparison.Ordinal)));
        Assert.IsFalse(args.Any(x => x.StartsWith("orientation-requested=", StringComparison.Ordinal)));
        Assert.IsFalse(args.Any(x => x.StartsWith("number-up=", StringComparison.Ordinal)));
    }
}
