using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfKeyboardHelpTests
{
    [TestMethod]
    public void OfflineHelpListsActualReaderAndTabCommands()
    {
        string help = PdfKeyboardHelp.Text;
        foreach (var shortcut in new[]
        {
            "Ctrl+O", "Ctrl+F", "Shift+F3", "Ctrl+G", "Ctrl+P", "Ctrl+S",
            "Ctrl+Z", "Ctrl+T", "Ctrl+W", "Ctrl+Tab", "Ctrl+Shift+L",
            "Ctrl+1", "F1", "F6", "F11", "Esc"
        })
            StringAssert.Contains(help, shortcut);
        StringAssert.Contains(help, "WORM");
        StringAssert.Contains(help, "КЭП");
        StringAssert.Contains(help, "эскизы");
        Assert.IsFalse(help.Contains("http://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(help.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }
}
