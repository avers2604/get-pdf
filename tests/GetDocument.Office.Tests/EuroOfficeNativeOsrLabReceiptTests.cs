using System.Security.Cryptography;
using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeOsrLabReceiptTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/c8sAAAAASUVORK5CYII=");

    private static string Receipt(bool observed, bool frame, string? source = null,
        string? hash = null) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object>
        {
            ["documentVerified"] = false,
            ["frameAvailable"] = frame,
            ["laboratoryOnly"] = true,
            ["paintCallbackObserved"] = observed,
            ["payloadBase64"] = frame ? Convert.ToBase64String(Png) : "",
            ["pdfVerified"] = false,
            ["pixelHeight"] = frame ? 1 : 0,
            ["pixelWidth"] = frame ? 1 : 0,
            ["printApproved"] = false,
            ["saveVerified"] = false,
            ["schema"] = "getdoc-cef-osr-lab/1",
            ["sha256"] = frame ? hash ?? Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant() : "",
            ["source"] = source ?? "cef-osr-wayland-synthetic-only"
        });

    [TestMethod]
    public void ReceivedOnPaintIsNotAnApprovedOfficeDocument()
    {
        var result = EuroOfficeNativeOsrLabReceipt.Parse(Receipt(true, true));
        Assert.IsTrue(result.PaintCallbackObserved);
        Assert.IsTrue(result.FrameAvailable);
        CollectionAssert.AreEqual(Png, result.PreviewPng!);
        Assert.IsFalse(result.OfficeDocumentVerified);
        Assert.IsFalse(result.SaveCompleted);
        Assert.IsFalse(result.PdfReady);
        Assert.IsFalse(result.PrintApproved);
        Assert.IsFalse(result.ApprovedForGetTab);
    }

    [TestMethod]
    public void NoOnPaintIsNormalUnverifiedLabResult()
    {
        var result = EuroOfficeNativeOsrLabReceipt.Parse(Receipt(false, false));
        Assert.IsFalse(result.PaintCallbackObserved);
        Assert.IsFalse(result.FrameAvailable);
        Assert.IsNull(result.PreviewPng);
        Assert.IsFalse(result.ApprovedForGetTab);
    }

    [TestMethod]
    public void RejectsForgedOrImpossibleFrameEnvelope()
    {
        string valid = Receipt(true, true);
        foreach (string forged in new[]
        {
            valid.Replace("\"documentVerified\":false", "\"documentVerified\":true"),
            valid.Replace("\"pdfVerified\":false", "\"pdfVerified\":true"),
            valid.Replace("\"laboratoryOnly\":true", "\"laboratoryOnly\":false"),
            valid.Replace("\"paintCallbackObserved\":true", "\"paintCallbackObserved\":false"),
            valid.Replace("\"pixelWidth\":1", "\"pixelWidth\":5000"),
            valid.Replace("\"source\":", "\"source\":\"bad\",\"source\":"),
            valid.Replace("\"pixelHeight\":", "\"extra\":123,\"pixelHeight\":"),
            Receipt(true, true, "cef-osr-production"),
            Receipt(true, true, hash: new string('0', 64)),
            Receipt(false, true),
            new string('x', 70_000)
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeOsrLabReceipt.Parse(forged));
    }

    [TestMethod]
    public void PreviewCannotBeModifiedAfterValidation()
    {
        var receipt = EuroOfficeNativeOsrLabReceipt.Parse(Receipt(true, true));
        var bytes = receipt.PreviewPng!;
        bytes[0] = 0;
        CollectionAssert.AreEqual(Png, receipt.PreviewPng!);
    }
}
