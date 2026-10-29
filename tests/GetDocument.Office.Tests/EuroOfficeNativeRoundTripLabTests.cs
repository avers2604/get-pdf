using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeRoundTripLabTests
{
    private const string Fixture = "gd31j6_writer.docx";
    private static readonly Guid Session = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");

    [TestMethod]
    public void RejectsUnapprovedNativeExecutionBeforeReadingAnyHostBinary()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeNativeRoundTripLab.CreateStartInfo(
                null!, "/offline-only", Fixture, Session, Guid.NewGuid(), labApproved: false));
    }

    [TestMethod]
    public void ModifiedNativeQuarantineCandidateIsValidatedButNeverPublished()
    {
        using var test = new FixtureRoot();
        test.WriteOoxml(test.Source, "Original synthetic text");
        test.WriteOoxml(test.Candidate, "Native changed synthetic text");
        var receipt = test.CreateReceipt(written: true);
        var review = EuroOfficeNativeRoundTripLab.ReviewCandidate(
            test.Root, Fixture, receipt);
        Assert.IsTrue(review.CandidateValidatedAsOoxml);
        Assert.AreEqual("docx", review.Candidate!.Format);
        Assert.IsFalse(review.SaveCompleted);
        Assert.IsFalse(review.AuthorizedForPublication);
        Assert.AreEqual("Original synthetic text", test.ReadDocument(test.Source));
    }

    [TestMethod]
    public void RefusesMismatchedDigestsOrMalformedOoxmlAndPreservesOriginal()
    {
        using var test = new FixtureRoot();
        test.WriteOoxml(test.Source, "Immutable fixture");
        test.WriteOoxml(test.Candidate, "Edited");
        var good = test.CreateReceipt(written: true);
        File.WriteAllBytes(test.Candidate, [1, 2, 3, 4, 5, 6]);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeRoundTripLab.ReviewCandidate(test.Root, Fixture, good));
        test.WriteOoxml(test.Candidate, "Changed again");
        File.AppendAllText(test.Source, "unauthorized fixture mutation");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeRoundTripLab.ReviewCandidate(test.Root, Fixture, good));
    }

    [TestMethod]
    public void MissingNativeSavedCandidateIsNotTreatedAsSaveSuccess()
    {
        using var test = new FixtureRoot();
        test.WriteOoxml(test.Source, "Only original");
        var receipt = test.CreateReceipt(written: false);
        var review = EuroOfficeNativeRoundTripLab.ReviewCandidate(test.Root, Fixture, receipt);
        Assert.IsFalse(review.CandidateValidatedAsOoxml);
        Assert.IsNull(review.Candidate);
    }

    private sealed class FixtureRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "gd31j25-unit-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, Fixture);
        public string Candidate => Path.Combine(
            Root, EuroOfficeNativeRoundTripReceipt.ExpectedCandidateName(Session, Fixture));

        public FixtureRoot() => Directory.CreateDirectory(Root);

        public void WriteOoxml(string path, string text)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var types = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                types.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var doc = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                doc.Write("<document xmlns=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><t>" + text + "</t></document>");
        }

        public string ReadDocument(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            string body = reader.ReadToEnd();
            int start = body.IndexOf("<t>", StringComparison.Ordinal);
            int end = body.IndexOf("</t>", StringComparison.Ordinal);
            return body[(start + 3)..end];
        }

        public EuroOfficeNativeRoundTripReceipt CreateReceipt(bool written)
        {
            static string Hash(string name) =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(name))).ToLowerInvariant();
            string original = Hash(Source);
            string changed = written ? Hash(Candidate) : string.Empty;
            return new EuroOfficeNativeRoundTripReceipt(
                original, original, written, written, false,
                written, written ? Path.GetFileName(Candidate) : string.Empty,
                written ? new FileInfo(Candidate).Length : 0,
                changed);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
