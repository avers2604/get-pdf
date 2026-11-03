using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bl: structural-only native deletion planning tests.
/// No actual delete, Windows FILE_DISPOSITION_INFO_EX or Linux unlinkat.
/// </summary>
[TestClass]
public sealed class RecoveryNativeDeletionPreparationTests
{
    [TestMethod]
    public void DeleteKeyAllowsOnlyDocumentRecoveryBasenames()
    {
        string hash = new string('A', 64);
        string id = Guid.NewGuid().ToString("N");
        foreach (string suffix in new[]
                 { ".docx", ".xlsx", ".pptx", ".part.docx",
                   ".part.xlsx", ".part.pptx", ".json", ".json.tmp" })
        {
            var key = RecoveryNativeDeleteKey.Create(hash, id + suffix);
            Assert.AreEqual(id + suffix, key.ArtifactName);
        }
        foreach (string invalid in new[]
                 { "../" + id + ".docx", id + ".exe",
                   "copy.json", id + ".json.bak", id + ".DOCX" })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryNativeDeleteKey.Create(hash, invalid));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryNativeDeleteKey.Create("wrong-hash", id + ".docx"));
    }

    [TestMethod]
    public void RealPlatformAdaptersRefuseAllDeletePreparations()
    {
        var key = NewKey();
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new WindowsRecoveryNativeDeleteObservationAdapter(), key));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new LinuxRecoveryNativeDeleteObservationAdapter(), key));
    }

    [TestMethod]
    public void SameSizeOrHashCannotSubstituteForNativeFileIdentity()
    {
        var invalid = GoodEvidence() with
        {
            FileBeforeDelete = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 5, 999, 1001)
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new FakeAdapter(invalid), NewKey()));
    }

    [TestMethod]
    public void ChangedDirectoryIdentityOrWeakIsolationFailsClosed()
    {
        var changedDirectory = GoodEvidence() with
        {
            ParentBeforeDelete = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 5, 99, 42)
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new FakeAdapter(changedDirectory), NewKey()));
        var noFollowMissing = GoodEvidence() with { NoFollowEnforced = false };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new FakeAdapter(noFollowMissing), NewKey()));
        var unlockedParent = GoodEvidence() with
        {
            ParentWriteIsolationConfirmed = false
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new FakeAdapter(unlockedParent), NewKey()));
    }

    [TestMethod]
    public void PlatformMismatchAndIncompleteIdentifiersAreRejected()
    {
        var wrongPlatform = GoodEvidence() with { Platform = RecoveryNativePlatform.Linux };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new FakeAdapter(wrongPlatform), NewKey()));
        var emptyId = GoodEvidence() with
        {
            FileAtOpen = new RecoveryNativeFileId(
                RecoveryNativePlatform.Windows, 0, 0, 0)
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new FakeAdapter(emptyId), NewKey()));
    }

    [TestMethod]
    public void ConsistentFakeEvidenceCreatesAuditObservationButNeverDeletes()
    {
        RecoveryNativeDeleteKey key = NewKey();
        RecoveryNativeDeleteEvidence evidence =
            RecoveryNativeDeletionPreparationGate.InspectForPlanning(
                new FakeAdapter(GoodEvidence()), key);

        Assert.AreEqual(GoodEvidence(), evidence);
        Assert.IsTrue(evidence.FileHandlePinned);
        Assert.IsTrue(evidence.ParentWriteIsolationConfirmed);
        // Positive synthetic evidence is NOT a deletion capability or file path.
        Assert.IsTrue(key.ArtifactName.EndsWith(".json.tmp", StringComparison.Ordinal));
    }

    private static RecoveryNativeDeleteKey NewKey() =>
        RecoveryNativeDeleteKey.Create(new string('A', 64),
            Guid.NewGuid().ToString("N") + ".json.tmp");

    private static RecoveryNativeDeleteEvidence GoodEvidence()
    {
        var parent = new RecoveryNativeFileId(
            RecoveryNativePlatform.Windows, 5, 10, 11);
        var file = new RecoveryNativeFileId(
            RecoveryNativePlatform.Windows, 5, 20, 21);
        return new RecoveryNativeDeleteEvidence(
            RecoveryNativePlatform.Windows, parent, parent, file, file,
            OpenedRelativeToTrustedRoot: true, NoFollowEnforced: true,
            ParentWriteIsolationConfirmed: true, FileHandlePinned: true);
    }

    private sealed class FakeAdapter : IRecoveryNativeDeleteObservationAdapter
    {
        private readonly RecoveryNativeDeleteEvidence _evidence;
        public FakeAdapter(RecoveryNativeDeleteEvidence evidence) => _evidence = evidence;
        public RecoveryNativePlatform Platform => RecoveryNativePlatform.Windows;
        public bool SupportsHandleBoundDeleteObservation => true;
        public RecoveryNativeDeleteEvidence ReadHeldHandleEvidence(
            RecoveryNativeDeleteKey key) => _evidence;
    }
}
