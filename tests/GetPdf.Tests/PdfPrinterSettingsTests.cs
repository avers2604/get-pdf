using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfPrinterSettingsTests
{
    [TestMethod]
    public void StoredOptionsRoundTripAndBadJsonFallsBack()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-printsettings-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "print.json");
            var choices = new PdfPrinterSettings(
                PdfPaperChoice.A3, PdfOrientationChoice.Landscape,
                PdfDuplexChoice.LongEdge, 3);
            PdfPrinterSettingsStore.SaveTo(file, choices);
            Assert.AreEqual(choices, PdfPrinterSettingsStore.ReadFrom(file));
            File.WriteAllText(file, "{malformed");
            Assert.AreEqual(PdfPrinterSettings.Default,
                PdfPrinterSettingsStore.ReadFrom(file));
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void PrinterMustNotSilentlyChangeRequestedPaperDuplexOrOrientation()
    {
        PdfPrinterSettingsStore.RejectDriverOverrides(System.Printing.ConflictStatus.NoConflict);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            PdfPrinterSettingsStore.RejectDriverOverrides(
                System.Printing.ConflictStatus.ConflictResolved));
    }

    [TestMethod]
    public void InvalidCopyCountOrEnumRejectedBeforePrinting()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfPrinterSettingsStore.Validate(new(
                PdfPaperChoice.A4, PdfOrientationChoice.Portrait,
                PdfDuplexChoice.OneSided, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfPrinterSettingsStore.Validate(new(
                (PdfPaperChoice)777, PdfOrientationChoice.Default,
                PdfDuplexChoice.Default, 1)));
    }
}
