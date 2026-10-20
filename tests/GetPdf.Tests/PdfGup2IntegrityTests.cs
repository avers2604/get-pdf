using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfGup2IntegrityTests
{
    [TestMethod]
    public async Task MatchesOriginalWormSha256WithoutReadingOrModifyingSource()
    {
        string dir = Path.Combine(Path.GetTempPath(), "getpdf-gup2-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "original.pdf");
            byte[] contents = "%PDF-1.4\nGUP2-safe-test"u8.ToArray();
            File.WriteAllBytes(path, contents);
            string originalHash = Convert.ToHexString(SHA256.HashData(contents));
            Assert.IsTrue(await Gup2PdfIntegrity.VerifyAsync(
                path, originalHash.ToLowerInvariant(), 1024));
            Assert.IsFalse(await Gup2PdfIntegrity.VerifyAsync(
                path, new string('A', 64), 1024));
            CollectionAssert.AreEqual(contents, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public async Task RefusesMalformedDigestOrOversizedFilesBeforeComparing()
    {
        string path = Path.Combine(Path.GetTempPath(), "getpdf-not-existing.pdf");
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await Gup2PdfIntegrity.VerifyAsync(path, "deadbeef", 1024));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await Gup2PdfIntegrity.VerifyAsync(path, new string('A', 64), 0));
    }

    [TestMethod]
    public async Task CancelledHashNeverReadsPdf()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await Gup2PdfIntegrity.VerifyAsync(
                "nonexistent.pdf", new string('A', 64), 1024, cancellation.Token));
    }
}
