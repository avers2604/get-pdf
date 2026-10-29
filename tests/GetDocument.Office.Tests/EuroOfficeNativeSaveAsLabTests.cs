using System.IO.Compression;
using System.Security.Cryptography;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeSaveAsLabTests
{
    private static readonly Guid Session =
        Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab =
        Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";

    [TestMethod]
    public void RejectsCrossFormatBeforeCheckingAnyHost()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeNativeSaveAsLab.CreateStartInfo(
                null!, "/unused", Fixture, "xlsx", Session, Tab, labApproved: false));
    }

    [TestMethod]
    public void InspectsSeparateSameFormatCandidateButDoesNotPublish()
    {
        using var lab = new SyntheticLab();
        lab.Write(lab.Source, "Original synthetic data");
        lab.Write(lab.CandidatePath, "Modified synthetic data");
        var review = EuroOfficeNativeSaveAsLab.ReviewCandidate(
            lab.Path, Fixture, lab.Receipt(true));
        Assert.IsTrue(review.CandidateValidatedAsOoxml);
        Assert.AreEqual("docx", review.Candidate!.Format);
        Assert.IsFalse(review.SaveCompleted);
        Assert.IsFalse(review.DocumentVerified);
        Assert.IsFalse(review.AuthorizedForPublication);
        Assert.IsTrue(File.Exists(lab.Source));
    }

    [TestMethod]
    public void MismatchedOrCorruptedQuarantineMustNotPassReview()
    {
        using var lab = new SyntheticLab();
        lab.Write(lab.Source, "Original");
        lab.Write(lab.CandidatePath, "Edited");
        var expected = lab.Receipt(true);
        File.WriteAllBytes(lab.CandidatePath, [1, 2, 3]);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsLab.ReviewCandidate(lab.Path, Fixture, expected));
        lab.Write(lab.CandidatePath, "Edited again");
        File.AppendAllText(lab.Source, "altered source");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsLab.ReviewCandidate(lab.Path, Fixture, expected));
    }

    [TestMethod]
    public void MissingCandidateMeansUnverifiedNotSaved()
    {
        using var lab = new SyntheticLab();
        lab.Write(lab.Source, "Source only");
        var review = EuroOfficeNativeSaveAsLab.ReviewCandidate(
            lab.Path, Fixture, lab.Receipt(false));
        Assert.IsNull(review.Candidate);
        Assert.IsFalse(review.CandidateValidatedAsOoxml);
        Assert.IsFalse(review.SaveCompleted);
    }

    [TestMethod]
    public void RejectsFabricatedSuccessfulSaveAsWithoutTypedRequest()
    {
        using var lab = new SyntheticLab();
        lab.Write(lab.Source, "Original");
        lab.Write(lab.CandidatePath, "Edited");
        var old = lab.Receipt(true);
        var forged = old with { SaveAsRequestSeen = false };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeSaveAsLab.ReviewCandidate(lab.Path, Fixture, forged));
    }

    private sealed class SyntheticLab : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gd31j26-test-" + Guid.NewGuid().ToString("N"));
        public string Source => System.IO.Path.Combine(Path, Fixture);
        public string CandidatePath => System.IO.Path.Combine(Path,
            EuroOfficeNativeSaveAsReceipt.CandidateNameFor(Session, "docx"));

        public SyntheticLab() => Directory.CreateDirectory(Path);

        public void Write(string target, string value)
        {
            if (File.Exists(target)) File.Delete(target);
            using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
            using (var types = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                types.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var document = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                document.Write("<document><t>" + value + "</t></document>");
        }

        public EuroOfficeNativeSaveAsReceipt Receipt(bool candidate)
        {
            static string Sha(string path) =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            string old = Sha(Source);
            return new EuroOfficeNativeSaveAsReceipt(
                Session, Tab, Fixture, "docx", old, old,
                candidate, candidate, candidate, false, candidate,
                candidate ? System.IO.Path.GetFileName(CandidatePath) : "",
                candidate ? new FileInfo(CandidatePath).Length : 0,
                candidate ? Sha(CandidatePath) : "");
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
