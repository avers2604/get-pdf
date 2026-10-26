using System.Text;
using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintFileSnapshotTests
{
    [TestMethod]
    public async Task SnapshotPrintBytesStayUnchangedAfterOriginalIsEdited()
    {
        string original = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            byte[] contents = Encoding.ASCII.GetBytes("%PDF-1.7\noriginal-print-revision");
            await File.WriteAllBytesAsync(original, contents);
            using var frozen = await PrintFileSnapshot.CreateAsync(original, CancellationToken.None);
            Assert.IsTrue(File.Exists(frozen.Path));
            Assert.AreNotEqual(Path.GetFullPath(original), frozen.Path);
            CollectionAssert.AreEqual(contents, await File.ReadAllBytesAsync(frozen.Path));
            await File.WriteAllTextAsync(original, "%PDF-1.7\nchanged");
            CollectionAssert.AreEqual(contents, await File.ReadAllBytesAsync(frozen.Path));
            Assert.IsTrue(await frozen.VerifyAsync(CancellationToken.None));
        }
        finally { File.Delete(original); }
    }

    [TestMethod]
    public async Task TamperedStagedPdfIsRejected()
    {
        string original = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllTextAsync(original, "%PDF-1.4\nalpha");
            using var frozen = await PrintFileSnapshot.CreateAsync(original, CancellationToken.None);
            Assert.AreEqual(64, frozen.Sha256Hex.Length);
            await File.WriteAllTextAsync(frozen.Path, "%PDF-1.4\nbeta");
            Assert.IsFalse(await frozen.VerifyAsync(CancellationToken.None));
        }
        finally { File.Delete(original); }
    }

    [TestMethod]
    public async Task DisposingSnapshotRemovesItsPrivateWorkingDirectory()
    {
        string original = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllTextAsync(original, "%PDF-1.4\n");
            var frozen = await PrintFileSnapshot.CreateAsync(original, CancellationToken.None);
            string folder = Path.GetDirectoryName(frozen.Path)!;
            Assert.IsTrue(Directory.Exists(folder));
            frozen.Dispose();
            Assert.IsFalse(File.Exists(frozen.Path));
            Assert.IsFalse(Directory.Exists(folder));
        }
        finally { File.Delete(original); }
    }

    [TestMethod]
    public async Task InvalidOrTooLargePdfCannotBeStaged()
    {
        string original = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllTextAsync(original, "not a PDF");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                PrintFileSnapshot.CreateAsync(original, CancellationToken.None));

            // Sparse file; test does not allocate a 128 MiB byte array.
            using (var file = new FileStream(original, FileMode.Open, FileAccess.Write))
                file.SetLength(PrintFileSnapshot.MaxBytes + 1);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                PrintFileSnapshot.CreateAsync(original, CancellationToken.None));
        }
        finally { File.Delete(original); }
    }
}
