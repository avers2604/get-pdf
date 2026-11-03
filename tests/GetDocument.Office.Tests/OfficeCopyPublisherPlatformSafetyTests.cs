using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27aj: shared Windows/Linux new-copy Save As safety checks.
/// These are local synthetic tests. They do not invoke native EuroOffice,
/// authorize WORM writes or prove Windows/Linux release readiness.
/// </summary>
[TestClass]
public sealed class OfficeCopyPublisherPlatformSafetyTests
{
    [TestMethod]
    public async Task CancellationBeforeExportDoesNotInvokeEngineOrCreateCopy()
    {
        using var fixture = new Scratch();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        bool called = false;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            OfficeCopyPublisher.PublishAsync(
                Path.Combine(fixture.Root, "cancelled.docx"),
                _ => { called = true; return Task.CompletedTask; }, cancel.Token));
        Assert.IsFalse(called);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    [TestMethod]
    public async Task CancellationAfterExportNeverPublishesPartialCopy()
    {
        using var fixture = new Scratch();
        using var cancel = new CancellationTokenSource();
        string target = Path.Combine(fixture.Root, "cancelled.xlsx");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            OfficeCopyPublisher.PublishAsync(target, async staged =>
            {
                await File.WriteAllTextAsync(staged, "STAGED ONLY");
                cancel.Cancel();
            }, cancel.Token));
        Assert.IsFalse(File.Exists(target));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    [TestMethod]
    public async Task ConcurrentDestinationCreationNeverOverwritesAnotherWritersCopy()
    {
        using var fixture = new Scratch();
        string target = Path.Combine(fixture.Root, "same.docx");
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            OfficeCopyPublisher.PublishAsync(target, async staged =>
            {
                await File.WriteAllTextAsync(staged, "OUR COPY");
                await File.WriteAllTextAsync(target, "COLLEAGUE COPY");
            }));
        Assert.AreEqual("COLLEAGUE COPY", await File.ReadAllTextAsync(target));
        Assert.AreEqual(1, Directory.GetFiles(fixture.Root).Length);
    }

    [TestMethod]
    public async Task NativeExportCannotPublishSymlinkToAnotherFile()
    {
        using var fixture = new Scratch();
        string protectedFile = Path.Combine(fixture.Root, "keep.txt");
        await File.WriteAllTextAsync(protectedFile, "ORIGINAL");
        string target = Path.Combine(fixture.Root, "copy.docx");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            OfficeCopyPublisher.PublishAsync(target, staged =>
            {
                File.CreateSymbolicLink(staged, protectedFile);
                return Task.CompletedTask;
            }));
        Assert.IsFalse(File.Exists(target));
        Assert.AreEqual("ORIGINAL", await File.ReadAllTextAsync(protectedFile));
    }

    [TestMethod]
    public async Task SymlinkedDestinationFolderIsRejectedBeforeNativeExport()
    {
        using var fixture = new Scratch();
        string actual = Path.Combine(fixture.Root, "actual");
        string alias = Path.Combine(fixture.Root, "alias");
        Directory.CreateDirectory(actual);
        Directory.CreateSymbolicLink(alias, actual);
        bool called = false;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            OfficeCopyPublisher.PublishAsync(Path.Combine(alias, "new.pptx"),
                _ => { called = true; return Task.CompletedTask; }));
        Assert.IsFalse(called);
        Assert.IsFalse(File.Exists(Path.Combine(actual, "new.pptx")));
    }

    [DataTestMethod]
    [DataRow("report.docx:alternate")]
    [DataRow("CON.docx")]
    [DataRow("NUL.xlsx")]
    [DataRow("com1.pptx")]
    [DataRow("LPT9.docx")]
    [DataRow("COM¹.docx")]
    [DataRow("com².xlsx")]
    [DataRow("COM³.pptx")]
    [DataRow("LPT¹.docx")]
    [DataRow("lpt².xlsx")]
    [DataRow("LPT³.pptx")]
    [DataRow("trailing.xlsx.")]
    [DataRow("trailing.pptx ")]
    public void PortableFileNamesRejectWindowsDeviceNamesAndAds(string unsafeName)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeCopyPublisher.ValidatePortableDestinationFileName(unsafeName));
    }

    [DataTestMethod]
    [DataRow("Отчёт 2026.docx")]
    [DataRow("Разработка.xlsx")]
    [DataRow("ГЭТ Презентация.pptx")]
    public void PortableFileNamesAcceptOrdinaryOfficeCopies(string safeName)
    {
        OfficeCopyPublisher.ValidatePortableDestinationFileName(safeName);
    }

    [TestMethod]
    public async Task WindowsDeviceAliasesCannotInvokeNativeExporterOnEitherPlatform()
    {
        using var fixture = new Scratch();
        foreach (string name in new[]
        {
            "COM¹.docx", "COM².xlsx", "COM³.pptx",
            "LPT¹.docx", "LPT².xlsx", "LPT³.pptx"
        })
        {
            bool called = false;
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => OfficeCopyPublisher.PublishAsync(
                    Path.Combine(fixture.Root, name), _ =>
                    {
                        called = true;
                        return Task.CompletedTask;
                    }));
            Assert.IsFalse(called, name);
        }
        Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-saveas-platform-test-" + Guid.NewGuid().ToString("N"));

        public Scratch() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
