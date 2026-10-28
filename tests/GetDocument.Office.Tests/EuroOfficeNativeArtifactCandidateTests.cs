using System.IO.Compression;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeArtifactCandidateTests
{
    [TestMethod]
    public void NewlyStagedOoxmlMustBeStructurallySafeAndStillNotCommitted()
    {
        using var fixture = new Fixture();
        fixture.MakeWord(fixture.Revision);
        var receipt = EuroOfficeNativeArtifactCandidate.InspectRevision(
            fixture.Root, fixture.Source, fixture.Revision);
        Assert.AreEqual("docx", receipt.Format);
        Assert.AreEqual(64, receipt.Sha256.Length);
        Assert.IsTrue(receipt.Length > 0);
        Assert.IsFalse(receipt.AuthorizedForPublication);
        Assert.IsFalse(receipt.WormUpdated);
    }

    [TestMethod]
    public void TruncatedOrMacroEnabledRevisionMustBeRejected()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Revision, [0x50, 0x4B, 0x03]);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeArtifactCandidate.InspectRevision(
                fixture.Root, fixture.Source, fixture.Revision));
        File.Delete(fixture.Revision);
        using (var archive = new ZipArchive(File.Create(fixture.Revision), ZipArchiveMode.Create))
        {
            Add(archive, "[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add(archive, "word/document.xml", "<w:document xmlns:w=\"urn:test\"/>");
            Add(archive, "word/vbaProject.bin", "malicious");
        }
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeArtifactCandidate.InspectRevision(
                fixture.Root, fixture.Source, fixture.Revision));
    }

    [TestMethod]
    public void PdfCandidateMustBeACompletedLocalPdfNotAnEvent()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Pdf, "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n"u8.ToArray());
        var result = EuroOfficeNativeArtifactCandidate.InspectPdf(
            fixture.Root, fixture.Source, fixture.Pdf);
        Assert.AreEqual("pdf", result.Format);
        Assert.IsFalse(result.AuthorizedForPublication);
        Assert.IsFalse(result.WormUpdated);
        File.WriteAllText(fixture.Pdf, "not a pdf");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeArtifactCandidate.InspectPdf(
                fixture.Root, fixture.Source, fixture.Pdf));
    }

    [TestMethod]
    public void RejectsEscapesAndSameSourceForRevision()
    {
        using var fixture = new Fixture();
        fixture.MakeWord(fixture.Revision);
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeNativeArtifactCandidate.InspectRevision(
                fixture.Root, fixture.Source, fixture.Source));
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeNativeArtifactCandidate.InspectRevision(
                fixture.Root, fixture.Source, Path.Combine(fixture.Root, "..", "outside.docx")));
    }

    private static void Add(ZipArchive archive, string path, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(text);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "gd31j7-artifact-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source.docx");
        public string Revision => Path.Combine(Root, "revision.docx");
        public string Pdf => Path.Combine(Root, "preview.pdf");
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            MakeWord(Source);
        }
        public void MakeWord(string path)
        {
            using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
            Add(zip, "[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add(zip, "word/document.xml", "<w:document xmlns:w=\"urn:test\"/>");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
