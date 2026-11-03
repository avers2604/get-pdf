using System.Security.Cryptography;
using System.Text;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bl: synthetic capture-admission tests. Never deletes an orphan,
/// never authenticates an OS inode, and never touches real user documents.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryCaptureAdmissionTests
{
    [TestMethod]
    public void EmptyAndBelowBudgetInventoryPermitAttempt()
    {
        RecoveryCaptureAdmission.EnsureMayCapture(
            new OfficeRecoveryIncompleteInventory(Array.Empty<OfficeRecoveryIncompleteCandidate>(),
                Truncated: false));
        RecoveryCaptureAdmission.EnsureMayCapture(
            MakeInventory(RecoveryCaptureAdmission.MaxIncompleteCandidates - 1));
    }

    [TestMethod]
    public void UntrustedOrphanCountAtLimitDeniesNewCaptureWithoutDeleting()
    {
        var inventory = MakeInventory(RecoveryCaptureAdmission.MaxIncompleteCandidates);
        Assert.ThrowsExactly<IOException>(() =>
            RecoveryCaptureAdmission.EnsureMayCapture(inventory));
        Assert.AreEqual(RecoveryCaptureAdmission.MaxIncompleteCandidates,
            inventory.Candidates.Count);
    }

    [TestMethod]
    public void PartialDirectorySurveyFailsClosedRegardlessOfObservedCount()
    {
        var inventory = new OfficeRecoveryIncompleteInventory(
            Array.Empty<OfficeRecoveryIncompleteCandidate>(), Truncated: true);
        Assert.ThrowsExactly<IOException>(() =>
            RecoveryCaptureAdmission.EnsureMayCapture(inventory));
    }

    [TestMethod]
    public async Task CaptureAtOrphanBudgetRejectsBeforeCallingExporterAndPreservesBytes()
    {
        using var scratch = new Scratch();
        var store = new OfficeRecoveryStore(scratch.RecoveryRoot);
        string folder = scratch.CreateRecoveryFolder();
        const string sample = "DO_NOT_DELETE_OR_MODIFY";
        for (int i = 0; i < RecoveryCaptureAdmission.MaxIncompleteCandidates; i++)
            File.WriteAllText(Path.Combine(folder,
                Guid.NewGuid().ToString("N") + ".part.docx"), sample);

        bool calledExporter = false;
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            store.CaptureAsync(scratch.Source, _ =>
            {
                calledExporter = true;
                return Task.CompletedTask;
            }));

        Assert.IsFalse(calledExporter);
        Assert.AreEqual(RecoveryCaptureAdmission.MaxIncompleteCandidates,
            Directory.GetFiles(folder).Length);
        Assert.IsTrue(Directory.GetFiles(folder).All(p => File.ReadAllText(p) == sample));
        Assert.AreEqual("SOURCE_PRESERVED", File.ReadAllText(scratch.Source));
    }

    private static OfficeRecoveryIncompleteInventory MakeInventory(int count)
    {
        var candidates = Enumerable.Range(0, count).Select(_ =>
            new OfficeRecoveryIncompleteCandidate(
                Guid.NewGuid().ToString("N") + ".docx",
                OfficeRecoveryIncompleteKind.FinalCopyWithoutObservedManifest,
                OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile,
                null, null)).ToArray();
        return new OfficeRecoveryIncompleteInventory(candidates, Truncated: false);
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bl-budget-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source.docx");
        public string RecoveryRoot => Path.Combine(Root, "recovery");

        public Scratch()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Source, "SOURCE_PRESERVED");
        }

        public string CreateRecoveryFolder()
        {
            string normalized = OperatingSystem.IsWindows()
                ? Path.GetFullPath(Source).ToUpperInvariant() : Path.GetFullPath(Source);
            string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            string folder = Path.Combine(RecoveryRoot, hash);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
