using GetDocument.Images;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Images.Tests;

[TestClass]
public sealed class ImageEditServiceTests
{
    [TestMethod]
    public void ExistingOutputCanNeverBeOverwritten()
    {
        using var sandbox = TempDirectory.Create();
        string original = Path.Combine(sandbox.Path, "image.png");
        string target = Path.Combine(sandbox.Path, "original.png");
        File.WriteAllBytes(original, [137, 80, 78, 71, 13, 10, 26, 10]);
        File.WriteAllText(target, "WORM");
        Assert.ThrowsExactly<IOException>(() =>
            ImageEditService.SaveNewRevision(original, target, new ImageEditPlan(1)));
        Assert.AreEqual("WORM", File.ReadAllText(target));
    }

    [TestMethod]
    public void OriginalCannotBeSavedOntoItself()
    {
        using var sandbox = TempDirectory.Create();
        string original = Path.Combine(sandbox.Path, "original.png");
        File.WriteAllBytes(original, [137, 80, 78, 71, 13, 10, 26, 10]);
        Assert.ThrowsExactly<IOException>(() =>
            ImageEditService.SaveNewRevision(original, original, new ImageEditPlan(1)));
    }

    [TestMethod]
    public void RejectsInvalidFileTypesBeforeNativeDecoderStarts()
    {
        using var sandbox = TempDirectory.Create();
        string original = Path.Combine(sandbox.Path, "input.svg");
        string target = Path.Combine(sandbox.Path, "output.png");
        File.WriteAllText(original, "<svg/>");
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ImageEditService.SaveNewRevision(original, target, new ImageEditPlan(1)));
        Assert.IsFalse(File.Exists(target));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        private TempDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }
        public static TempDirectory Create() => new(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gd07-" + Guid.NewGuid().ToString("N")));
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
