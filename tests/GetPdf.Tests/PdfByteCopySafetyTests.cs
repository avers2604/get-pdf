using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfByteCopySafetyTests
{
    private static readonly byte[] SourceBytes =
        "%PDF-1.7\n% signed PDF test fixture (exact bytes only)\n/Sig /ByteRange [0 2 4 6]\n%%EOF\n"u8.ToArray();

    [TestMethod]
    public void RawCopyPreservesEveryByteAndLeavesOriginalUntouched()
    {
        string folder = CreateFolder();
        try
        {
            string original = Path.Combine(folder, "original.pdf");
            string exported = Path.Combine(folder, "exported.pdf");
            File.WriteAllBytes(original, SourceBytes);

            PdfByteCopy.Copy(original, exported);

            CollectionAssert.AreEqual(SourceBytes, File.ReadAllBytes(original));
            CollectionAssert.AreEqual(SourceBytes, File.ReadAllBytes(exported));
            CollectionAssert.AreEquivalent(
                new[] { "original.pdf", "exported.pdf" },
                Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void SamePathOrExistingDestinationCannotOverwriteWormOriginal()
    {
        string folder = CreateFolder();
        try
        {
            string source = Path.Combine(folder, "original.pdf");
            string occupied = Path.Combine(folder, "occupied.pdf");
            byte[] other = "preserve existing file"u8.ToArray();
            File.WriteAllBytes(source, SourceBytes);
            File.WriteAllBytes(occupied, other);

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                PdfByteCopy.Copy(source, source));
            Assert.ThrowsExactly<IOException>(() =>
                PdfByteCopy.Copy(source, occupied));

            CollectionAssert.AreEqual(SourceBytes, File.ReadAllBytes(source));
            CollectionAssert.AreEqual(other, File.ReadAllBytes(occupied));
            Assert.AreEqual(2, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void CancelledCopyCannotPublishPartialDocument()
    {
        string folder = CreateFolder();
        try
        {
            string source = Path.Combine(folder, "original.pdf");
            string result = Path.Combine(folder, "copy.pdf");
            File.WriteAllBytes(source, SourceBytes);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(() =>
                PdfByteCopy.Copy(source, result, cancel.Token));

            CollectionAssert.AreEqual(SourceBytes, File.ReadAllBytes(source));
            Assert.IsFalse(File.Exists(result));
            Assert.AreEqual(1, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string CreateFolder()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "getpdf-worm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
