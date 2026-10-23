using System.Security.Cryptography;
using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class WopiProofValidationTests
{
    private static readonly Uri Origin = new("https://get-files.intra.example/");
    private static readonly Uri RequestUrl =
        new("https://get-files.intra.example/wopi/files/42?access_token=opaque");
    private static readonly DateTimeOffset Now =
        new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AcceptsValidRsaProofAndRejectsTamperedSignature()
    {
        using var current = RSA.Create(2048);
        using var old = RSA.Create(2048);
        long ticks = Now.UtcDateTime.Ticks;
        byte[] payload = WopiProofValidator.BuildExpectedProof(
            "opaque", RequestUrl, ticks);
        string valid = Convert.ToBase64String(
            current.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.IsTrue(WopiProofValidator.Verify("opaque", RequestUrl, ticks,
            valid, null, current.ExportParameters(false),
            old.ExportParameters(false), Origin, Now));
        Assert.IsFalse(WopiProofValidator.Verify("opaque-tampered", RequestUrl, ticks,
            valid, null, current.ExportParameters(false),
            old.ExportParameters(false), Origin, Now));
    }

    [TestMethod]
    public void SupportsCurrentKeyRotationProofOldHeader()
    {
        using var current = RSA.Create(2048);
        using var old = RSA.Create(2048);
        long ticks = Now.UtcDateTime.Ticks;
        byte[] payload = WopiProofValidator.BuildExpectedProof("opaque", RequestUrl, ticks);
        string validOldHeader = Convert.ToBase64String(
            current.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.IsTrue(WopiProofValidator.Verify("opaque", RequestUrl, ticks,
            null, validOldHeader, current.ExportParameters(false),
            old.ExportParameters(false), Origin, Now));
    }

    [TestMethod]
    public void RejectsExpiredFutureAndExternalRequests()
    {
        using var rsa = RSA.Create(2048);
        var pub = rsa.ExportParameters(false);
        Assert.IsFalse(WopiProofValidator.Verify("opaque", RequestUrl, Now.AddMinutes(-25).UtcDateTime.Ticks,
            "", null, pub, pub, Origin, Now));
        Assert.IsFalse(WopiProofValidator.Verify("opaque", RequestUrl, Now.AddMinutes(10).UtcDateTime.Ticks,
            "", null, pub, pub, Origin, Now));
        Assert.IsFalse(WopiProofValidator.Verify("opaque",
            new Uri("https://attacker.example/wopi/files/42"),
            Now.UtcDateTime.Ticks, "", null, pub, pub, Origin, Now));
    }

    [TestMethod]
    public void TimestampIsNetworkOrderBigEndianMatchingOfficialWopiVectors()
    {
        // The official microsoft/Office-Online-Test-Tools-and-Documentation
        // samples/python/proof_keys implementation encodes u64 as struct.pack('>Q', ...).
        byte[] proof = WopiProofValidator.BuildExpectedProof("a",
            new Uri("https://get-files.intra.example/wopi/files/42"), 0x0102030405060708L);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            proof[^8..]);
    }

    [TestMethod]
    public void ProofBytesIncludeUppercaseUrlAndBinaryTimestamp()
    {
        var bytes = WopiProofValidator.BuildExpectedProof(
            "a", new Uri("https://get-files.intra.example/wopi/files/42?q=m"), 1234L);
        Assert.AreEqual(0, bytes[0]);
        Assert.AreEqual(1, bytes[3]);
        Assert.AreEqual((byte)'a', bytes[4]);
        CollectionAssert.Contains(bytes, (byte)'H');
        Assert.AreEqual(4 + 1 + 4 +
            System.Text.Encoding.UTF8.GetByteCount(
                "https://get-files.intra.example/wopi/files/42?q=m".ToUpperInvariant()) +
            4 + 8, bytes.Length);
    }
}
