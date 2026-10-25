using System.IO.Compression;
using System.Text;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class OfficePackageGuardTests
{
    private const string Types = "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>";

    private static byte[] MakeZip(params (string path, string content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(),
                    new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    internal static byte[] Valid(string ext, string suffix = "")
    {
        var main = ext switch
        {
            ".docx" => "word/document.xml",
            ".xlsx" => "xl/workbook.xml",
            ".pptx" => "ppt/presentation.xml",
            _ => throw new ArgumentException("Extension")
        };
        return MakeZip(("[Content_Types].xml", Types),
            (main, "<root xmlns=\"urn:get-document:test\">" + suffix + "</root>"));
    }

    [DataTestMethod]
    [DataRow(".docx")]
    [DataRow(".xlsx")]
    [DataRow(".pptx")]
    public void AcceptsBoundedValidOpenXmlPackage(string extension)
    {
        using var stream = new MemoryStream(Valid(extension));
        OfficePackageGuard.Validate(stream, "sample" + extension);
        Assert.AreEqual(0L, stream.Position);
    }

    [TestMethod]
    public void RejectsNonZipAndMisleadingExtension()
    {
        using var bad = new MemoryStream(Encoding.UTF8.GetBytes("not a zip"));
        Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
            OfficePackageGuard.Validate(bad, "document.docx"));
        using var wrong = new MemoryStream(Valid(".xlsx"));
        Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
            OfficePackageGuard.Validate(wrong, "document.docx"));
    }

    [TestMethod]
    public void RejectsZipSlipAndBackslashPaths()
    {
        foreach (var unsafeName in new[] { "../escape.xml", "word/../escape.xml", "/absolute.xml", "word\\escape.xml" })
        {
            using var stream = new MemoryStream(MakeZip(
                ("[Content_Types].xml", Types),
                ("word/document.xml", "<root/>"),
                (unsafeName, "<root/>")));
            Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
                OfficePackageGuard.Validate(stream, "doc.docx"), unsafeName);
        }
    }

    [TestMethod]
    public void RejectsDuplicateCaseInsensitivePartNames()
    {
        using var stream = new MemoryStream(MakeZip(
            ("[Content_Types].xml", Types),
            ("word/document.xml", "<root/>"),
            ("WORD/DOCUMENT.XML", "<root/>")));
        Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
            OfficePackageGuard.Validate(stream, "doc.docx"));
    }

    [TestMethod]
    public void RejectsCompressedZipBombByExpansionRatio()
    {
        using var stream = new MemoryStream(MakeZip(
            ("[Content_Types].xml", Types),
            ("word/document.xml", "<root>" + new string('A', 2_000_000) + "</root>")));
        Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
            OfficePackageGuard.Validate(stream, "doc.docx"));
    }

    [TestMethod]
    public void RejectsDtdInXmlParts()
    {
        using var stream = new MemoryStream(MakeZip(
            ("[Content_Types].xml", Types),
            ("word/document.xml", "<!DOCTYPE data [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><data>&x;</data>")));
        Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
            OfficePackageGuard.Validate(stream, "doc.docx"));
    }

    [TestMethod]
    public void RejectsDisguisedActiveCode()
    {
        foreach (var part in new[] { "word/vbaProject.bin", "word/activeX/activeX1.bin" })
        {
            using var stream = new MemoryStream(MakeZip(
                ("[Content_Types].xml", Types),
                ("word/document.xml", "<root/>"),
                (part, "BINARY")));
            Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
                OfficePackageGuard.Validate(stream, "document.docx"));
        }
    }

    [TestMethod]
    public void RejectsExcessPartsUnderSmallTestLimits()
    {
        byte[] archive = MakeZip(("[Content_Types].xml", Types),
            ("word/document.xml", "<root/>"),
            ("word/styles.xml", "<root/>"));
        using var input = new MemoryStream(archive);
        var limits = OfficePackageLimits.Default with { MaxEntries = 2 };
        Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
            OfficePackageGuard.Validate(input, "doc.docx", limits));
    }

    [TestMethod]
    public void RejectsTooLargeUncompressedPartUnderTestLimits()
    {
        byte[] archive = MakeZip(("[Content_Types].xml", Types),
            ("word/document.xml", "<root>" + new string('B', 2048) + "</root>"));
        using var input = new MemoryStream(archive);
        var limits = OfficePackageLimits.Default with { MaxEntryUncompressedBytes = 1024 };
        Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
            OfficePackageGuard.Validate(input, "doc.docx", limits));
    }

    [TestMethod]
    public void LeavesInputPositionUnchangedAfterRejection()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("invalid"));
        input.Position = 2;
        Assert.ThrowsExactly<RejectedOfficePackageException>(() =>
            OfficePackageGuard.Validate(input, "doc.docx"));
        Assert.AreEqual(2L, input.Position);
    }
}
