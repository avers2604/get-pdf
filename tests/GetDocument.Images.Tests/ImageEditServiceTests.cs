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

    [TestMethod]
    public void RotatesAndCropsRealPngAndKeepsOriginalByteIdentical()
    {
        using var sandbox = TempDirectory.Create();
        string original = Path.Combine(sandbox.Path, "input.png");
        string output = Path.Combine(sandbox.Path, "revision.png");
        // Four by three RGB PNG generated specifically for this regression test.
        File.WriteAllBytes(original, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7ljmRAAAAGElEQVR4nGNkYJAzYmCAIBaGKAY4QOEAACQwAXJRPwcJAAAAAElFTkSuQmCC"));
        byte[] before = File.ReadAllBytes(original);
        string sha = ImageEditService.FingerprintSource(original);
        ImageEditService.SaveNewRevision(original, output,
            new ImageEditPlan(ClockwiseQuarterTurns: 1,
                Crop: new ImageCrop(1, 1, 2, 3)), expectedSha256: sha);

        CollectionAssert.AreEqual(before, File.ReadAllBytes(original));
        using var actual = NetVips.Image.NewFromFile(output);
        Assert.AreEqual(2, actual.Width);
        Assert.AreEqual(3, actual.Height);
    }

    [TestMethod]
    public void RefusesImageEditedAfterCapture()
    {
        using var sandbox = TempDirectory.Create();
        string original = Path.Combine(sandbox.Path, "input.png");
        string output = Path.Combine(sandbox.Path, "revision.png");
        File.WriteAllBytes(original, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7ljmRAAAAGElEQVR4nGNkYJAzYmCAIBaGKAY4QOEAACQwAXJRPwcJAAAAAElFTkSuQmCC"));
        string sha = ImageEditService.FingerprintSource(original);
        using (var stream = new FileStream(original, FileMode.Append, FileAccess.Write))
            stream.WriteByte(0); // Intentional change to hash.
        Assert.ThrowsExactly<IOException>(() =>
            ImageEditService.SaveNewRevision(original, output,
                new ImageEditPlan(ClockwiseQuarterTurns: 1), expectedSha256: sha));
        Assert.IsFalse(File.Exists(output));
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
