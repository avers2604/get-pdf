using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO.Compression;
using System.Text;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27cf RED: read actual production manifest bytes and hash an
/// already-held OOXML copy, never open the untrusted SourcePath.
/// </summary>
[TestClass]
public sealed class RecoveryHeldProductionOoxmlReaderTests
{
    private const string Source = "C:\\GET\\Files\\report.docx";

    private static byte[] BuildZip(
        string mainPart = "word/document.xml",
        string mainPayload = "<document><p>Testing</p></document>",
        string? extraName = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(xml);
            }
            Add("[Content_Types].xml", "<Types><Default Extension=\"xml\"/></Types>");
            Add(mainPart, mainPayload);
            if (extraName is not null) Add(extraName, "<extra/>");
        }
        return buffer.ToArray();
    }

    private static byte[] Manifest(byte[] copy, string source = Source) =>
        RecoveryHeldProductionManifestCodec.EncodeCanonical(
            new RecoveryHeldProductionManifestCandidate(
                source, new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy))));

    private static Task<RecoveryHeldProductionOoxmlReadbackResult> Inspect(
        byte[] copy, byte[]? manifest = null, string source = Source,
        string extension = ".docx", CancellationToken token = default)
    {
        // The actual stream lifetime is owned by the returned async helper.
        return InspectStreams(new MemoryStream(copy),
            new MemoryStream(manifest ?? Manifest(copy, source)), source,
            extension, token);
    }

    private static async Task<RecoveryHeldProductionOoxmlReadbackResult> InspectStreams(
        Stream copy, Stream manifest, string source,
        string extension, CancellationToken token)
    {
        using (copy)
        using (manifest)
        {
            return await RecoveryHeldProductionOoxmlReader.ReadOnHeldStreamsAsync(
                copy, manifest, source, extension, token);
        }
    }

    [TestMethod]
    public async Task ValidProductionDocxManifestAndCopyHashMatchOnlyAtObservation()
    {
        byte[] copy = BuildZip();
        var result = await Inspect(copy);
        Assert.AreEqual(RecoveryHeldProductionOoxmlFinding.MatchedTwiceUntrusted,
            result.Finding);
        Assert.AreEqual(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(copy)), result.CopySha256);
        Assert.IsFalse(result.CanRestore);
        Assert.IsFalse(result.CanDelete);
        Assert.IsFalse(result.CanPublish);
        Assert.IsFalse(result.HasIndependentManifestAuthentication);
        Assert.IsFalse(result.HasVerifiedNameBinding);
        Assert.IsTrue(result.OoxmlEnvelopeParsed);
    }

    [TestMethod]
    public async Task ManifestHashMismatchRejectsActualCopyBytes()
    {
        byte[] copy = BuildZip();
        byte[] bad = RecoveryHeldProductionManifestCodec.EncodeCanonical(
            RecoveryHeldProductionManifestCodec.DecodeCandidate(Manifest(copy))
                with { Sha256 = new string('A', 64) });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(copy, bad));
    }

    [TestMethod]
    public async Task ExpectedSourceMismatchDoesNotOpenOrModifySourceDocument()
    {
        byte[] copy = BuildZip();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(copy, Manifest(copy), source: "C:\\Other\\source.docx"));
        Assert.IsFalse(RecoveryHeldProductionOoxmlReader.CanOpenManifestSourcePath);
    }

    [TestMethod]
    public async Task InvalidZipMagicAndTruncatedArchiveReject()
    {
        byte[] bad = Encoding.UTF8.GetBytes("not-a-zip");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(bad));
        byte[] zip = BuildZip();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(zip[..^12]));
    }

    [TestMethod]
    public async Task MissingContentTypesOrMainPartRejected()
    {
        byte[] missingMain = BuildZip(mainPart: "docProps/core.xml");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(missingMain));
        byte[] wrongExt = BuildZip();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(wrongExt, extension: ".xlsx"));
    }

    [TestMethod]
    public async Task DangerousZipTraversalEntryRejected()
    {
        byte[] copy = BuildZip(extraName: "../escape.xml");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(copy));
    }

    [TestMethod]
    public async Task MacrosAndActiveXRejectedInNonMacroOoxml()
    {
        byte[] macro = BuildZip(extraName: "word/vbaProject.bin");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(macro));
        byte[] activeX = BuildZip(extraName: "word/activeX/control.xml");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(activeX));
    }

    [TestMethod]
    public async Task DtdXmlRejected()
    {
        byte[] copy = BuildZip(mainPayload: "<!DOCTYPE a [<!ENTITY x 'exp'>]><a>&x;</a>");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(copy));
    }

    [TestMethod]
    public async Task TooLargeManifestRejectedWithoutAllocatingCopy()
    {
        byte[] copy = BuildZip();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => Inspect(copy,
                new byte[RecoveryHeldProductionManifestCodec.MaxManifestBytes + 1]));
    }

    [TestMethod]
    public async Task UnsupportedExtensionRejectedBeforeReading()
    {
        byte[] copy = BuildZip();
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => Inspect(copy, extension: ".pdf"));
    }

    [TestMethod]
    public async Task SameStreamInBothRolesDenied()
    {
        byte[] copy = BuildZip();
        using var s = new MemoryStream(copy);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            RecoveryHeldProductionOoxmlReader.ReadOnHeldStreamsAsync(
                s, s, Source, ".docx"));
    }

    [TestMethod]
    public async Task CanceledBeforeReadingPreservesCallerBytes()
    {
        byte[] copy = BuildZip();
        byte[] manifest = Manifest(copy);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => Inspect(copy, manifest, token: cts.Token));
        CollectionAssert.AreEqual(copy, BuildZip());
        Assert.IsFalse(RecoveryHeldProductionOoxmlReader.CanAuthorizeCleanup);
    }

    [TestMethod]
    public async Task ChangedCopyBetweenPassesRejected()
    {
        byte[] copy = BuildZip();
        using var c = new MutateOnLaterAsyncHashRead(copy);
        using var m = new MemoryStream(Manifest(copy));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            RecoveryHeldProductionOoxmlReader.ReadOnHeldStreamsAsync(
                c, m, Source, ".docx"));
    }

    [TestMethod]
    public async Task ChangedManifestBetweenPassesRejected()
    {
        byte[] copy = BuildZip();
        using var c = new MemoryStream(copy);
        using var m = new MutateOnLaterAsyncHashRead(Manifest(copy), triggerRead: 2);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            RecoveryHeldProductionOoxmlReader.ReadOnHeldStreamsAsync(
                c, m, Source, ".docx"));
    }

    [TestMethod]
    public async Task ValidXlsxAndPptxCopyTypesAreRecognized()
    {
        byte[] workbook = BuildZip(mainPart: "xl/workbook.xml");
        var xlsx = await Inspect(workbook,
            Manifest(workbook, "C:\\GET\\Files\\report.xlsx"),
            source: "C:\\GET\\Files\\report.xlsx", extension: ".xlsx");
        Assert.AreEqual(RecoveryHeldProductionOoxmlFinding.MatchedTwiceUntrusted,
            xlsx.Finding);

        byte[] presentation = BuildZip(mainPart: "ppt/presentation.xml");
        var pptx = await Inspect(presentation,
            Manifest(presentation, "C:\\GET\\Files\\report.pptx"),
            source: "C:\\GET\\Files\\report.pptx", extension: ".pptx");
        Assert.AreEqual(RecoveryHeldProductionOoxmlFinding.MatchedTwiceUntrusted,
            pptx.Finding);
    }

    [TestMethod]
    public async Task OpenFileStreamReadbackIsOptionalTestOwnedFixture()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) ||
            Environment.GetEnvironmentVariable("GD31J27CF_HELD_FILE_LAB") != "1")
            return;
        byte[] copy = BuildZip();
        byte[] manifest = Manifest(copy);
        string dir = Path.Combine(Path.GetTempPath(),
            "getdoc-cf-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string copyPath = Path.Combine(dir, "owned.docx");
            string manifestPath = Path.Combine(dir, "owned.json");
            File.WriteAllBytes(copyPath, copy);
            File.WriteAllBytes(manifestPath, manifest);
            await using (var c = new FileStream(copyPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            await using (var m = new FileStream(manifestPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                var result = await RecoveryHeldProductionOoxmlReader.ReadOnHeldStreamsAsync(
                    c, m, Source, ".docx");
                Assert.IsTrue(result.UsedHeldNativeFileStreams);
                Assert.IsFalse(result.HasVerifiedNameBinding);
                Assert.IsFalse(result.CanRestore);
            }
        }
        finally
        {
            // Cleanup belongs only to this uniquely named TEST fixture.
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class MutateOnLaterAsyncHashRead(byte[] bytes, int triggerRead = 3) :
        MemoryStream((byte[])bytes.Clone())
    {
        private int _reads;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (++_reads == triggerRead)
            {
                long position = Position;
                Position = 0;
                WriteByte((byte)'?');
                Position = position;
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
