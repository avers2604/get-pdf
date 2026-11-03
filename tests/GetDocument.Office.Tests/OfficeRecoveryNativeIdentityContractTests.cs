using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bd: pure managed tests of a future native identity contract.
/// The fake adapter is NOT an OS identity implementation or native test.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryNativeIdentityContractTests
{
    [TestMethod]
    public void RelativeNamesAllowOnlySourceHashAndKnownRecoveryCopyNames()
    {
        string hash = new string('A', 64);
        string guid = Guid.NewGuid().ToString("N");

        foreach (string suffix in new[] {
            ".docx", ".xlsx", ".pptx", ".part.docx", ".part.xlsx", ".part.pptx"
        })
        {
            RecoveryRelativeKey key = RecoveryRelativeKey.Create(hash, guid + suffix);
            Assert.AreEqual(hash, key.SourceFolderHash);
            Assert.AreEqual(guid + suffix, key.CopyFileName);
        }
    }

    [TestMethod]
    public void RelativeNamesRejectTraversalPathsStreamsAndUnknownSuffixes()
    {
        string hash = new string('B', 64);
        string guid = Guid.NewGuid().ToString("N");
        foreach (string name in new[] {
            "../" + guid + ".docx",
            "..\\" + guid + ".docx",
            guid + ".docx/../outside",
            guid + ".docx:ADS",
            guid + ".json",
            guid + ".docx.tmp",
            "/tmp/" + guid + ".docx",
            "not-a-guid.docx",
        })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryRelativeKey.Create(hash, name));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryRelativeKey.Create("../../escape", guid + ".docx"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryRelativeKey.Create(new string('Z', 64), guid + ".docx"));
    }

    [TestMethod]
    public void FullWindowsFileIdIncludesBothHighAndLowHalves()
    {
        var id = new RecoveryNativeFileId(RecoveryNativePlatform.Windows,
            DeviceOrVolume: 100, ObjectLow: 200, ObjectHigh: 300);
        var differentHigh = new RecoveryNativeFileId(RecoveryNativePlatform.Windows,
            DeviceOrVolume: 100, ObjectLow: 200, ObjectHigh: 301);
        Assert.IsTrue(id.IsComplete);
        Assert.AreNotEqual(id, differentHigh);
    }

    [TestMethod]
    public void LinuxIdentityIncludesDeviceAndInode()
    {
        var first = new RecoveryNativeFileId(RecoveryNativePlatform.Linux,
            DeviceOrVolume: 7, ObjectLow: 900, ObjectHigh: 0);
        var differentDevice = new RecoveryNativeFileId(RecoveryNativePlatform.Linux,
            DeviceOrVolume: 8, ObjectLow: 900, ObjectHigh: 0);
        Assert.IsTrue(first.IsComplete);
        Assert.AreNotEqual(first, differentDevice);
    }

    [TestMethod]
    public void DefaultRelativeKeyMustNotReachAnyAdapter()
    {
        using var scratch = new Scratch("SYNTHETIC_INVALID_KEY");
        bool invoked = false;
        var adapter = new FakeAdapter(RecoveryNativePlatform.Windows, () =>
        {
            invoked = true;
            return NewLease(scratch.FilePath, RecoveryNativePlatform.Windows);
        });
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryNativeIdentityGate.OpenVerifiedRead(adapter, default));
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void WindowsAndLinuxAdaptersStayUnavailableUntilNativeValidation()
    {
        RecoveryRelativeKey key = NewKey();
        IRecoveryNativeIdentityAdapter[] adapters = {
            new WindowsRecoveryNativeIdentityAdapter(),
            new LinuxRecoveryNativeIdentityAdapter(),
        };
        foreach (var adapter in adapters)
        {
            Assert.IsFalse(adapter.SupportsVerifiedDirectoryRelativeOpen);
            Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
                RecoveryNativeIdentityGate.OpenVerifiedRead(adapter, key));
        }
    }

    [TestMethod]
    public void VerifiedFakeLeaseMustBeReadOnlyAndRemainsOwnedByCaller()
    {
        using var scratch = new Scratch("SYNTHETIC_VERIFIED_BYTES");
        var adapter = new FakeAdapter(RecoveryNativePlatform.Windows,
            () => NewLease(scratch.FilePath, RecoveryNativePlatform.Windows));
        using var lease = RecoveryNativeIdentityGate.OpenVerifiedRead(adapter, NewKey());

        Assert.IsTrue(lease.Stream.CanRead);
        Assert.IsFalse(lease.Stream.CanWrite);
        Assert.AreEqual((int)'S', lease.Stream.ReadByte());
    }

    [TestMethod]
    public void MismatchedParentOrFileIdentityFailsClosedAndClosesStream()
    {
        using var scratch = new Scratch("SYNTHETIC_MISMATCH_BYTES");
        var valid = NewIds(RecoveryNativePlatform.Linux);
        var changedFile = valid.File with { ObjectLow = valid.File.ObjectLow + 1 };
        var changedParent = valid.Parent with { ObjectLow = valid.Parent.ObjectLow + 1 };
        foreach (var identity in new[] {
            (valid.Parent, valid.Parent, valid.File, changedFile),
            (valid.Parent, changedParent, valid.File, valid.File),
        })
        {
            RecoveryNativeReadLease? provided = null;
            var adapter = new FakeAdapter(RecoveryNativePlatform.Linux, () =>
                provided = Lease(scratch.FilePath, RecoveryNativePlatform.Linux,
                    identity.Item1, identity.Item2, identity.Item3, identity.Item4));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RecoveryNativeIdentityGate.OpenVerifiedRead(adapter, NewKey()));
            Assert.IsNotNull(provided);
            Assert.IsFalse(provided.Stream.CanRead, "Rejecting a claim closes its handle.");
        }
    }

    [TestMethod]
    public void UnsupportedEvidenceOrPartialNetworkIdentityFailsClosed()
    {
        using var scratch = new Scratch("SYNTHETIC_NO_FOLLOW_POLICY");
        var ids = NewIds(RecoveryNativePlatform.Windows);
        var invalidFile = ids.File with { ObjectLow = 0, ObjectHigh = 0 };

        foreach (var change in new[] { "no-follow", "parent", "missing-file-id" })
        {
            RecoveryNativeReadLease? provided = null;
            var adapter = new FakeAdapter(RecoveryNativePlatform.Windows, () =>
                provided = Lease(scratch.FilePath, RecoveryNativePlatform.Windows,
                    ids.Parent, ids.Parent, ids.File,
                    change == "missing-file-id" ? invalidFile : ids.File,
                    directoryRelative: change != "parent",
                    noFollow: change != "no-follow"));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RecoveryNativeIdentityGate.OpenVerifiedRead(adapter, NewKey()));
            Assert.IsNotNull(provided);
            Assert.IsFalse(provided.Stream.CanRead);
        }
    }

    [TestMethod]
    public void EmptyStreamAndPlatformMismatchAreRejected()
    {
        using var scratch = new Scratch("");
        var adapter = new FakeAdapter(RecoveryNativePlatform.Windows,
            () => NewLease(scratch.FilePath, RecoveryNativePlatform.Windows));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeIdentityGate.OpenVerifiedRead(adapter, NewKey()));

        using var normal = new Scratch("SYNTHETIC_LINUX_ID");
        var mismatched = new FakeAdapter(RecoveryNativePlatform.Windows,
            () => NewLease(normal.FilePath, RecoveryNativePlatform.Linux));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeIdentityGate.OpenVerifiedRead(mismatched, NewKey()));
    }

    private static RecoveryRelativeKey NewKey() => RecoveryRelativeKey.Create(
        new string('F', 64), Guid.NewGuid().ToString("N") + ".docx");

    private static (RecoveryNativeFileId Parent, RecoveryNativeFileId File) NewIds(
        RecoveryNativePlatform platform)
    {
        ulong highParent = platform == RecoveryNativePlatform.Windows ? 0x5678UL : 0UL;
        ulong highFile = platform == RecoveryNativePlatform.Windows ? 0x8888UL : 0UL;
        var parent = new RecoveryNativeFileId(platform, 0x1234, 0x3456, highParent);
        var file = new RecoveryNativeFileId(platform, 0x1234, 0x9999, highFile);
        return (parent, file);
    }

    private static RecoveryNativeReadLease NewLease(string path, RecoveryNativePlatform platform)
    {
        var ids = NewIds(platform);
        return Lease(path, platform, ids.Parent, ids.Parent, ids.File, ids.File);
    }

    private static RecoveryNativeReadLease Lease(
        string path, RecoveryNativePlatform platform,
        RecoveryNativeFileId parentBefore, RecoveryNativeFileId parentAfter,
        RecoveryNativeFileId fileBefore, RecoveryNativeFileId fileAfter,
        bool directoryRelative = true, bool noFollow = true) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            platform, parentBefore, parentAfter, fileBefore, fileAfter,
            directoryRelative, noFollow);

    private sealed class FakeAdapter : IRecoveryNativeIdentityAdapter
    {
        private readonly Func<RecoveryNativeReadLease> _open;
        public RecoveryNativePlatform Platform { get; }
        public bool SupportsVerifiedDirectoryRelativeOpen => true;

        public FakeAdapter(RecoveryNativePlatform platform, Func<RecoveryNativeReadLease> open)
        {
            Platform = platform;
            _open = open;
        }
        public RecoveryNativeReadLease OpenReadOnlyRelative(RecoveryRelativeKey key)
            => _open();
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bd-" + Guid.NewGuid().ToString("N"));

        public string FilePath => Path.Combine(_dir, "synthetic.docx");

        public Scratch(string content)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
    }
}
