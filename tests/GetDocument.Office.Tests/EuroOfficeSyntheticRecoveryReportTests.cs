using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSyntheticRecoveryReportTests
{
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly string Digest = new('a', 64);

    [TestMethod]
    public void ManualReportIsReadOnlyAndNeverContainsPathsOrUntrustedNames()
    {
        using var lab = new Fixture();
        var intent = EuroOfficeSyntheticSnapshotJournal.Begin(
            lab.Root, Session, "docx", Digest, 42, Guid.NewGuid());
        File.WriteAllText(Path.Combine(lab.Reviewed, intent.PendingName), "private bytes SECRET_TEST_CONTENT");
        string rogue = ".gd31j29-pending-ATTACK_SECRET_FILENAME.tmp";
        File.WriteAllText(Path.Combine(lab.Reviewed, rogue), "private rogue bytes");
        var report = EuroOfficeSyntheticRecoveryReport.Build(lab.Root, 0, labApproved: true);
        string json = report.ToJson();
        using var document = JsonDocument.Parse(json);
        JsonElement value = document.RootElement;
        Assert.AreEqual("getdoc-synthetic-recovery-report/1", value.GetProperty("schema").GetString());
        Assert.AreEqual(2, value.GetProperty("totalFindings").GetInt32());
        Assert.IsTrue(value.GetProperty("readOnly").GetBoolean());
        Assert.IsTrue(value.GetProperty("manualReviewRequired").GetBoolean());
        Assert.IsFalse(value.GetProperty("cleanupAuthorized").GetBoolean());
        Assert.IsFalse(value.GetProperty("authorizedForPublication").GetBoolean());
        Assert.IsFalse(value.GetProperty("durabilityProven").GetBoolean());
        Assert.IsFalse(json.Contains(lab.Root, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(intent.PendingName, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(intent.FinalName, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(rogue, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("SECRET_TEST_CONTENT", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private rogue", StringComparison.Ordinal));
        Assert.AreEqual(2, value.GetProperty("findings").GetArrayLength());
        Assert.IsTrue(File.Exists(Path.Combine(lab.Reviewed, rogue)));
        Assert.IsTrue(File.Exists(Path.Combine(lab.Reviewed, intent.PendingName)));
    }

    [TestMethod]
    public void BoundedPaginationIncludesSummaryOfAllEvents()
    {
        using var lab = new Fixture();
        for (int i = 0; i < 37; i++)
        {
            File.WriteAllText(Path.Combine(lab.Reviewed,
                ".gd31j29-pending-" + Guid.NewGuid().ToString("N") + ".tmp"), "x");
        }
        var first = EuroOfficeSyntheticRecoveryReport.Build(lab.Root, 0, true);
        var second = EuroOfficeSyntheticRecoveryReport.Build(lab.Root, 1, true);
        Assert.AreEqual(37, first.TotalFindings);
        Assert.AreEqual(37, second.TotalFindings);
        Assert.AreEqual(2, first.PageCount);
        Assert.AreEqual(32, first.Findings.Count);
        Assert.AreEqual(5, second.Findings.Count);
        Assert.AreEqual(37, first.Counts.Single(x =>
            x.State == EuroOfficeSyntheticSnapshotJournal.Observation.UnknownPending).Count);
        Assert.IsTrue(first.ToJson().Length <= 16384);
        Assert.IsTrue(second.ToJson().Length <= 16384);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            EuroOfficeSyntheticRecoveryReport.Build(lab.Root, 2, true));
        Assert.AreEqual(37, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void EmptyPageIsStillUntrustedAndFailsClosed()
    {
        using var lab = new Fixture();
        var page = EuroOfficeSyntheticRecoveryReport.Build(lab.Root, 0, true);
        Assert.AreEqual(0, page.TotalFindings);
        Assert.AreEqual(1, page.PageCount);
        Assert.AreEqual(0, page.Findings.Count);
        Assert.IsFalse(page.CleanupAuthorized);
        Assert.IsFalse(page.AuthorizedForPublication);
        Assert.IsFalse(page.DurabilityProven);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            EuroOfficeSyntheticRecoveryReport.Build(lab.Root, -1, true));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeSyntheticRecoveryReport.Build(lab.Root, 0));
    }

    [TestMethod]
    public void CorruptJournalIsGenericObservationWithoutLeakingInputText()
    {
        using var lab = new Fixture();
        File.WriteAllText(Path.Combine(lab.Reviewed,
            ".gd31j30-intent-UNKNOWN_SECRET_JOURNAL.jsonl"), 
            "{\"secret\":\"CONFIDENTIAL_DOCUMENT_BODY\"}");
        var page = EuroOfficeSyntheticRecoveryReport.Build(lab.Root, 0, true);
        Assert.AreEqual(1, page.TotalFindings);
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.MalformedJournal,
            page.Findings[0].State);
        Assert.IsNull(page.Findings[0].AttemptId);
        string json = page.ToJson();
        Assert.IsFalse(json.Contains("UNKNOWN_SECRET_JOURNAL", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("CONFIDENTIAL_DOCUMENT_BODY", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RootLinkIsRejectedAndNoExportFileIsCreated()
    {
        using var lab = new Fixture();
        string reviewed = lab.Reviewed;
        Directory.Delete(reviewed);
        try
        {
            Directory.CreateSymbolicLink(reviewed, lab.Root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                   or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Создание символьных ссылок недоступно на тестовой платформе.");
            return;
        }
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticRecoveryReport.Build(lab.Root, 0, true));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "gd31j31-report-" + Guid.NewGuid().ToString("N"));
        public string Reviewed => Path.Combine(Root, "reviewed");
        public Fixture() => Directory.CreateDirectory(Reviewed);

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
