using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class CupsPrintArgumentsTests
{
    // lp runs on Linux, but the argv builder is checked on every developer OS:
    // a Unix path is not fully qualified on Windows, so map it to a drive path there.
    private static string Absolute(string unixPath) =>
        OperatingSystem.IsWindows() ? @"C:" + unixPath.Replace('/', '\\') : unixPath;

    [TestMethod]
    public void UsesArgumentListAndDoesNotInvokeShellOrPrinterDialog()
    {
        var plan = PrintJobPlan.Create("GET_HP", 15, "2-4,9", 3,
            PrintDuplex.LongEdge, PrintPaper.A4);
        var args = CupsPrintArguments.Create(plan, Absolute("/data/ПТО Шаврова/распечатка.pdf"));
        CollectionAssert.AreEqual(new[]
        {
            "-d", "GET_HP", "-n", "3", "-P", "2-4,9",
            "-o", "media=A4", "-o", "sides=two-sided-long-edge",
            "--", Absolute("/data/ПТО Шаврова/распечатка.pdf")
        }, args.ToArray());
    }

    [TestMethod]
    public void OneSidedA3DoesNotMakeUnknownPrinterDriverCalls()
    {
        var plan = PrintJobPlan.Create("Printer", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A3);
        var args = CupsPrintArguments.Create(plan, Absolute("/tmp/paper.pdf"));
        CollectionAssert.Contains(args.ToArray(), "media=A3");
        CollectionAssert.Contains(args.ToArray(), "sides=one-sided");
    }

    [TestMethod]
    public void NonPdfOrRelativePathsAreRejected()
    {
        var plan = PrintJobPlan.Create("Printer", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4);
        foreach (string path in new[] { "file.pdf", "/tmp/notes.docx", "/tmp/a.pdf\n", "" })
            Assert.ThrowsExactly<ArgumentException>(() => CupsPrintArguments.Create(plan, path));
    }
}
