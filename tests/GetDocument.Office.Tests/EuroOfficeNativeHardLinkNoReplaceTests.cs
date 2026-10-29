using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeHardLinkNoReplaceTests
{
    [TestMethod]
    public void CreatesOnlySameDirectoryHardlinkAndRefusesToReplaceExistingDestination()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "pending.tmp");
        string dest = Path.Combine(fixture.Root, "reviewed.docx");
        File.WriteAllText(source, "first synthetic version");
        EuroOfficeNativeHardLinkNoReplace.Create(dest, source);
        Assert.AreEqual(File.ReadAllText(source), File.ReadAllText(dest));
        File.WriteAllText(source, "second synthetic version");
        Assert.AreEqual("second synthetic version", File.ReadAllText(dest));
        Assert.ThrowsExactly<IOException>(() =>
            EuroOfficeNativeHardLinkNoReplace.Create(dest, source));
        Assert.AreEqual("second synthetic version", File.ReadAllText(dest));
    }

    [TestMethod]
    public void MissingOrSamePathAndWrongDirectoryAreRejectedBeforeNativeCall()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "pending.tmp");
        File.WriteAllText(source, "safe fixture");
        Assert.ThrowsExactly<ArgumentException>(() =>
            EuroOfficeNativeHardLinkNoReplace.Create(source, source));
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            EuroOfficeNativeHardLinkNoReplace.Create(
                Path.Combine(fixture.Root, "other.docx"),
                Path.Combine(fixture.Root, "missing.tmp")));
        string nested = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(nested);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeHardLinkNoReplace.Create(
                Path.Combine(nested, "other.docx"), source));
        Assert.IsFalse(File.Exists(Path.Combine(nested, "other.docx")));
    }

    [TestMethod]
    public void ExistingFileAndDirectoryCannotBeReplaced()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "pending.tmp");
        string dest = Path.Combine(fixture.Root, "reviewed.docx");
        File.WriteAllText(source, "new");
        File.WriteAllText(dest, "old");
        Assert.ThrowsExactly<IOException>(() =>
            EuroOfficeNativeHardLinkNoReplace.Create(dest, source));
        Assert.AreEqual("old", File.ReadAllText(dest));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "directory.docx"));
        Assert.ThrowsExactly<IOException>(() =>
            EuroOfficeNativeHardLinkNoReplace.Create(
                Path.Combine(fixture.Root, "directory.docx"), source));
    }

    [TestMethod]
    public void SourceSymlinkIsRejectedInsteadOfCreatingLinkToLink()
    {
        using var fixture = new Fixture();
        string original = Path.Combine(fixture.Root, "original.tmp");
        string sourceLink = Path.Combine(fixture.Root, "symlink.tmp");
        string dest = Path.Combine(fixture.Root, "reviewed.docx");
        File.WriteAllText(original, "private text");
        try
        {
            File.CreateSymbolicLink(sourceLink, original);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                   or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Символические ссылки недоступны на данном стенде.");
            return;
        }
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeNativeHardLinkNoReplace.Create(dest, sourceLink));
        Assert.IsFalse(File.Exists(dest));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "gd31j33-hardlink-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
