using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeHandoffTests
{
    private static EuroOfficeNativeArtifactCandidate Doc() =>
        new("docx", 1024, new string('a', 64));
    private static EuroOfficeNativeArtifactCandidate Pdf() =>
        new("pdf", 2048, new string('b', 64));

    [TestMethod]
    public void ProposedRevisionIsNeverAnAuthorizedGup2Version()
    {
        var gateway = new EuroOfficeNativeHandoff();
        var proposal = gateway.PrepareGup2Revision("doc-47", "v3", Doc());
        Assert.AreEqual("doc-47", proposal.DocumentId);
        Assert.AreEqual("v3", proposal.ExpectedBaseVersion);
        Assert.AreEqual(new string('a', 64), proposal.CandidateSha256);
        Assert.IsFalse(proposal.Committed);
        Assert.IsFalse(proposal.WormOriginalModified);
        Assert.AreEqual("blocked-no-trusted-server", proposal.State);
    }

    [TestMethod]
    public async Task SaveAndPrintRequireRealTrustedBackendsAndNeverClaimSuccess()
    {
        var gateway = new EuroOfficeNativeHandoff();
        var version = gateway.PrepareGup2Revision("doc-47", "v3", Doc());
        var print = gateway.PrepareGetPrintPdf("doc-47", Pdf());
        Assert.IsFalse(print.PrintApproved);
        Assert.AreEqual("blocked-no-get-print-adapter", print.State);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            gateway.CommitRevisionAsync(version, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            gateway.OpenInGetPrintWizardAsync(print, CancellationToken.None));
    }

    [TestMethod]
    public void InvalidCandidateOrMissingBaseVersionCannotFormAProposal()
    {
        var gateway = new EuroOfficeNativeHandoff();
        Assert.ThrowsExactly<ArgumentException>(() =>
            gateway.PrepareGup2Revision("doc-47", "", Doc()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            gateway.PrepareGup2Revision("doc-47", "v3", Pdf()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            gateway.PrepareGetPrintPdf("doc-47", Doc()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            gateway.PrepareGup2Revision("doc-47", "v3", new(
                "docx", 10, "bad-hash")));
    }

    [TestMethod]
    public void NoExternalNetworkOrFileOperationsAreRequiredToPrepareTheIntent()
    {
        var gateway = new EuroOfficeNativeHandoff();
        var proposal = gateway.PrepareGup2Revision("doc-local", "version-in-get", Doc());
        Assert.AreEqual(1024, proposal.CandidateLength);
        Assert.IsFalse(proposal.Committed);
        Assert.IsFalse(proposal.WormOriginalModified);
    }
}
