using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27aw: deterministic fault-injection tests for pair deletion policy.
/// The fake deletion callback touches no real files or user documents.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryDeletionOrderTests
{
    [TestMethod]
    public void CopyDeletionDeniedKeepsManifestAndNeverCallsManifestDelete()
    {
        var attempted = new List<string>();
        bool removed = OfficeRecoveryStore.DeleteVerifiedPair(
            "copy.docx", "copy.json", path =>
            {
                attempted.Add(path);
                return false;
            });

        Assert.IsFalse(removed);
        CollectionAssert.AreEqual(new[] { "copy.docx" }, attempted.ToArray());
    }

    [TestMethod]
    public void ManifestDeletionDeniedOnlyAfterSuccessfulCopyDeletion()
    {
        var attempted = new List<string>();
        bool removed = OfficeRecoveryStore.DeleteVerifiedPair(
            "copy.xlsx", "copy.json", path =>
            {
                attempted.Add(path);
                return path == "copy.xlsx";
            });

        Assert.IsFalse(removed);
        CollectionAssert.AreEqual(new[] { "copy.xlsx", "copy.json" }, attempted.ToArray());
    }

    [TestMethod]
    public void BothDeletionsSucceedInCopyFirstOrder()
    {
        var attempted = new List<string>();
        bool removed = OfficeRecoveryStore.DeleteVerifiedPair(
            "copy.pptx", "copy.json", path =>
            {
                attempted.Add(path);
                return true;
            });

        Assert.IsTrue(removed);
        CollectionAssert.AreEqual(new[] { "copy.pptx", "copy.json" }, attempted.ToArray());
    }

    [TestMethod]
    public void FaultingCopyDeletionDoesNotAttemptManifestDeletion()
    {
        var attempted = new List<string>();
        Assert.ThrowsExactly<IOException>(() =>
            OfficeRecoveryStore.DeleteVerifiedPair(
                "copy.docx", "copy.json", path =>
                {
                    attempted.Add(path);
                    throw new IOException("Synthetic sharing violation");
                }));
        CollectionAssert.AreEqual(new[] { "copy.docx" }, attempted.ToArray());
    }
}
