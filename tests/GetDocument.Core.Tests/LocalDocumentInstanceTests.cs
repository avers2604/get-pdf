using GetDocument.Core.Instance;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class LocalDocumentInstanceTests
{
    [TestMethod]
    public void ValidatesOnlyLocalFilesWithKnownExtensions()
    {
        string path = Path.Combine(Path.GetTempPath(), "getdoc-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            CollectionAssert.AreEqual(new[] { Path.GetFullPath(path) },
                LocalDocumentInstance.ValidateFiles([path, path]));
            Assert.ThrowsExactly<ArgumentException>(() =>
                LocalDocumentInstance.ValidateFiles(["https://test.internal/file.docx"]));
            Assert.ThrowsExactly<ArgumentException>(() =>
                LocalDocumentInstance.ValidateFiles(["getdocument://open?token=x"]));
            Assert.ThrowsExactly<ArgumentException>(() =>
                LocalDocumentInstance.ValidateFiles(["--token=secret"]));
            Assert.ThrowsExactly<ArgumentException>(() =>
                LocalDocumentInstance.ValidateFiles([path + ".exe"]));
            Assert.ThrowsExactly<FileNotFoundException>(() =>
                LocalDocumentInstance.ValidateFiles([path + "-missing"]));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void RejectsOverlongLaunchRequests()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            LocalDocumentInstance.ValidateFiles(Enumerable.Repeat("a.docx", 17).ToArray()));
    }

    [TestMethod]
    public async Task SecondLaunchReusesFirstInstanceAndForwardsFile()
    {
        string endpoint = "getdoc-test-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), "getdoc-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46]);
        try
        {
            await using var primary = new LocalDocumentInstance(endpoint);
            Assert.IsTrue(primary.TryBecomePrimary());
            var delivery = new TaskCompletionSource<IReadOnlyList<string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Start(files => delivery.TrySetResult(files));

            await using var second = new LocalDocumentInstance(endpoint);
            Assert.IsFalse(second.TryBecomePrimary());
            Assert.IsTrue(await second.ForwardAsync([path]));
            var received = await delivery.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { Path.GetFullPath(path) }, received.ToArray());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task SecondLaunchWithoutFileActivatesExistingWindow()
    {
        string endpoint = "getdoc-test-" + Guid.NewGuid().ToString("N");
        await using var primary = new LocalDocumentInstance(endpoint);
        Assert.IsTrue(primary.TryBecomePrimary());
        var activated = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.Start(files => activated.TrySetResult(files));
        await using var second = new LocalDocumentInstance(endpoint);
        Assert.IsFalse(second.TryBecomePrimary());
        Assert.IsTrue(await second.ForwardAsync([]));
        Assert.AreEqual(0, (await activated.Task.WaitAsync(TimeSpan.FromSeconds(5))).Count);
    }
}
