using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class WindowsTrayCapabilitiesTests
{
    // Shape of a real HP DeskJet 5000 PrintCapabilities document (trimmed): a keyword
    // AutoSelect option plus a vendor tray that the WPF InputBin enum reports as Unknown.
    private const string Hp = """
        <psf:PrintCapabilities xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
            xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords"
            xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema"
            xmlns:ns0000="http://schemas.hp.com/ptpc/2006/1" version="1">
          <psf:Feature name="psk:JobInputBin">
            <psf:Option name="psk:AutoSelect"><psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">Automatically Select</psf:Value></psf:Property></psf:Option>
            <psf:Option name="ns0000:_1"><psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">Main Tray</psf:Value></psf:Property></psf:Option>
            <psf:Option name="ns0000:_2"><psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">Лоток 2 (A3)</psf:Value></psf:Property></psf:Option>
            <psf:Option name="psk:Manual"/>
          </psf:Feature>
          <psf:Feature name="ns0000:JobInputBinPrev"><psf:Option name="ns0000:_9"/></psf:Feature>
        </psf:PrintCapabilities>
        """;

    [TestMethod]
    public void VendorTraysKeepTheDriversNamesAndAutoSelectIsLeftToTheWizard()
    {
        var trays = WindowsTrayCapabilities.Parse(Hp);
        Assert.HasCount(3, trays);
        Assert.AreEqual("_1", trays[0].LocalName);
        Assert.AreEqual("Main Tray", trays[0].Label);
        Assert.AreEqual("http://schemas.hp.com/ptpc/2006/1", trays[0].NamespaceUri);
        Assert.AreEqual("Лоток 2 (A3)", trays[1].Label);
        Assert.AreEqual("Manual", trays[2].Label, "a keyword option without a display name shows its keyword");
        Assert.IsTrue(trays.All(t => t.Feature == "JobInputBin"));
    }

    [TestMethod]
    public void AutoSelectOnlyDriversOfferNoTrays()
    {
        const string pdfPrinter = """
            <psf:PrintCapabilities xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords" version="1">
              <psf:Feature name="psk:JobInputBin"><psf:Option name="psk:AutoSelect"/></psf:Feature>
            </psf:PrintCapabilities>
            """;
        Assert.HasCount(0, WindowsTrayCapabilities.Parse(pdfPrinter));
        Assert.HasCount(0, WindowsTrayCapabilities.Parse(""));
        Assert.HasCount(0, WindowsTrayCapabilities.Parse("<not xml"));
        Assert.HasCount(0, WindowsTrayCapabilities.Parse("<!DOCTYPE x [<!ENTITY a 'b'>]><x>&a;</x>"));
    }

    [TestMethod]
    public void UnsafeOptionNamesAndLabelsAreNotPassedOn()
    {
        string xml = Hp.Replace("ns0000:_2", "ns0000:bad name").Replace("Main Tray", "\tMain Tray\r\n");
        var trays = WindowsTrayCapabilities.Parse(xml);
        Assert.IsFalse(trays.Any(t => t.LocalName.Contains(' ')));
        Assert.AreEqual("Main Tray", trays[0].Label);
    }

    [TestMethod]
    public void TicketFragmentSelectsTheVendorTrayAndIsRecognisedAfterValidation()
    {
        var tray = WindowsTrayCapabilities.Parse(Hp)[0];
        string delta = WindowsTrayCapabilities.TicketDelta(tray);
        StringAssert.Contains(delta, "name=\"psk:JobInputBin\"");
        Assert.IsTrue(WindowsTrayCapabilities.TicketSelects(delta, tray));
        // The driver may use its own prefix in the validated ticket: only the namespace counts.
        string validated = delta.Replace("getbin:", "ns0000:").Replace("xmlns:getbin=", "xmlns:ns0000=");
        Assert.IsTrue(WindowsTrayCapabilities.TicketSelects(validated, tray));
        Assert.IsFalse(WindowsTrayCapabilities.TicketSelects(delta.Replace("_1", "_2"), tray), "dropped by the driver");
        Assert.IsFalse(WindowsTrayCapabilities.TicketSelects("", tray));
    }

    [TestMethod]
    public void PlansKeepTheDriverLabelButOnlyTheValueSelectsTheTray()
    {
        var tray = PrintTrayOptions.Make("InputBin", "_1", "Main Tray");
        var plan = PrintJobPlan.Create("HP", 1, "", 1, PrintDuplex.OneSided, PrintPaper.A4, tray: tray);
        Assert.AreEqual("Main Tray", plan.Tray!.Label);
        Assert.IsTrue(PrintTrayOptions.IsSupported([PrintTrayOptions.Make("InputBin", "_1", "Основной лоток")], tray));
        Assert.IsTrue(PrintTrayOptions.IsAutomatic(PrintTrayOptions.Make("InputSlot", "Auto")));
        Assert.IsFalse(PrintTrayOptions.IsAutomatic(tray));
    }
}
