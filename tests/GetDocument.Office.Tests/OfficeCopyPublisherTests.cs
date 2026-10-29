using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class OfficeCopyPublisherTests
{
    [TestMethod]
    public async Task PublishesNewCopyFromStagingInTheDestinationDirectory()
    {
        using var fixture = new Fixture();
        string target = Path.Combine(fixture.Root, "new.docx");
        await OfficeCopyPublisher.PublishAsync(target,
            staged => File.WriteAllTextAsync(staged, "valid test output"));
        Assert.AreEqual("valid test output", File.ReadAllText(target));
        Assert.AreEqual(1, Directory.GetFiles(fixture.Root).Length);
    }

    [TestMethod]
    public async Task NeverOverwritesAnExistingFileEvenIfPickerConfirmedIt()
    {
        using var fixture = new Fixture();
        string target = Path.Combine(fixture.Root, "existing.docx");
        await File.WriteAllTextAsync(target, "COLLEAGUE CONTENT");
        await Assert.ThrowsExactlyAsync<IOException>(
            () => OfficeCopyPublisher.PublishAsync(target,
                staged => File.WriteAllTextAsync(staged, "MY NEW CONTENT")));
        Assert.AreEqual("COLLEAGUE CONTENT", await File.ReadAllTextAsync(target));
        Assert.AreEqual(1, Directory.GetFiles(fixture.Root).Length);
    }

    [TestMethod]
    public async Task InterruptedExportLeavesNoPartialDocumentVisible()
    {
        using var fixture = new Fixture();
        string target = Path.Combine(fixture.Root, "draft.xlsx");
        await Assert.ThrowsExactlyAsync<IOException>(
            () => OfficeCopyPublisher.PublishAsync(target, async staged =>
            {
                await File.WriteAllTextAsync(staged, "INCOMPLETE");
                throw new IOException("Simulated process termination");
            }));
        Assert.IsFalse(File.Exists(target));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root).Length);
    }

    [TestMethod]
    public async Task NativeExportMustActuallyProduceNonEmptyFile()
    {
        using var fixture = new Fixture();
        string target = Path.Combine(fixture.Root, "empty.pptx");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => OfficeCopyPublisher.PublishAsync(target, _ => Task.CompletedTask));
        Assert.IsFalse(File.Exists(target));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root).Length);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-copy-publish-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
