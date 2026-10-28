using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeReceiptTests
{
    private static string Receipt(bool changed = false, bool save = false,
        bool cancelled = false, bool printed = false) =>
        JsonSerializer.Serialize(new
        {
            schema = "GD31J7_EVENTS_ONLY",
            nativeEvents = new
            {
                viewLoaded = true,
                modifiedNotification = changed,
                onSaveNotification = save,
                onSaveCancelled = cancelled,
                printEndNotification = printed
            },
            documentVerified = false,
            saveVerified = false,
            pdfVerified = false,
            printApproved = false,
            laboratoryOnly = true
        });

    [TestMethod]
    public void NativeReceiptNeverConvertsNotificationsIntoVerifiedOutput()
    {
        var result = EuroOfficeNativeReceipt.Parse(Receipt(
            changed: true, save: true, printed: true));
        Assert.IsTrue(result.SaveNotificationObserved);
        Assert.IsTrue(result.PrintNotificationObserved);
        Assert.IsTrue(result.ModifiedNotificationObserved);
        Assert.IsFalse(result.DocumentVerified);
        Assert.IsFalse(result.SaveCompleted);
        Assert.IsFalse(result.PdfReady);
        Assert.IsFalse(result.PrintApproved);
    }

    [TestMethod]
    public void RejectsFalseClaimsAboutSavedFilesAndDocumentRendering()
    {
        string valid = Receipt();
        foreach (string tampered in new[]
        {
            valid.Replace("\"saveVerified\":false", "\"saveVerified\":true"),
            valid.Replace("\"pdfVerified\":false", "\"pdfVerified\":true"),
            valid.Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            valid.Replace("\"printApproved\":false", "\"printApproved\":true"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false")
        })
            Assert.ThrowsExactly<InvalidDataException>(() => EuroOfficeNativeReceipt.Parse(tampered));
    }

    [TestMethod]
    public void RejectsDuplicateUnknownAndMalformedReceiptFields()
    {
        string valid = Receipt();
        foreach (string tampered in new[]
        {
            valid.Replace("\"schema\":", "\"schema\":\"extra\",\"schema\":"),
            valid.Replace("\"onSaveNotification\":", "\"unknown\":true,\"onSaveNotification\":"),
            valid.Replace("\"viewLoaded\":true", "\"viewLoaded\":\"true\""),
            valid.Replace("\"nativeEvents\":", "\"nativeEvents\":{} ,\"nativeEvents\":"),
            "{}",
            "{",
            new string('X', 16 * 1024)
        })
            Assert.ThrowsExactly<InvalidDataException>(() => EuroOfficeNativeReceipt.Parse(tampered));
    }

    [TestMethod]
    public void CancelledSaveCannotBeReportedAsCompleted()
    {
        var result = EuroOfficeNativeReceipt.Parse(Receipt(save: true, cancelled: true));
        Assert.IsTrue(result.SaveCancelled);
        Assert.IsFalse(result.SaveCompleted);
    }
}
