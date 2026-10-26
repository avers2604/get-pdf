using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintRejectionTests
{
    [TestMethod]
    public void RefusalsBeforeSpoolingSayWhatIsUnsupportedAndAllowARetry()
    {
        string? message = PrintRejection.Describe(PrintRejection.Unsupported, "unsupported:orientation", "HP DeskJet 5000 series");
        Assert.IsNotNull(message);
        StringAssert.Contains(message, "«HP DeskJet 5000 series» не поддерживает выбранную ориентацию");
        StringAssert.Contains(message, "Задание не отправлено");
        StringAssert.Contains(PrintRejection.Describe(4, "unsupported:color", "P")!, "цветовой режим");
        StringAssert.Contains(PrintRejection.Describe(4, "unsupported:nup", "P")!, "страниц на листе");
        StringAssert.Contains(PrintRejection.Describe(4, "garbage", "P")!, "одну из выбранных настроек");
        StringAssert.Contains(PrintRejection.Describe(PrintRejection.Changed, "", "P")!, "Задание не отправлено");
        StringAssert.Contains(PrintRejection.Describe(PrintRejection.BadRequest, null, "P")!, "Задание не отправлено");
    }

    [TestMethod]
    public void FailuresWhileSpoolingStayAmbiguous()
    {
        Assert.IsNull(PrintRejection.Describe(0, "submitted", "P"));
        Assert.IsNull(PrintRejection.Describe(5, "", "P"), "a driver exception during XPS write may already have queued the job");
        Assert.IsNull(PrintRejection.Describe(1, "", "P"));
    }

    [TestMethod]
    public void SummaryUsesRussianNames()
    {
        Assert.AreEqual("альбомная", PrintRejection.Orientation(PrintOrientation.Landscape));
        Assert.AreEqual("книжная", PrintRejection.Orientation(PrintOrientation.Portrait));
        Assert.AreEqual("автоматически (по документу)", PrintRejection.Orientation(PrintOrientation.Automatic));
        Assert.AreEqual("чёрно-белая", PrintRejection.Color(PrintColorMode.Monochrome));
        Assert.AreEqual("цветная", PrintRejection.Color(PrintColorMode.Color));
        Assert.AreEqual("как настроено в принтере", PrintRejection.Color(PrintColorMode.PrinterDefault));
        Assert.AreEqual("A3", PrintRejection.Paper(PrintPaper.A3));
    }
}
