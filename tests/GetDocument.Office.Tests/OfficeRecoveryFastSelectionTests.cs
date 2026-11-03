using System.IO.Compression;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27az: synthetic OOXML only. These cases demonstrate selection
/// strategy without benchmarking the real NTFS/SMB or ZIP decompressor.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryFastSelectionTests
{
    [TestMethod]
    public async Task NewestValidSnapshotIsTheOnlyOneFullyVerified()
    {
        using var fixture = new Scratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        OfficeRecoveryCopy older = await CaptureAsync(store, fixture.Source);
        await Task.Delay(25);
        OfficeRecoveryCopy newest = await CaptureAsync(store, fixture.Source);
        string newestManifest = Path.ChangeExtension(newest.FilePath, ".json");

        var checkedManifests = new List<string>();
        OfficeRecoveryCopy? selected = store.FindLatestWithEnumerator(fixture.Source,
            folder => Directory.EnumerateFiles(folder, "*.json"),
            manifestPath =>
            {
                checkedManifests.Add(manifestPath);
                if (!string.Equals(manifestPath, newestManifest, StringComparison.Ordinal))
                    throw new InvalidOperationException("Older copy should not be inflated or hashed.");
                return newest;
            });

        Assert.AreEqual(newest.FilePath, selected?.FilePath);
        CollectionAssert.AreEqual(new[] { newestManifest }, checkedManifests.ToArray());
        Assert.IsTrue(File.Exists(older.FilePath));
        Assert.AreEqual(newest.FilePath, store.FindLatest(fixture.Source)?.FilePath);
    }

    [TestMethod]
    public async Task FailedNewestVerificationFallsBackToOlderVerifiedCopy()
    {
        using var fixture = new Scratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        OfficeRecoveryCopy older = await CaptureAsync(store, fixture.Source);
        await Task.Delay(25);
        OfficeRecoveryCopy newest = await CaptureAsync(store, fixture.Source);
        string newestManifest = Path.ChangeExtension(newest.FilePath, ".json");
        string olderManifest = Path.ChangeExtension(older.FilePath, ".json");

        var checkedManifests = new List<string>();
        OfficeRecoveryCopy? selected = store.FindLatestWithEnumerator(fixture.Source,
            folder => Directory.EnumerateFiles(folder, "*.json"),
            manifestPath =>
            {
                checkedManifests.Add(manifestPath);
                if (manifestPath == newestManifest) return null;
                if (manifestPath == olderManifest) return older;
                throw new InvalidOperationException("Unexpected candidate.");
            });

        Assert.AreEqual(older.FilePath, selected?.FilePath);
        CollectionAssert.AreEqual(new[] { newestManifest, olderManifest },
            checkedManifests.ToArray());
    }

    [TestMethod]
    public void TooManyManifestsFailsClosedBeforeReadingAnySnapshot()
    {
        using var fixture = new Scratch();
        Directory.CreateDirectory(fixture.PerSourceFolder);
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        bool verifiedAny = false;

        Assert.ThrowsExactly<InvalidDataException>(() =>
            store.FindLatestWithEnumerator(fixture.Source,
                folder => Enumerable.Range(0, 4097).Select(i =>
                    Path.Combine(folder, i.ToString("D8") + ".json")),
                _ =>
                {
                    verifiedAny = true;
                    throw new InvalidOperationException("No verification allowed over budget.");
                }));

        Assert.IsFalse(verifiedAny);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.PerSourceFolder).Length);
    }

    [TestMethod]
    public async Task ChangedManifestTimestampDuringValidationFailsClosed()
    {
        using var fixture = new Scratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        OfficeRecoveryCopy latest = await CaptureAsync(store, fixture.Source);
        string manifest = Path.ChangeExtension(latest.FilePath, ".json");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            store.FindLatestWithEnumerator(fixture.Source,
                folder => Directory.EnumerateFiles(folder, "*.json"),
                path =>
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(
                        File.ReadAllText(manifest))!.AsObject();
                    json["CapturedUtc"] = DateTimeOffset.UtcNow.AddMinutes(-30);
                    File.WriteAllText(manifest, json.ToJsonString());
                    return latest; // simulated successful expensive verification
                }));

        Assert.IsTrue(File.Exists(latest.FilePath));
        Assert.IsTrue(File.Exists(manifest));
        Assert.IsTrue(File.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task InvalidManifestBecomesValidDuringVerificationFailsClosed()
    {
        using var fixture = new Scratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        OfficeRecoveryCopy older = await CaptureAsync(store, fixture.Source);
        await Task.Delay(25);
        OfficeRecoveryCopy newest = await CaptureAsync(store, fixture.Source);
        string olderManifest = Path.ChangeExtension(older.FilePath, ".json");
        byte[] originalBytes = File.ReadAllBytes(olderManifest);
        File.WriteAllText(olderManifest, "{not valid");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            store.FindLatestWithEnumerator(fixture.Source,
                folder => Directory.EnumerateFiles(folder, "*.json"),
                path =>
                {
                    File.WriteAllBytes(olderManifest, originalBytes);
                    return newest;
                }));

        Assert.IsTrue(File.Exists(newest.FilePath));
        Assert.IsTrue(File.Exists(older.FilePath));
        Assert.IsTrue(File.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task NewManifestAddedDuringVerificationFailsClosed()
    {
        using var fixture = new Scratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        OfficeRecoveryCopy copy = await CaptureAsync(store, fixture.Source);
        string extra = Path.Combine(Path.GetDirectoryName(copy.FilePath)!,
            Guid.NewGuid().ToString("N") + ".json");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            store.FindLatestWithEnumerator(fixture.Source,
                folder => Directory.EnumerateFiles(folder, "*.json"),
                path =>
                {
                    File.WriteAllText(extra, "{not valid");
                    return copy;
                }));
        Assert.IsTrue(File.Exists(extra));
        Assert.IsTrue(File.Exists(copy.FilePath));
    }

    [TestMethod]
    public async Task CopyChangedAfterHashMustNotBeAccepted()
    {
        using var fixture = new Scratch();
        var store = new OfficeRecoveryStore(fixture.RecoveryRoot);
        OfficeRecoveryCopy copy = await CaptureAsync(store, fixture.Source);
        string manifest = Path.ChangeExtension(copy.FilePath, ".json");
        long originalLength = new FileInfo(copy.FilePath).Length;

        bool writerDenied = false;
        OfficeRecoveryCopy? result = store.ReadValidCopyWithPostHashMutation(
            fixture.Source, manifest, path =>
            {
                // Windows FileShare.Read can deny the attempted write while
                // the same input handle stays open. POSIX/SMB may permit it;
                // in that case the changed content must not be accepted.
                try { File.AppendAllText(path, "SYNTHETIC_AFTER_HASH"); }
                catch (IOException) { writerDenied = true; }
            });

        if (writerDenied)
        {
            Assert.IsNotNull(result, "A blocked write leaves the verified snapshot unchanged.");
            Assert.AreEqual(originalLength, new FileInfo(copy.FilePath).Length);
        }
        else
        {
            Assert.IsNull(result, "If a concurrent write succeeds, the snapshot must fail verification.");
            Assert.IsTrue(new FileInfo(copy.FilePath).Length > originalLength);
        }
        Assert.IsTrue(File.Exists(manifest));
        Assert.IsTrue(File.Exists(fixture.Source));
    }

    private static Task<OfficeRecoveryCopy> CaptureAsync(OfficeRecoveryStore store, string source)
        => store.CaptureAsync(source, temporary =>
        {
            File.Copy(source, temporary);
            return Task.CompletedTask;
        });

    private sealed class Scratch : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27az-" + Guid.NewGuid().ToString("N"));

        public string Source => Path.Combine(_root, "synthetic.docx");
        public string RecoveryRoot => Path.Combine(_root, "recovery");
        public string PerSourceFolder
        {
            get
            {
                string normalized = OperatingSystem.IsWindows()
                    ? Path.GetFullPath(Source).ToUpperInvariant()
                    : Path.GetFullPath(Source);
                string hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(normalized)));
                return Path.Combine(RecoveryRoot, hash);
            }
        }

        public Scratch()
        {
            Directory.CreateDirectory(_root);
            using var output = File.Create(Source);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            using (var entry = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                entry.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var entry = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                entry.Write("<root/>");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
