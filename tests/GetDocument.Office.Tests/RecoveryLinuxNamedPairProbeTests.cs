using System.Buffers.Binary;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bw: synthetic contracts for one-time Linux name-to-held-handle
/// evidence. No native unlink/rename, no Astra host attestation.
/// </summary>
[TestClass]
public sealed class RecoveryLinuxNamedPairProbeTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void StrictNamesAcceptOnlyMatchedCanonicalGuidAndOfficeSuffix()
    {
        foreach (string ext in new[] { ".docx", ".xlsx", ".pptx" })
        {
            RecoveryLinuxNamedPairNames names =
                RecoveryLinuxNamedPairProbe.ValidatePairNames(
                    Nonce + ext, Nonce + ".json");
            Assert.AreEqual(Nonce, names.Nonce);
            Assert.AreEqual(ext, names.Extension);
        }
    }

    [TestMethod]
    public void NameValidationRejectsTraversalTempAndMixedGuids()
    {
        foreach ((string copy, string manifest) in new[]
        {
            ("../" + Nonce + ".docx", Nonce + ".json"),
            ("/tmp/" + Nonce + ".docx", Nonce + ".json"),
            (Nonce.ToUpperInvariant() + ".docx", Nonce + ".json"),
            (Nonce + ".part.docx", Nonce + ".json"),
            (Nonce + ".docx", Nonce + ".json.tmp"),
            (Nonce + ".docx", new string('f', 32) + ".json"),
            (Nonce + ".pdf", Nonce + ".json"),
            (Nonce + ".docx/", Nonce + ".json"),
            (Nonce + ".docx", "../" + Nonce + ".json"),
            ("", Nonce + ".json")
        })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryLinuxNamedPairProbe.ValidatePairNames(copy, manifest));
    }

    [TestMethod]
    public void CompleteOwnerOnlyRegularStatxCanBeReadWithoutAuthorizingMutation()
    {
        var item = RecoveryLinuxNamedPairProbe.ParseRegularStatx(
            RegularStatx(), expectedEffectiveUid: 1234);
        Assert.IsTrue(item.Id.IsComplete);
        Assert.AreEqual(77UL, item.MountId);
        Assert.AreEqual(1U, item.LinkCount);
        Assert.AreEqual(1234U, item.OwnerUid);
    }

    [TestMethod]
    public void SymlinkDirectoryAndDevicesAreNotRegularFiles()
    {
        foreach (ushort mode in new ushort[] { 0xA180, 0x4180, 0x2180, 0x1180 })
        {
            byte[] bytes = RegularStatx();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), mode);
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RecoveryLinuxNamedPairProbe.ParseRegularStatx(bytes, 1234));
        }
    }

    [TestMethod]
    public void BroadPermissionsForeignUidOrHardlinksAreRejected()
    {
        byte[] bytes = RegularStatx();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 0x81A4);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxNamedPairProbe.ParseRegularStatx(bytes, 1234));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxNamedPairProbe.ParseRegularStatx(RegularStatx(), 44));
        bytes = RegularStatx();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 2);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxNamedPairProbe.ParseRegularStatx(bytes, 1234));
    }

    [TestMethod]
    public void IncompleteStatxAndMissingMountFailClosed()
    {
        byte[] bytes = RegularStatx();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x0001);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxNamedPairProbe.ParseRegularStatx(bytes, 1234));
        bytes = RegularStatx();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(144, 8), 0);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryLinuxNamedPairProbe.ParseRegularStatx(bytes, 1234));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxNamedPairProbe.ParseRegularStatx(new byte[3], 1234));
    }

    [TestMethod]
    public void StableHeldAndNamedIdsGiveObservationOnly()
    {
        var good = Good();
        Assert.AreEqual(RecoveryLinuxNamedPairFinding.MatchedAtObservation,
            RecoveryLinuxNamedPairProbe.Evaluate(good));
        Assert.IsFalse(good.CanAuthorizeMutation);
    }

    [TestMethod]
    public void RenamedCopyOrManifestNameIsRejected()
    {
        var good = Good();
        Assert.AreEqual(RecoveryLinuxNamedPairFinding.NameDoesNotMatchHeldHandle,
            RecoveryLinuxNamedPairProbe.Evaluate(good with
            {
                CopyNamed = Regular(98)
            }));
        Assert.AreEqual(RecoveryLinuxNamedPairFinding.NameDoesNotMatchHeldHandle,
            RecoveryLinuxNamedPairProbe.Evaluate(good with
            {
                ManifestNamedAtRecheck = Regular(98)
            }));
    }

    [TestMethod]
    public void SameInodeAndDifferentMountOrHardlinkAreRejected()
    {
        var good = Good();
        Assert.AreEqual(RecoveryLinuxNamedPairFinding.AliasedCopyAndManifest,
            RecoveryLinuxNamedPairProbe.Evaluate(good with
            {
                ManifestHeld = Regular(2), ManifestNamed = Regular(2),
                ManifestNamedAtRecheck = Regular(2),
                ManifestHeldAtRecheck = Regular(2)
            }));
        Assert.AreEqual(RecoveryLinuxNamedPairFinding.CrossMountOrDevice,
            RecoveryLinuxNamedPairProbe.Evaluate(good with
            {
                ManifestNamedAtRecheck = Regular(3) with { MountId = 99 }
            }));
    }

    [TestMethod]
    public void ParentChangedOrMissingEvidenceDoesNotAuthorizeAnything()
    {
        var good = Good();
        Assert.AreEqual(RecoveryLinuxNamedPairFinding.ParentChanged,
            RecoveryLinuxNamedPairProbe.Evaluate(good with
            {
                ParentAtRecheck = Parent() with { MountId = 99 }
            }));
        Assert.AreEqual(RecoveryLinuxNamedPairFinding.IncompleteIdentity,
            RecoveryLinuxNamedPairProbe.Evaluate(good with
            {
                CopyHeld = default
            }));
    }

    [TestMethod]
    public void InvalidOrClosedHandlesRefuseBeforeNativeCalls()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryLinuxNamedPairProbe.ObserveExistingPair(
                invalid, invalid, invalid, Nonce + ".docx", Nonce + ".json"));
    }

    [TestMethod]
    public void CancelledProbeRefusesWithoutFilesOrMutation()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryLinuxNamedPairProbe.ObserveExistingPair(
                invalid, invalid, invalid, Nonce + ".docx", Nonce + ".json",
                cancelled.Token));
        Assert.IsFalse(RecoveryLinuxNamedPairProbe.CanAuthorizePublish);
        Assert.IsFalse(RecoveryLinuxNamedPairProbe.CanAuthorizeDelete);
    }

    private static RecoveryLinuxRegularEntryObservation Regular(ulong inode) =>
        new(new RecoveryNativeFileId(RecoveryNativePlatform.Linux, 8, inode, 0),
            77, 1234, 1);

    private static RecoveryLinuxParentEvidence Parent() =>
        new(new RecoveryNativeFileId(RecoveryNativePlatform.Linux, 8, 1, 0),
            1234, 0x41C0, 77);

    private static RecoveryLinuxNamedPairObservation Good() =>
        new(Parent(), Parent(), Regular(2), Regular(3), Regular(2), Regular(3),
            Regular(2), Regular(3), Regular(2), Regular(3));

    private static byte[] RegularStatx()
    {
        var data = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x110F);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), 1234);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28, 2), 0x8180);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32, 8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(136, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(140, 4), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(144, 8), 77);
        return data;
    }
}
