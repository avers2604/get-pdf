using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSyntheticSnapshotJournalTests
{
    private static readonly Guid Session =
        Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private const string ExpectedHash =
        "a215c4f081ded9a7fd314573b38a18a97973c1e2f9dd42da8c4e3894ab678afe";

    [TestMethod]
    public void IntentWithPendingIsObservationOnlyAndNeverAutoCleanup()
    {
        using var lab = new JournalLab();
        var attempt = EuroOfficeSyntheticSnapshotJournal.Begin(
            lab.Root, Session, "docx", ExpectedHash, 42, Guid.NewGuid());
        string pending = Path.Combine(lab.Reviewed, attempt.PendingName);
        File.WriteAllText(pending, "unverified partial");
        var found = EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true);
        Assert.AreEqual(1, found.Count);
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.PendingNeedsReview, found[0].State);
        Assert.AreEqual("intent", found[0].LastStage);
        Assert.IsFalse(found[0].TrustedForDeletion);
        Assert.IsTrue(File.Exists(pending));
        Assert.IsFalse(found[0].AuthorizedForPublication);
    }

    [TestMethod]
    public void CompleteJournalIsOnlyObservedAndLeavesOriginalUntouched()
    {
        using var lab = new JournalLab();
        var attempt = EuroOfficeSyntheticSnapshotJournal.Begin(
            lab.Root, Session, "docx", ExpectedHash, 42, Guid.NewGuid());
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "pending_flushed");
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "linked");
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "completed");
        string final = Path.Combine(lab.Reviewed, attempt.FinalName);
        File.WriteAllText(final, "still untrusted");
        var observed = EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true).Single();
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.CompleteObserved,
            observed.State);
        Assert.IsFalse(observed.TrustedForDeletion);
        Assert.IsFalse(observed.AuthorizedForPublication);
        Assert.IsTrue(File.Exists(final));
    }

    [TestMethod]
    public void PostLinkCrashAndUnknownPendingNeverTriggerDeletion()
    {
        using var lab = new JournalLab();
        var attempt = EuroOfficeSyntheticSnapshotJournal.Begin(
            lab.Root, Session, "docx", ExpectedHash, 42, Guid.NewGuid());
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "pending_flushed");
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "linked");
        File.WriteAllText(Path.Combine(lab.Reviewed, attempt.PendingName), "partial");
        File.WriteAllText(Path.Combine(lab.Reviewed, attempt.FinalName), "copy");
        string otherName = ".gd31j29-pending-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(Path.Combine(lab.Reviewed, otherName), "unidentified");
        var found = EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true);
        Assert.IsTrue(found.Any(x => x.State ==
            EuroOfficeSyntheticSnapshotJournal.Observation.PendingAfterLink));
        Assert.IsTrue(found.Any(x => x.State ==
            EuroOfficeSyntheticSnapshotJournal.Observation.UnknownPending));
        Assert.IsTrue(File.Exists(Path.Combine(lab.Reviewed, otherName)));
    }

    [TestMethod]
    public void MissingOrMalformedJournalIsFlaggedWithoutDeletingIt()
    {
        using var lab = new JournalLab();
        var attempt = EuroOfficeSyntheticSnapshotJournal.Begin(
            lab.Root, Session, "docx", ExpectedHash, 42, Guid.NewGuid());
        File.AppendAllText(attempt.JournalPath, "\n{\"fake\":true}\n");
        var observed = EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true).Single();
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.MalformedJournal,
            observed.State);
        Assert.IsTrue(File.Exists(attempt.JournalPath));
    }

    [TestMethod]
    public void MalformedTypesInJournalAreReportedWithoutAbortingInspection()
    {
        using var lab = new JournalLab();
        var attempt = EuroOfficeSyntheticSnapshotJournal.Begin(
            lab.Root, Session, "docx", ExpectedHash, 42, Guid.NewGuid());
        // An externally corrupted journal is a suspicious observation, not
        // a basis for deletion or a trusted completed record.
        File.WriteAllText(attempt.JournalPath, "{\"schema\":123}\n");
        var findings = EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true);
        Assert.AreEqual(1, findings.Count);
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.MalformedJournal,
            findings[0].State);
        Assert.IsTrue(File.Exists(attempt.JournalPath));
    }

    [TestMethod]
    public void JournalMustFollowSingleForwardOnlyStateMachine()
    {
        using var lab = new JournalLab();
        var attempt = EuroOfficeSyntheticSnapshotJournal.Begin(
            lab.Root, Session, "xlsx", ExpectedHash, 42, Guid.NewGuid());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticSnapshotJournal.Append(attempt, "completed"));
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "pending_flushed");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticSnapshotJournal.Append(attempt, "pending_flushed"));
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "linked");
        EuroOfficeSyntheticSnapshotJournal.Append(attempt, "completed");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticSnapshotJournal.Append(attempt, "linked"));
    }

    [TestMethod]
    public void InspectionIsExplicitlyDisabledAndRequiresNonLinkedLocalFolder()
    {
        using var lab = new JournalLab();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root));
        string reviewed = lab.Reviewed;
        Directory.Delete(reviewed);
        try { Directory.CreateSymbolicLink(reviewed, lab.Root); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                   or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Платформа не разрешает создавать символьную ссылку.");
            return;
        }
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true));
    }

    [TestMethod]
    public void InspectRefusesUnexpectedOrUnboundedDirectory()
    {
        using var lab = new JournalLab();
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeSyntheticSnapshotJournal.Inspect("relative", true));
        for (int i = 0; i <= 256; i++)
            File.WriteAllText(Path.Combine(lab.Reviewed,
                ".gd31j29-pending-" + Guid.NewGuid().ToString("N") + ".tmp"), "x");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, true));
    }

    private sealed class JournalLab : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "gd31j30-journal-" + Guid.NewGuid().ToString("N"));
        public string Reviewed => Path.Combine(Root, "reviewed");
        public JournalLab() => Directory.CreateDirectory(Reviewed);
        public void Dispose()
        {
            if (Directory.Exists(Reviewed) &&
                (File.GetAttributes(Reviewed) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(Reviewed);
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
