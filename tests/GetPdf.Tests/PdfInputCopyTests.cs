using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfInputCopyTests
{
    [TestMethod]
    public void ChangedOrTruncatedSnapshotMustNotBeAccepted()
    {
        PdfInputCopy.EnsureCompleteLength(7, 7);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfInputCopy.EnsureCompleteLength(7, 6));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfInputCopy.EnsureCompleteLength(7, 8));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfInputCopy.EnsureCompleteLength(0, 0));
    }

    [TestMethod]
    public async Task SuccessfulCopyKeepsOriginalBytesAndRejectsExistingTarget()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-input-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.pdf");
            string target = Path.Combine(folder, "target.pdf");
            byte[] content = "%PDF-1.7\nBody\n%%EOF\n"u8.ToArray();
            File.WriteAllBytes(source, content);
            Assert.AreEqual(target, await PdfInputCopy.CopySafelyAsync(source, target, 1024));
            CollectionAssert.AreEqual(content, File.ReadAllBytes(source));
            CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await PdfInputCopy.CopySafelyAsync(source, target, 1024));
            CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public async Task OversizeAndCancelledImportsNeverCreateScratchFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-input-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "original.pdf");
            string target = Path.Combine(folder, "staged.pdf");
            File.WriteAllBytes(source, "%PDF-1.4\n"u8.ToArray());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await PdfInputCopy.CopySafelyAsync(source, target, 4));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await PdfInputCopy.CopySafelyAsync(source, target, 100, cancellation.Token));
            Assert.IsFalse(File.Exists(target));
            Assert.AreEqual(1, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
