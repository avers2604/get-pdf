using System.IO.Compression;
using System.Security.Cryptography;
using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeOfflineDraftSessionTests
{
    [TestMethod]
    public async Task CreatesIsolatedDurableCopyWithoutTouchingSource()
    {
        using var root = new TemporaryRoot();
        string original = Path.Combine(root.Path, "original.docx");
        CreateOoxml(original, "word/document.xml", "<w:document>before</w:document>");
        byte[] initial = await File.ReadAllBytesAsync(original);
        var draft = await EuroOfficeOfflineDraftSession.CreateAsync(
            original, root.Staging);

        Assert.AreNotEqual(original, draft.WorkingPath);
        Assert.IsTrue(File.Exists(draft.WorkingPath));
        Assert.IsTrue(File.Exists(Path.Combine(draft.RecoveryDirectory, "session.json")));
        CollectionAssert.AreEqual(initial, await File.ReadAllBytesAsync(draft.WorkingPath));
        CollectionAssert.AreEqual(initial, await File.ReadAllBytesAsync(original));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(initial)), draft.BaselineSha256);
    }

    [TestMethod]
    public async Task ExplicitSaveAsCreatesNewValidatedCopyNeverOverwritingOriginal()
    {
        using var root = new TemporaryRoot();
        string original = Path.Combine(root.Path, "original.xlsx");
        CreateOoxml(original, "xl/workbook.xml", "<workbook>before</workbook>");
        var draft = await EuroOfficeOfflineDraftSession.CreateAsync(
            original, root.Staging);
        CreateOoxml(draft.WorkingPath, "xl/workbook.xml",
            "<workbook>changed by native editor</workbook>");

        string target = Path.Combine(root.Path, "new-copy.xlsx");
        await draft.SaveAsNewCopyAsync(target, userApproved: true);
        Assert.IsTrue(File.Exists(target));
        CollectionAssert.AreEqual(
            await File.ReadAllBytesAsync(draft.WorkingPath),
            await File.ReadAllBytesAsync(target));
        Assert.IsTrue(File.Exists(draft.WorkingPath)); // recovery remains
        Assert.IsFalse(File.ReadAllText(original).Contains("changed by native editor"));
    }

    [TestMethod]
    public async Task NeverReplacesExistingFileEvenWhenDestinationMatchesSource()
    {
        using var root = new TemporaryRoot();
        string original = Path.Combine(root.Path, "original.pptx");
        CreateOoxml(original, "ppt/presentation.xml", "<presentation/>");
        byte[] initial = await File.ReadAllBytesAsync(original);
        var draft = await EuroOfficeOfflineDraftSession.CreateAsync(
            original, root.Staging);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            draft.SaveAsNewCopyAsync(original, userApproved: true));
        CollectionAssert.AreEqual(initial, await File.ReadAllBytesAsync(original));
    }

    [TestMethod]
    public async Task DeniesWithoutUserConsentOrChangedSource()
    {
        using var root = new TemporaryRoot();
        string original = Path.Combine(root.Path, "original.docx");
        CreateOoxml(original, "word/document.xml", "<document>first</document>");
        var draft = await EuroOfficeOfflineDraftSession.CreateAsync(
            original, root.Staging);
        string target = Path.Combine(root.Path, "other.docx");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            draft.SaveAsNewCopyAsync(target, userApproved: false));
        CreateOoxml(original, "word/document.xml", "<document>someone else changed this</document>");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            draft.SaveAsNewCopyAsync(target, userApproved: true));
        Assert.IsFalse(File.Exists(target));
        Assert.IsTrue(File.Exists(draft.WorkingPath));
    }

    [TestMethod]
    public async Task RejectsWrongExtensionAndMalformedOrMacroEnabledOutput()
    {
        using var root = new TemporaryRoot();
        string original = Path.Combine(root.Path, "original.docx");
        CreateOoxml(original, "word/document.xml", "<document/>");
        var draft = await EuroOfficeOfflineDraftSession.CreateAsync(
            original, root.Staging);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            draft.SaveAsNewCopyAsync(Path.Combine(root.Path, "output.xlsx"), true));

        await File.WriteAllTextAsync(draft.WorkingPath, "not a ZIP package");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            draft.SaveAsNewCopyAsync(Path.Combine(root.Path, "output.docx"), true));

        CreateOoxml(draft.WorkingPath, "word/document.xml", "<document/>",
            "word/vbaProject.bin");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            draft.SaveAsNewCopyAsync(Path.Combine(root.Path, "output.docx"), true));
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "output.docx")));
    }

    [TestMethod]
    public async Task RejectsSourcePackagesWithUnexpectedFormatAndPathTraversal()
    {
        using var root = new TemporaryRoot();
        string disguised = Path.Combine(root.Path, "disguised.docx");
        CreateOoxml(disguised, "xl/workbook.xml", "<workbook/>");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            EuroOfficeOfflineDraftSession.CreateAsync(disguised, root.Staging));

        string unsafePackage = Path.Combine(root.Path, "unsafe.docx");
        CreateOoxml(unsafePackage, "word/document.xml", "<document/>",
            "../untrusted.txt");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            EuroOfficeOfflineDraftSession.CreateAsync(unsafePackage, root.Staging));
    }

    private static void CreateOoxml(string target, string mainEntry,
        string payload, string? extraEntry = null)
    {
        using var file = new FileStream(target, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }
        Add("[Content_Types].xml", "<Types/>");
        Add("_rels/.rels", "<Relationships/>");
        Add(mainEntry, payload);
        if (extraEntry is not null) Add(extraEntry, "disallowed");
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "getdoc-gd31j20-" + Guid.NewGuid().ToString("N"));
        public string Staging => System.IO.Path.Combine(Path, "private-staging");

        public TemporaryRoot()
        {
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(Staging);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
