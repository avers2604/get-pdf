using System.IO.Compression;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class OfficeRecoveryStoreTests
{
    [TestMethod]
    public async Task CaptureKeepsOriginalAndMakesValidatedRecoveryCopy()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Приказ.docx");
        byte[] original = File.ReadAllBytes(source);
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);

        OfficeRecoveryCopy copy = await store.CaptureAsync(source, target =>
        {
            File.Copy(source, target);
            return Task.CompletedTask;
        });

        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        Assert.IsTrue(File.Exists(copy.FilePath));
        Assert.AreNotEqual(Path.GetFullPath(source), copy.FilePath);
        Assert.AreEqual(copy.FilePath, store.FindLatest(source)?.FilePath);
        Assert.IsTrue(copy.FilePath.StartsWith(Path.GetFullPath(fixture.RecoveryDirectory), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectsCorruptRecoveryCopyAndDoesNotOfferItToUser()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Расчёт.xlsx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        OfficeRecoveryCopy copy = await store.CaptureAsync(source, target =>
        {
            File.Copy(source, target);
            return Task.CompletedTask;
        });
        File.AppendAllText(copy.FilePath, "tampered");

        Assert.IsNull(store.FindLatest(source));
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task ExportFailureDoesNotExposeIncompleteCopy()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Доклад.pptx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);

        await Assert.ThrowsExactlyAsync<IOException>(() => store.CaptureAsync(source, target =>
        {
            File.WriteAllText(target, "partial");
            throw new IOException("disk full");
        }));

        Assert.IsNull(store.FindLatest(source));
        CollectionAssert.AreEqual(RecoveryFixture.SampleBytes(source), File.ReadAllBytes(source));
    }

    [TestMethod]
    public async Task RejectsNonOfficeZipAndUnsupportedFormats()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Лист.xlsx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.CaptureAsync(source, target =>
        {
            File.WriteAllText(target, "not OOXML");
            return Task.CompletedTask;
        }));
        Assert.IsNull(store.FindLatest(source));
        string unsupported = Path.Combine(fixture.Root, "macro.xlsm");
        File.WriteAllText(unsupported, "macros");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            store.CaptureAsync(unsupported, _ => Task.CompletedTask));
    }

    [TestMethod]
    public async Task DiscardAfterConfirmedSaveRemovesRecoveryButNotSource()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("План.docx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        await store.CaptureAsync(source, target =>
        {
            File.Copy(source, target);
            return Task.CompletedTask;
        });

        store.Discard(source);

        Assert.IsNull(store.FindLatest(source));
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task SeparateFilesNeverShareRecoverySnapshots()
    {
        using var fixture = new RecoveryFixture();
        string first = fixture.CreateOfficeFile("А.docx");
        string second = fixture.CreateOfficeFile("Б.docx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        await store.CaptureAsync(first, target => { File.Copy(first, target); return Task.CompletedTask; });

        Assert.IsNull(store.FindLatest(second));
        Assert.IsNotNull(store.FindLatest(first));
    }

    private sealed class RecoveryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "getdoc-recovery-test-" + Guid.NewGuid().ToString("N"));
        public string RecoveryDirectory => Path.Combine(Root, "recovery");

        public string CreateOfficeFile(string filename)
        {
            Directory.CreateDirectory(Root);
            string target = Path.Combine(Root, filename);
            using var output = File.Create(target);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            using var part = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open());
            part.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            return target;
        }

        public static byte[] SampleBytes(string source) => File.ReadAllBytes(source);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
