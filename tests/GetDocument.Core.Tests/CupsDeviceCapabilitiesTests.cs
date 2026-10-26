using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class CupsDeviceCapabilitiesTests
{
    [TestMethod]
    public void PrinterPageSizeOptionsRestrictA3WhenUnavailable()
    {
        const string a4Only = "PageSize/Page Size: *A4 Letter\n";
        Assert.IsTrue(CupsDeviceCapabilities.Supports(a4Only, PrintPaper.A4,
            PrintDuplex.OneSided));
        Assert.IsFalse(CupsDeviceCapabilities.Supports(a4Only, PrintPaper.A3,
            PrintDuplex.OneSided));

        const string both = "PageSize/Page Size: *A4 A3 Letter\n";
        Assert.IsTrue(CupsDeviceCapabilities.Supports(both, PrintPaper.A3,
            PrintDuplex.OneSided));
    }

    [TestMethod]
    public void DriverlessIppMediaSizeNamesAreRecognized()
    {
        const string output = "media/Page Size: *iso_a4_210x297mm iso_a3_297x420mm\n" +
            "sides/Sides: *one-sided two-sided-long-edge\n";
        Assert.IsTrue(CupsDeviceCapabilities.Supports(output, PrintPaper.A3,
            PrintDuplex.LongEdge));
        Assert.IsFalse(CupsDeviceCapabilities.Supports(output, PrintPaper.A3,
            PrintDuplex.ShortEdge));
    }

    [TestMethod]
    public void LegacyPpdDuplexNoTumbleAndTumbleMapToRequestedSides()
    {
        const string output = "PageSize/Page Size: *A4 A3\n" +
            "Duplex/Two-sided: *None DuplexNoTumble DuplexTumble\n";
        Assert.IsTrue(CupsDeviceCapabilities.Supports(output, PrintPaper.A3,
            PrintDuplex.LongEdge));
        Assert.IsTrue(CupsDeviceCapabilities.Supports(output, PrintPaper.A3,
            PrintDuplex.ShortEdge));
    }

    [TestMethod]
    public void MissingCapabilitiesNeverClaimA3OrDuplex()
    {
        Assert.IsTrue(CupsDeviceCapabilities.Supports("", PrintPaper.A4,
            PrintDuplex.OneSided));
        Assert.IsFalse(CupsDeviceCapabilities.Supports("", PrintPaper.A3,
            PrintDuplex.OneSided));
        Assert.IsFalse(CupsDeviceCapabilities.Supports("", PrintPaper.A4,
            PrintDuplex.LongEdge));
    }

    [TestMethod]
    public void OversizedOrMalformedOutputFailsClosedOnExplicitSettings()
    {
        string garbage = new string('x', 65537);
        Assert.IsFalse(CupsDeviceCapabilities.Supports(garbage, PrintPaper.A3,
            PrintDuplex.LongEdge));
        Assert.IsFalse(CupsDeviceCapabilities.Supports(
            "PageSize/Page Size: A4 A3<script>\n", PrintPaper.A3,
            PrintDuplex.OneSided));
    }
}
