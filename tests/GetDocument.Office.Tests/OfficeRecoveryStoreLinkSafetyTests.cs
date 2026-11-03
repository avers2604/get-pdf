using System.IO.Compression;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27an: synthetic OOXML only. No real office engine, document share,
/// Windows service, native SaveAs, or GUP-2; mandatory symlink cases are not skipped.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryStoreLinkSafetyTests
{
    [TestMethod]
    public async Task CaptureRejectsExporterSymlinkToOriginalAndKeepsOriginalBytes()
    {
        using var fixture = new RecoveryScratch();
        byte[] original = await File.ReadAllBytesAsync(fixture.Source);
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.CaptureAsync(fixture.Source, temporary =>
            {
                File.CreateSymbolicLink(temporary, fixture.Source);
                return Task.CompletedTask;
            }));

        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.Source));
        Assert.IsNull(store.FindLatest(fixture.Source));
        Assert.AreEqual(0, Directory.GetFiles(fixture.RecoveryRoot, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task PrecreatedTemporaryManifestSymlinkNeverOverwritesUnrelatedFile()
    {
        using var fixture = new RecoveryScratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        string untouched = Path.Combine(fixture.Root, "untouched.txt");
        await File.WriteAllTextAsync(untouched, "KEEP_THIS_CONTENT");

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            store.CaptureAsync(fixture.Source, temporary =>
            {
                File.Copy(fixture.Source, temporary);
                string name = Path.GetFileName(temporary).Split('.')[0];
                string manifest = Path.Combine(Path.GetDirectoryName(temporary)!,
                    name + ".json.tmp");
                File.CreateSymbolicLink(manifest, untouched);
                return Task.CompletedTask;
            }));

        Assert.AreEqual("KEEP_THIS_CONTENT", await File.ReadAllTextAsync(untouched));
        Assert.IsNull(store.FindLatest(fixture.Source));
        Assert.IsTrue(File.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task LatestRecoveryCopyMustNotBeSymlinkedToValidOriginal()
    {
        using var fixture = new RecoveryScratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        OfficeRecoveryCopy copy = await store.CaptureAsync(fixture.Source, temporary =>
        {
            File.Copy(fixture.Source, temporary);
            return Task.CompletedTask;
        });
        File.Delete(copy.FilePath);
        File.CreateSymbolicLink(copy.FilePath, fixture.Source);

        // The original and link have identical SHA-256; content-hashing alone
        // cannot distinguish a genuine independent snapshot from a link.
        Assert.IsNull(store.FindLatest(fixture.Source));
        Assert.IsTrue(File.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task LatestRecoveryMustRejectSymlinkedManifestEvenIfJsonMatches()
    {
        using var fixture = new RecoveryScratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        await store.CaptureAsync(fixture.Source, temporary =>
        {
            File.Copy(fixture.Source, temporary);
            return Task.CompletedTask;
        });
        string manifest = Directory.GetFiles(fixture.RecoveryRoot, "*.json",
            SearchOption.AllDirectories).Single();
        string outside = Path.Combine(fixture.Root, "outside-manifest.json");
        File.Copy(manifest, outside);
        File.Delete(manifest);
        File.CreateSymbolicLink(manifest, outside);

        Assert.IsNull(store.FindLatest(fixture.Source));
        Assert.IsTrue(File.Exists(outside));
    }

    [TestMethod]
    public async Task SymlinkedRecoveryRootRejectsCaptureBeforeCallingExporter()
    {
        using var fixture = new RecoveryScratch();
        Directory.CreateDirectory(fixture.RecoveryRoot);
        string linkedRoot = Path.Combine(fixture.Root, "recovery-alias");
        Directory.CreateSymbolicLink(linkedRoot, fixture.RecoveryRoot);
        var store = new OfficeRecoveryStore(linkedRoot);
        bool exported = false;

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.CaptureAsync(fixture.Source, temporary =>
            {
                exported = true;
                File.Copy(fixture.Source, temporary);
                return Task.CompletedTask;
            }));

        Assert.IsFalse(exported);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.RecoveryRoot).Length);
        Assert.IsTrue(File.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task SymlinkedRecoveryRootDoesNotDeleteExistingValidSnapshots()
    {
        using var fixture = new RecoveryScratch();
        var trusted = new OfficeRecoveryStore(fixture.RecoveryRoot);
        OfficeRecoveryCopy copy = await trusted.CaptureAsync(fixture.Source, temporary =>
        {
            File.Copy(fixture.Source, temporary);
            return Task.CompletedTask;
        });
        string linkedRoot = Path.Combine(fixture.Root, "recovery-alias");
        Directory.CreateSymbolicLink(linkedRoot, fixture.RecoveryRoot);

        var untrusted = new OfficeRecoveryStore(linkedRoot);
        Assert.ThrowsExactly<InvalidDataException>(() => untrusted.Discard(fixture.Source));
        Assert.ThrowsExactly<InvalidDataException>(() => untrusted.FindLatest(fixture.Source));
        Assert.IsTrue(File.Exists(copy.FilePath));
        Assert.AreEqual(copy.FilePath, trusted.FindLatest(fixture.Source)?.FilePath);
    }

    private sealed class RecoveryScratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27an-recovery-test-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "synthetic.docx");
        public string RecoveryRoot => Path.Combine(Root, "recovery");

        public RecoveryScratch()
        {
            Directory.CreateDirectory(Root);
            using var output = File.Create(Source);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            using (var manifest = new StreamWriter(
                zip.CreateEntry("[Content_Types].xml").Open()))
                manifest.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var part = new StreamWriter(
                zip.CreateEntry("word/document.xml").Open()))
                part.Write("<document xmlns=\"urn:getdoc:synthetic\"/>");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
