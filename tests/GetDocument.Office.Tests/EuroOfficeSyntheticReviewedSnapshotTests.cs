using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeSyntheticReviewedSnapshotTests
{
    private static readonly Guid Session =
        Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f010");
    private static readonly Guid Tab =
        Guid.Parse("f010e0d0-c0b0-a090-8070-605040302010");
    private const string Fixture = "gd31j6_writer.docx";

    [TestMethod]
    public void CommitCreatesNewReviewedSnapshotWithoutMutatingSourceOrQuarantine()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        string original = lab.Hash(lab.Source);
        string quarantine = lab.Hash(lab.Candidate);
        var saved = EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, labApproved: true);
        Assert.IsTrue(saved.SyntheticOnly);
        Assert.IsFalse(saved.AuthorizedForPublication);
        Assert.IsFalse(saved.WormUpdated);
        Assert.AreEqual(EuroOfficeSyntheticReviewedSnapshot.ExpectedName(Session, "docx"), saved.FileName);
        Assert.AreEqual(quarantine, saved.Sha256);
        Assert.AreEqual(quarantine, lab.Hash(Path.Combine(lab.Reviewed, saved.FileName)));
        Assert.AreEqual(original, lab.Hash(lab.Source));
        Assert.AreEqual(quarantine, lab.Hash(lab.Candidate));
        Assert.IsTrue(Directory.GetFiles(lab.Reviewed, "*.tmp").Length == 0);
        var journal = EuroOfficeSyntheticSnapshotJournal.Inspect(lab.Root, labApproved: true);
        Assert.AreEqual(1, journal.Count);
        Assert.AreEqual(EuroOfficeSyntheticSnapshotJournal.Observation.CompleteObserved,
            journal[0].State);
        Assert.AreEqual("completed", journal[0].LastStage);
        Assert.IsFalse(journal[0].TrustedForDeletion);
    }

    [TestMethod]
    public void SecondCommitCannotOverwriteExistingReviewedFile()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        _ = EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true);
        string destination = Path.Combine(lab.Reviewed,
            EuroOfficeSyntheticReviewedSnapshot.ExpectedName(Session, "docx"));
        string before = lab.Hash(destination);
        Assert.ThrowsExactly<IOException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(before, lab.Hash(destination));
    }

    [TestMethod]
    public void RefusesApprovalByDefaultOrWithoutAuthenticCorrelatedNativeEvidence()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, false));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root,
                review with { NativeEvidence = null }, true));
        var broken = review.NativeEvidence! with { FailureCode = "timeout" };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root,
                review with { NativeEvidence = broken }, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void SourceMutationOrCandidateMutationFailsBeforeReviewedSnapshot()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        File.AppendAllText(lab.Candidate, "damaged");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        lab.Create();
        review = lab.GetReview();
        File.AppendAllText(lab.Source, "different");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void MissingOrUnapprovedManifestPreventsAnyOutput()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        File.Delete(lab.Manifest);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
        Assert.AreEqual(0, Directory.GetFiles(lab.Reviewed).Length);
    }

    [TestMethod]
    public void RejectsLinkedReviewedFolder()
    {
        using var lab = new SyntheticLab();
        lab.Create();
        var review = lab.GetReview();
        string linked = lab.Reviewed;
        Directory.Delete(linked);
        try
        {
            Directory.CreateSymbolicLink(linked, lab.Root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                   or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Символические ссылки запрещены платформой стенда.");
            return;
        }
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeSyntheticReviewedSnapshot.Commit(lab.Root, review, true));
    }

    internal sealed class SyntheticLab : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "gd31j35-synth-" + Guid.NewGuid().ToString("N"));
        public string Reviewed => Path.Combine(Root, "reviewed");
        public string Format { get; }
        public string FixtureName => Format switch
        {
            "docx" => "gd31j6_writer.docx",
            "xlsx" => "gd31j6_calc.xlsx",
            "pptx" => "gd31j6_impress.pptx",
            _ => throw new ArgumentException("Unknown synthetic fixture")
        };
        public string Source => Path.Combine(Root, FixtureName);
        public string Candidate => Path.Combine(Root,
            EuroOfficeNativeSaveAsReceipt.CandidateNameFor(Session, Format));
        public string Manifest => Path.Combine(Root, "gd31j6-fixture-manifest.json");

        public SyntheticLab(string format = "docx")
        {
            if (format is not ("docx" or "xlsx" or "pptx"))
                throw new ArgumentException("Only three fixed synthetic formats.");
            Format = format;
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Reviewed);
        }

        public string Hash(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        public void Create()
        {
            // Every manifest member is a physically present, independently
            // hashed synthetic OPC package. Never fabricate sibling records.
            string[] names =
            [
                "gd31j6_writer.docx", "gd31j6_calc.xlsx", "gd31j6_impress.pptx"
            ];
            var records = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                string path = Path.Combine(Root, name);
                WriteOoxml(path, "Synthetic original " + name);
                records.Add(name, new { size = new FileInfo(path).Length, sha256 = Hash(path) });
            }
            WriteOoxml(Candidate, "Synthetic edited " + Format);
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new
            {
                schema = "gd31j6-office-fixtures/1",
                purpose = "test-only-local-ooxml-no-corporate-documents",
                files = records
            }));
        }

        public EuroOfficeNativeSaveAsLab.Review GetReview()
        {
            string oldHash = Hash(Source);
            string newHash = Hash(Candidate);
            var core = new EuroOfficeNativeSaveAsReceipt(
                Session, Tab, FixtureName, Format, oldHash, oldHash,
                true, true, true, false, true,
                Path.GetFileName(Candidate), new FileInfo(Candidate).Length, newHash);
            var native = new EuroOfficeNativeSaveAsEventReceipt(core,
                "candidate_untrusted", "quarantined", 1, 1, 1, 2);
            return EuroOfficeNativeSaveAsLab.ReviewCandidate(
                Root, FixtureName, core, native);
        }

        private static void WriteOoxml(string path, string value)
        {
            if (File.Exists(path)) File.Delete(path);
            string format = Path.GetExtension(path).TrimStart('.');
            const string relationshipType =
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
            const string relNs =
                "http://schemas.openxmlformats.org/package/2006/relationships";
            string mainPart = format switch
            {
                "docx" => "word/document.xml",
                "xlsx" => "xl/workbook.xml",
                "pptx" => "ppt/presentation.xml",
                _ => throw new ArgumentException("Unexpected OPC test type")
            };
            string mainType = format switch
            {
                "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                _ => throw new ArgumentException("Unexpected OPC test type")
            };
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            string typeOverrides = "<Override PartName=\"/" + mainPart +
                "\" ContentType=\"" + mainType + "\"/>";
            if (format == "xlsx")
                typeOverrides += "<Override PartName=\"/xl/worksheets/sheet1.xml\" " +
                    "ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>";
            if (format == "pptx")
                typeOverrides += "<Override PartName=\"/ppt/slides/slide1.xml\" " +
                    "ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>";
            WriteXml(zip, "[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                typeOverrides + "</Types>");
            WriteXml(zip, "_rels/.rels",
                "<Relationships xmlns=\"" + relNs + "\">" +
                "<Relationship Id=\"rId1\" Type=\"" + relationshipType +
                "\" Target=\"" + mainPart + "\"/></Relationships>");
            string escaped = System.Security.SecurityElement.Escape(value) ?? "";
            switch (format)
            {
                case "docx":
                    WriteXml(zip, mainPart,
                        "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                        "<w:body><w:p><w:r><w:t>" + escaped +
                        "</w:t></w:r></w:p></w:body></w:document>");
                    break;
                case "xlsx":
                    WriteXml(zip, mainPart,
                        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                        "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                    WriteXml(zip, "xl/_rels/workbook.xml.rels",
                        "<Relationships xmlns=\"" + relNs + "\"><Relationship Id=\"rId1\" " +
                        "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
                        "Target=\"worksheets/sheet1.xml\"/></Relationships>");
                    WriteXml(zip, "xl/worksheets/sheet1.xml",
                        "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                        "<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>" +
                        escaped + "</t></is></c></row></sheetData></worksheet>");
                    break;
                case "pptx":
                    WriteXml(zip, mainPart,
                        "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
                        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                        "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst>" +
                        "<p:sldSz cx=\"9144000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
                        "</p:presentation>");
                    WriteXml(zip, "ppt/_rels/presentation.xml.rels",
                        "<Relationships xmlns=\"" + relNs + "\"><Relationship Id=\"rId1\" " +
                        "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" " +
                        "Target=\"slides/slide1.xml\"/></Relationships>");
                    WriteXml(zip, "ppt/slides/slide1.xml",
                        "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
                        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                        "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/>" +
                        "<p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                        "<p:grpSpPr/><p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Text\"/>" +
                        "<p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody>" +
                        "<a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>" + escaped +
                        "</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>");
                    break;
            }
        }

        private static void WriteXml(ZipArchive zip, string partName, string xml)
        {
            using var writer = new StreamWriter(
                zip.CreateEntry(partName).Open(), new System.Text.UTF8Encoding(false));
            writer.Write(xml);
        }

        public void Dispose()
        {
            // Only this disposable test-created directory. No production code
            // ever deletes original, candidate, reviewed or journal files.
            if (Directory.Exists(Reviewed) &&
                (File.GetAttributes(Reviewed) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(Reviewed);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
