using System.Security.Cryptography;
using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeDiagnosticFrameTests
{
    // Tiny fixed PNG fixture, not a screenshot or office document.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/c8sAAAAASUVORK5CYII=");

    private static readonly Guid Tab = Guid.Parse("b70b9c8b-7341-4d99-ae3b-2db31d9c25c7");
    private static readonly Guid Session = Guid.Parse("25fe7878-23da-4d76-b28d-2fd5ffb187e8");

    public static string Reply(byte[]? png = null, string? source = null, int width = 1,
        int height = 1, int sequence = 2, string? hash = null) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object>
        {
            ["command"] = "frame",
            ["encoding"] = "png-base64",
            ["height"] = height,
            ["payloadBase64"] = Convert.ToBase64String(png ?? Png),
            ["protocol"] = EuroOfficeNativeTabIpc.Protocol,
            ["sequence"] = sequence,
            ["sessionId"] = Session.ToString("N"),
            ["sha256"] = hash ?? Convert.ToHexString(SHA256.HashData(png ?? Png)).ToLowerInvariant(),
            ["source"] = source ?? "qt-painter-test-only",
            ["state"] = "preview",
            ["tabId"] = Tab.ToString("N"),
            ["width"] = width
        });

    [TestMethod]
    public void ValidFrameIsStillOnlyQtDiagnosticNotOfficeRendering()
    {
        var frame = EuroOfficeNativeDiagnosticFrame.Parse(Reply(), Session, Tab, 2);
        Assert.AreEqual(1, frame.Width);
        Assert.AreEqual(1, frame.Height);
        Assert.AreEqual(Session, frame.SessionId);
        Assert.AreEqual(Tab, frame.TabId);
        CollectionAssert.AreEqual(Png, frame.PngBytes);
        Assert.IsFalse(frame.CefRendered);
        Assert.IsFalse(frame.OfficeDocumentVerified);
        Assert.IsFalse(frame.RevisionCommitted);
        Assert.IsFalse(frame.PrintApproved);
    }

    [TestMethod]
    public void RejectsForgedCefSourceAndChangedFrameBytes()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeDiagnosticFrame.Parse(Reply(source: "cef-osr-rendered"),
                Session, Tab, 2));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeDiagnosticFrame.Parse(Reply(hash: new string('0', 64)),
                Session, Tab, 2));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeDiagnosticFrame.Parse(Reply(width: 8192),
                Session, Tab, 2));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeDiagnosticFrame.Parse(Reply(height: 2),
                Session, Tab, 2));
    }

    [TestMethod]
    public void RejectsUnknownDuplicateFieldsWrongSessionAndReplay()
    {
        string valid = Reply();
        foreach (var payload in new[]
        {
            valid.Replace("\"state\":", "\"state\":\"preview\",\"state\":"),
            valid.Replace("\"width\":1", "\"unknown\":true,\"width\":1"),
            valid.Replace(Session.ToString("N"), Guid.NewGuid().ToString("N")),
            valid.Replace(Tab.ToString("N"), Guid.NewGuid().ToString("N")),
            Reply(sequence: 1),
            "{",
            new string('x', 65537)
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeNativeDiagnosticFrame.Parse(payload, Session, Tab, 2));
    }

    [TestMethod]
    public void RejectsInvalidSignatureAndInflatedPng()
    {
        byte[] corrupt = (byte[])Png.Clone();
        corrupt[0] = 0;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeDiagnosticFrame.Parse(Reply(png: corrupt), Session, Tab, 2));
        byte[] oversized = new byte[49 * 1024];
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeDiagnosticFrame.Parse(Reply(png: oversized), Session, Tab, 2));
    }
}
