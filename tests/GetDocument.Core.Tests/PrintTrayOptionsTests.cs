using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintTrayOptionsTests
{
    [TestMethod]
    public void InputSlotIsParsedFromCupsOptionsAndSorted()
    {
        const string output = "PageSize/Page Size: *A4 A3\n" +
            "InputSlot/Paper Source: *Auto Tray1 Tray2 Manual\n";
        var sources = PrintTrayOptions.ParseCups(output);
        Assert.AreEqual(4, sources.Count);
        Assert.AreEqual("InputSlot", sources[0].Option);
        Assert.AreEqual("Auto", sources[0].Value);
        Assert.AreEqual("Tray1", sources[1].Value);
        Assert.AreEqual("Tray2", sources[2].Value);
        Assert.AreEqual("Manual", sources[3].Value);
    }

    [TestMethod]
    public void DriverlessIppCanUseMediaSourceWithoutInventingTrays()
    {
        const string output = "media-source/Source: *auto tray-1 tray-2\n";
        var sources = PrintTrayOptions.ParseCups(output);
        Assert.AreEqual(3, sources.Count);
        Assert.IsTrue(sources.All(x => x.Option == "media-source"));
        Assert.AreEqual("tray-2", sources[2].Value);
    }

    [TestMethod]
    public void UnknownOrUnsafeDriverOutputIsNotAccepted()
    {
        foreach (string output in new[]
        {
            "", new string('A', 65537), "InputSlot: /bin/sh -c", "InputSlot: tray\r-1",
            "MediaType/Media: A4 Plain\n", "InputSlot: ../etc/shadow\n"
        })
            Assert.AreEqual(0, PrintTrayOptions.ParseCups(output).Count);
    }

    [TestMethod]
    public void CannotForgePrinterTrayOrPassUnvalidatedOption()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PrintTrayOptions.Make("InputSlot", "-bad"));
        Assert.ThrowsExactly<ArgumentException>(() => PrintTrayOptions.Make("Danger", "tray-1"));
        Assert.ThrowsExactly<ArgumentException>(() => PrintTrayOptions.Make("InputSlot", "T ray"));
        var available = PrintTrayOptions.ParseCups("InputSlot/Source: *Auto Tray1");
        Assert.IsTrue(PrintTrayOptions.IsSupported(available, available[1]));
        Assert.IsFalse(PrintTrayOptions.IsSupported(available,
            PrintTrayOptions.Make("InputSlot", "Tray9")));
    }

    [TestMethod]
    public void DefaultPrintJobDoesNotOverrideInputBin()
    {
        var plan = PrintJobPlan.Create("Printer", 3, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4);
        Assert.IsNull(plan.Tray);
        var tray = PrintTrayOptions.Make("InputSlot", "Tray2");
        var chosen = PrintJobPlan.Create("Printer", 3, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4, tray: tray);
        Assert.AreEqual(tray, chosen.Tray);
    }

    [TestMethod]
    public void CUPSArgumentsContainOnlyPreviouslyValidatedTrayTokens()
    {
        var plan = PrintJobPlan.Create("Printer", 2, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4,
            tray: PrintTrayOptions.Make("InputSlot", "Tray2"));
        string pdf = OperatingSystem.IsWindows() ? @"C:\data\test.pdf" : "/tmp/test.pdf";
        var options = CupsPrintArguments.Create(plan, pdf).ToArray();
        CollectionAssert.Contains(options, "InputSlot=Tray2");
    }
}
