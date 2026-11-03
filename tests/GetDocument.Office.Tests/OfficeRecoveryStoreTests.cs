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
    public async Task FindLatestSkipsMalformedAndOversizedJsonAndKeepsValidCopy()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("valid-with-corrupt-neighbors.docx");
        byte[] original = File.ReadAllBytes(source);
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        OfficeRecoveryCopy valid = await store.CaptureAsync(source, destination =>
        {
            File.Copy(source, destination);
            return Task.CompletedTask;
        });
        string folder = Path.GetDirectoryName(valid.FilePath)!;
        string malformed = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        string oversized = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        string wrongType = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(malformed, "{not-json");
        File.WriteAllText(oversized, new string('x', 3000));
        File.WriteAllText(wrongType, "{\"SourcePath\":true,\"CapturedUtc\":\"not-a-date\"}");
        byte[] malformedBytes = File.ReadAllBytes(malformed);
        byte[] oversizedBytes = File.ReadAllBytes(oversized);
        byte[] wrongTypeBytes = File.ReadAllBytes(wrongType);

        Assert.AreEqual(valid.FilePath, store.FindLatest(source)?.FilePath);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(malformedBytes, File.ReadAllBytes(malformed));
        CollectionAssert.AreEqual(oversizedBytes, File.ReadAllBytes(oversized));
        CollectionAssert.AreEqual(wrongTypeBytes, File.ReadAllBytes(wrongType));
    }

    [TestMethod]
    public async Task FindLatestFallsBackToOlderValidCopyWhenNewerIsCorrupted()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("two-snapshots.xlsx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        OfficeRecoveryCopy older = await store.CaptureAsync(source, destination =>
        {
            File.Copy(source, destination);
            return Task.CompletedTask;
        });
        await Task.Delay(20);
        OfficeRecoveryCopy newer = await store.CaptureAsync(source, destination =>
        {
            File.Copy(source, destination);
            return Task.CompletedTask;
        });
        Assert.AreEqual(newer.FilePath, store.FindLatest(source)?.FilePath);

        File.AppendAllText(newer.FilePath, "TAMPERED_AFTER_PUBLICATION");

        Assert.AreEqual(older.FilePath, store.FindLatest(source)?.FilePath);
        Assert.IsTrue(File.Exists(newer.FilePath));
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task ExportFailureDoesNotExposeIncompleteCopy()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Доклад.pptx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        byte[] original = File.ReadAllBytes(source);

        await Assert.ThrowsExactlyAsync<IOException>(() => store.CaptureAsync(source, target =>
        {
            File.WriteAllText(target, "partial");
            throw new IOException("disk full");
        }));

        Assert.IsNull(store.FindLatest(source));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        Assert.AreEqual(1, store.InspectIncomplete(source).Candidates.Count,
            "A failed path-only export is an untrusted diagnostic artifact, not an owned deletion target.");
    }

    [TestMethod]
    public async Task CancelledExporterLeavesUntrustedCopyForReadOnlyInventory()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("canceled.docx");
        byte[] original = File.ReadAllBytes(source);
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            store.CaptureAsync(source, destination =>
            {
                File.Copy(source, destination);
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        // The exporter wrote a path but did not return an identity-bound
        // ownership receipt; rollback must not delete it by pathname.
        var incomplete = store.InspectIncomplete(source);
        Assert.AreEqual(1, incomplete.Candidates.Count);
        Assert.AreEqual(OfficeRecoveryIncompleteKind.StagedOfficeCopy,
            incomplete.Candidates[0].Kind);
        Assert.AreEqual(OfficeRecoveryIncompleteStatus.RegularUnverified,
            incomplete.Candidates[0].Status);
        Assert.IsNull(store.FindLatest(source));
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
    public async Task RejectsDuplicateAndTraversalZipEntriesBeforePublishingRecovery()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Проверка.docx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        using (var zip = new ZipArchive(File.Open(source, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
        {
            using var duplicate = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            duplicate.Write("<other/>");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.CaptureAsync(source, target =>
            {
                File.Copy(source, target);
                return Task.CompletedTask;
            }));
        Assert.IsNull(store.FindLatest(source));

        // An otherwise valid office archive must also reject unsafe internal paths.
        using (var zip = new ZipArchive(File.Open(source, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
        {
            foreach (var duplicate in zip.Entries.Where(x => x.FullName == "word/document.xml").Skip(1).ToArray())
                duplicate.Delete();
            using var unsafePart = new StreamWriter(zip.CreateEntry("../payload.txt").Open());
            unsafePart.Write("payload");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.CaptureAsync(source, target =>
            {
                File.Copy(source, target);
                return Task.CompletedTask;
            }));
        Assert.IsNull(store.FindLatest(source));
    }

    [TestMethod]
    public async Task RejectsSuspiciousHighlyCompressedRecoveryEntries()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Копия.xlsx");
        using (var zip = new ZipArchive(File.Open(source, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
        {
            using var uncompressed = zip.CreateEntry("xl/large.bin", CompressionLevel.Optimal).Open();
            byte[] block = new byte[1024 * 1024];
            uncompressed.Write(block);
        }
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.CaptureAsync(source, target =>
            {
                File.Copy(source, target);
                return Task.CompletedTask;
            }));
        Assert.IsNull(store.FindLatest(source));
        Assert.IsTrue(File.Exists(source));
    }

    [DataTestMethod]
    [DataRow("<root><unclosed>", "Некорректный XML")]
    [DataRow("<!DOCTYPE root [<!ENTITY unsafe 'text'>]><root>&unsafe;</root>",
        "Документы с DTD")]
    public async Task RecoverySnapshotRejectsMalformedXmlAndDtd(string xml, string reason)
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Акт.docx");
        // An OOXML ZIP envelope with a damaged XML part must not be offered
        // as a valid crash-recovery copy, even if the ZIP directory looks valid.
        using (var zip = new ZipArchive(
            File.Open(source, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
        {
            zip.GetEntry("word/document.xml")!.Delete();
            using var replacement = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            replacement.Write(xml);
        }

        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.CaptureAsync(source, target =>
            {
                File.Copy(source, target);
                return Task.CompletedTask;
            }), reason);
        Assert.IsNull(store.FindLatest(source));
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task ValidRecoverySnapshotWithXmlMetadataRemainsReadable()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("Ведомость.xlsx");
        using (var zip = new ZipArchive(
            File.Open(source, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
        {
            using var settings = new StreamWriter(zip.CreateEntry("xl/styles.xml").Open());
            settings.Write("<?xml version=\"1.0\"?><styles xmlns=\"urn:test\"/>");
        }

        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        var copy = await store.CaptureAsync(source, target =>
        {
            File.Copy(source, target);
            return Task.CompletedTask;
        });
        Assert.AreEqual(copy.FilePath, store.FindLatest(source)?.FilePath);
        Assert.IsTrue(File.Exists(copy.FilePath));
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
    public async Task DiscardRetainsTamperedSnapshotAndManifestForManualInvestigation()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("tamper.docx");
        byte[] original = File.ReadAllBytes(source);
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        OfficeRecoveryCopy snapshot = await store.CaptureAsync(source, destination =>
        {
            File.Copy(source, destination);
            return Task.CompletedTask;
        });
        string manifest = Directory.GetFiles(fixture.RecoveryDirectory, "*.json",
            SearchOption.AllDirectories).Single();

        // The manifest still claims this snapshot belongs to the source,
        // but its SHA-256 no longer matches. A mere path match is not proof.
        File.AppendAllText(snapshot.FilePath, "TAMPERED");
        byte[] suspectBytes = File.ReadAllBytes(snapshot.FilePath);

        store.Discard(source);

        CollectionAssert.AreEqual(suspectBytes, File.ReadAllBytes(snapshot.FilePath));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        Assert.IsTrue(File.Exists(manifest));
        Assert.IsNull(store.FindLatest(source));
    }

    [TestMethod]
    public async Task DiscardDoesNotUnlinkSymlinkReplacedRecoveryCopy()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("replaced.docx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        OfficeRecoveryCopy snapshot = await store.CaptureAsync(source, destination =>
        {
            File.Copy(source, destination);
            return Task.CompletedTask;
        });
        string manifest = Directory.GetFiles(fixture.RecoveryDirectory, "*.json",
            SearchOption.AllDirectories).Single();
        string outside = fixture.CreateOfficeFile("unrelated.docx");
        byte[] outsideBytes = File.ReadAllBytes(outside);

        File.Delete(snapshot.FilePath);
        File.CreateSymbolicLink(snapshot.FilePath, outside);

        store.Discard(source);

        Assert.IsTrue(File.Exists(manifest));
        Assert.IsNotNull(new FileInfo(snapshot.FilePath).LinkTarget);
        CollectionAssert.AreEqual(outsideBytes, File.ReadAllBytes(outside));
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task DiscardPreservesOrphanManifestWhenItsCopyDisappeared()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("missing.xlsx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        OfficeRecoveryCopy snapshot = await store.CaptureAsync(source, destination =>
        {
            File.Copy(source, destination);
            return Task.CompletedTask;
        });
        string manifest = Directory.GetFiles(fixture.RecoveryDirectory, "*.json",
            SearchOption.AllDirectories).Single();
        byte[] manifestBytes = File.ReadAllBytes(manifest);

        File.Delete(snapshot.FilePath);
        store.Discard(source);

        CollectionAssert.AreEqual(manifestBytes, File.ReadAllBytes(manifest));
        Assert.IsFalse(File.Exists(snapshot.FilePath));
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

    [TestMethod]
    public async Task RetainsOnlyThreeMostRecentValidatedCopies()
    {
        using var fixture = new RecoveryFixture();
        string source = fixture.CreateOfficeFile("План.docx");
        var store = new OfficeRecoveryStore(fixture.RecoveryDirectory);
        for (int i = 0; i < 5; i++)
        {
            await store.CaptureAsync(source, target =>
            {
                File.Copy(source, target);
                return Task.CompletedTask;
            });
        }
        Assert.AreEqual(3, Directory.GetFiles(fixture.RecoveryDirectory, "*.json", SearchOption.AllDirectories).Length);
        Assert.IsNotNull(store.FindLatest(source));
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
            using (var part = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                part.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            string mainPart = Path.GetExtension(filename).ToLowerInvariant() switch
            {
                ".docx" => "word/document.xml",
                ".xlsx" => "xl/workbook.xml",
                _ => "ppt/presentation.xml"
            };
            using (var part = new StreamWriter(zip.CreateEntry(mainPart).Open()))
                part.Write("<root/>");
            return target;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
