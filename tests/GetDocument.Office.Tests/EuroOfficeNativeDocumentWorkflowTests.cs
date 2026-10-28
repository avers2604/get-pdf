using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeDocumentWorkflowTests
{
    private static EuroOfficeNativeDocumentWorkflow NewWorkflow()
        => new("gd31j7-fixture", "gd31j7-session");

    [TestMethod]
    public void NativeBrowserLoadedDoesNotProveDocumentOpened()
    {
        var flow = NewWorkflow();
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Requested, flow.Status);
        Assert.IsFalse(flow.DocumentVerified);
        flow.Observe(new("view-loaded", 10, "gd31j7-session"));
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.ViewLoadedUnverified, flow.Status);
        Assert.IsFalse(flow.DocumentVerified);
        Assert.IsFalse(flow.SaveCompleted);
        Assert.IsFalse(flow.PdfReady);
    }

    [TestMethod]
    public void OnSaveAndPrintEndCannotAuthorizeSaveOrPdf()
    {
        var flow = NewWorkflow();
        flow.Observe(new("view-loaded", 10, "gd31j7-session"));
        flow.Observe(new("modified", 1012, "gd31j7-session"));
        flow.Observe(new("on-save", 3002, "gd31j7-session"));
        flow.Observe(new("print-end", 5013, "gd31j7-session"));
        Assert.IsTrue(flow.ModifiedNotificationObserved);
        Assert.IsTrue(flow.SaveNotificationObserved);
        Assert.IsTrue(flow.PrintNotificationObserved);
        Assert.IsFalse(flow.SaveCompleted);
        Assert.IsFalse(flow.PdfReady);
    }

    [TestMethod]
    public void ForeignSessionUnknownEventAndDuplicateCloseAreRejected()
    {
        var flow = NewWorkflow();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            flow.Observe(new("on-save", 3002, "other-session")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            flow.Observe(new("execute", 5015, "gd31j7-session")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            flow.Observe(new("on-save", 1012, "gd31j7-session")));
        flow.Observe(new("closed", 2001, "gd31j7-session"));
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Closed, flow.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            flow.Observe(new("view-loaded", 10, "gd31j7-session")));
    }

    [TestMethod]
    public void FailureIsTerminalAndCannotBeReinterpretedAsSuccessfulSave()
    {
        var flow = NewWorkflow();
        flow.Fail("native-host-crashed");
        Assert.AreEqual(EuroOfficeNativeDocumentStatus.Failed, flow.Status);
        Assert.IsFalse(flow.SaveCompleted);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            flow.Observe(new("on-save", 3002, "gd31j7-session")));
    }

    [TestMethod]
    public void RevisionAndPdfIntentRequireExplicitTrustedCopyAndStayPending()
    {
        using var fixture = new SyntheticFixture();
        var flow = NewWorkflow();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            flow.CreateRevisionIntent(fixture.Root, fixture.Source, fixture.Revision,
                editableWorkingCopy: false));
        string command = flow.CreateRevisionIntent(fixture.Root, fixture.Source, fixture.Revision,
            editableWorkingCopy: true);
        using var parsed = JsonDocument.Parse(command);
        Assert.AreEqual("save-as-new-revision", parsed.RootElement.GetProperty("command").GetString());
        Assert.IsFalse(flow.SaveCompleted);

        string pdfIntent = flow.CreatePdfIntent(fixture.Root, fixture.Source, fixture.Pdf);
        using var pdf = JsonDocument.Parse(pdfIntent);
        Assert.AreEqual("export-pdf", pdf.RootElement.GetProperty("command").GetString());
        Assert.IsFalse(flow.PdfReady);
        Assert.IsFalse(File.Exists(fixture.Pdf));
    }

    [TestMethod]
    public void RepeatedIntentCannotOverwriteEarlierDestination()
    {
        using var fixture = new SyntheticFixture();
        var flow = NewWorkflow();
        flow.CreateRevisionIntent(fixture.Root, fixture.Source, fixture.Revision,
            editableWorkingCopy: true);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            flow.CreateRevisionIntent(fixture.Root, fixture.Source, fixture.Revision,
                editableWorkingCopy: true));
    }

    [TestMethod]
    public void ExportingPdfCannotBypassGetBrandedPrintWizard()
    {
        using var fixture = new SyntheticFixture();
        var flow = NewWorkflow();
        flow.CreatePdfIntent(fixture.Root, fixture.Source, fixture.Pdf);
        Assert.IsFalse(flow.PdfReady);
        Assert.IsFalse(flow.PrintApproved);
        Assert.ThrowsExactly<NotSupportedException>(() => flow.AuthorizeDirectPrint());
    }

    private sealed class SyntheticFixture : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "gd31j7-fixture-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "test.docx");
        public string Revision => Path.Combine(Root, "revision.docx");
        public string Pdf => Path.Combine(Root, "get-print.pdf");
        public SyntheticFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Source, [0x50, 0x4b, 0x03, 0x04, 0x00]);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
