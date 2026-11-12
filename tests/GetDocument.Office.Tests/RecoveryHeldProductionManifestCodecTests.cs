using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using System.Text.Json;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27cf RED: exact existing production RecoveryManifest JSON shape,
/// parsed from an already supplied candidate byte envelope (never SourcePath).
/// </summary>
[TestClass]
public sealed class RecoveryHeldProductionManifestCodecTests
{
    private static RecoveryHeldProductionManifestCandidate Example() =>
        new(@"C:\GET\Documents\report.docx",
            new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero),
            new string('A', 64));

    [TestMethod]
    public void MatchesExistingProductionManifestJsonShape()
    {
        byte[] bytes = RecoveryHeldProductionManifestCodec.EncodeCanonical(Example());
        using JsonDocument root = JsonDocument.Parse(bytes);
        string[] names = root.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
        CollectionAssert.AreEqual(
            new[] { "SourcePath", "CapturedUtc", "Sha256" }, names);
        Assert.AreEqual(Example(), RecoveryHeldProductionManifestCodec.DecodeCandidate(bytes));
        Assert.IsFalse(RecoveryHeldProductionManifestCodec.HasTrustedManifestOrigin);
    }

    [TestMethod]
    public void OtherCeLabReceiptFormatIsNotAccepted()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { Version = 1, TransactionId = Guid.NewGuid(),
                JournalHeadSha256 = new string('0', 64),
                CopySha256 = new string('B', 64) });
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.DecodeCandidate(bytes));
    }

    [TestMethod]
    public void UnknownFieldIsRejectedRatherThanIgnored()
    {
        string json = Encoding.UTF8.GetString(
            RecoveryHeldProductionManifestCodec.EncodeCanonical(Example()));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.DecodeCandidate(
                Encoding.UTF8.GetBytes(json.Replace("}", ",\"Unexpected\":true}"))));
    }

    [TestMethod]
    public void TrailingSpacesAndDifferentFieldOrderingRejected()
    {
        byte[] canonical = RecoveryHeldProductionManifestCodec.EncodeCanonical(Example());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.DecodeCandidate(
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonical) + " ")));
        string wrongOrder = JsonSerializer.Serialize(
            new { Example().Sha256, Example().CapturedUtc, Example().SourcePath });
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.DecodeCandidate(
                Encoding.UTF8.GetBytes(wrongOrder)));
    }

    [TestMethod]
    public void InvalidOrLowercaseShaRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.EncodeCanonical(
                Example() with { Sha256 = "BAD" }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.EncodeCanonical(
                Example() with { Sha256 = new string('a', 64) }));
    }

    [TestMethod]
    public void MissingSourceOrDefaultTimeRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.EncodeCanonical(
                Example() with { SourcePath = "" }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.EncodeCanonical(
                Example() with { CapturedUtc = default }));
    }

    [TestMethod]
    public void ManifestHasStrictByteSizeAndNoPathResolution()
    {
        Assert.AreEqual(2048, RecoveryHeldProductionManifestCodec.MaxManifestBytes);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.DecodeCandidate(
                new byte[RecoveryHeldProductionManifestCodec.MaxManifestBytes + 1]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryHeldProductionManifestCodec.EncodeCanonical(
                Example() with { SourcePath = new string('A', 3000) }));
        Assert.IsFalse(RecoveryHeldProductionManifestCodec.CanOpenSourceDocument);
    }

    [TestMethod]
    public void UnsafePathContentIsDataOnlyNotPermission()
    {
        var claim = Example() with {
            SourcePath = @"C:\Other\WORM\protected.docx"
        };
        var result = RecoveryHeldProductionManifestCodec.DecodeCandidate(
            RecoveryHeldProductionManifestCodec.EncodeCanonical(claim));
        Assert.AreEqual(claim.SourcePath, result.SourcePath);
        Assert.IsFalse(RecoveryHeldProductionManifestCodec.CanAuthorizeRestore);
        Assert.IsFalse(RecoveryHeldProductionManifestCodec.CanAuthorizeDelete);
    }
}
