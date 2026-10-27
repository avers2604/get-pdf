using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class CupsColorCapabilitiesTests
{
    [TestMethod]
    public void StandardIppModeIsParsedFromLpoptions()
    {
        string options = "media/Page size: *A4 A3\n" +
            "print-color-mode/Color Mode: *monochrome color\n";
        Assert.IsTrue(CupsColorCapabilities.Supports(options, PrintColorMode.Monochrome));
        Assert.IsTrue(CupsColorCapabilities.Supports(options, PrintColorMode.Color));
        Assert.IsTrue(CupsColorCapabilities.Supports(options, PrintColorMode.PrinterDefault));
    }

    [TestMethod]
    public void UnknownLegacyDriverCannotClaimColorSupport()
    {
        string options = "ColorModel/Color Model: *Gray RGB\n";
        Assert.IsFalse(CupsColorCapabilities.Supports(options, PrintColorMode.Color));
        Assert.IsFalse(CupsColorCapabilities.Supports(options, PrintColorMode.Monochrome));
        Assert.IsTrue(CupsColorCapabilities.Supports(options, PrintColorMode.PrinterDefault));
    }

    [TestMethod]
    public void CapabilityIsDeniedWhenPrinterLacksTheValue()
    {
        const string options = "print-color-mode/Color mode: *monochrome\n";
        Assert.IsFalse(CupsColorCapabilities.Supports(options, PrintColorMode.Color));
        Assert.IsTrue(CupsColorCapabilities.Supports(options, PrintColorMode.Monochrome));
    }

    [TestMethod]
    public void MalformedOrOversizedOutputFailsClosed()
    {
        Assert.IsFalse(CupsColorCapabilities.Supports("", PrintColorMode.Color));
        Assert.IsFalse(CupsColorCapabilities.Supports("print-color-mode: \n", PrintColorMode.Color));
        Assert.IsFalse(CupsColorCapabilities.Supports(
            new string('x', 65537), PrintColorMode.Color));
    }
}
