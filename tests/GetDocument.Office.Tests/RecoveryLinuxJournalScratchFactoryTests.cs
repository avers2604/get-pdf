using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27cb: fail-closed Linux openat2 scratch factory. Actual fixture
/// creation is opt-in on an approved local Linux lab only.
/// </summary>
[TestClass]
public sealed class RecoveryLinuxJournalScratchFactoryTests
{
    private static string Name(string nonce = "a1234567890abcdef1234567890abcde") =>
        nonce + ".gdjournal.tmp";

    private static RecoveryNativeFileId Linux(ulong id, ulong volume = 87) =>
        new(RecoveryNativePlatform.Linux, volume, id, 0);

    private static RecoveryLinuxParentEvidence Parent(
        ulong id = 9, ulong mount = 13, uint owner = 1000,
        ushort mode = 0x41C0) =>
        new(Linux(id), owner, mode, mount);

    private static RecoveryLinuxRegularEntryObservation Created(
        ulong id = 10, ulong mount = 13, uint owner = 1000,
        uint links = 1, ulong volume = 87) =>
        new(Linux(id, volume), mount, owner, links);

    [TestMethod]
    public void CanonicalNonceAndSuffixAccepted()
    {
        Assert.AreEqual(Name(), RecoveryLinuxJournalScratchFactory.ValidateName(Name()));
    }

    [TestMethod]
    public void UppercaseAndMixedCaseNamesRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxJournalScratchFactory.ValidateName(Name().ToUpperInvariant()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxJournalScratchFactory.ValidateName("A" + Name()[1..]));
    }

    [TestMethod]
    public void TraversalSlashBackslashAndEmbeddedNullRejected()
    {
        foreach (string value in new[] { "../" + Name(), "/" + Name(),
                     "dir/" + Name(), "dir\\" + Name(), Name() + "\0" })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryLinuxJournalScratchFactory.ValidateName(value));
    }

    [TestMethod]
    public void DifferentExtensionsAndExtraDotsRejected()
    {
        foreach (string value in new[] { Name()[..32] + ".json",
                     Name() + ".bak", Name()[..32] + ".gdjournal.tmp.",
                     Name()[..32] + ".docx" })
            Assert.ThrowsExactly<ArgumentException>(() =>
                RecoveryLinuxJournalScratchFactory.ValidateName(value));
    }

    [TestMethod]
    public void EmptyNonceAndNonHexRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxJournalScratchFactory.ValidateName(
                new string('0', 32) + ".gdjournal.tmp"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxJournalScratchFactory.ValidateName(
                new string('z', 32) + ".gdjournal.tmp"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxJournalScratchFactory.ValidateName(""));
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxJournalScratchFactory.ValidateName(null!));
    }

    [TestMethod]
    public void OnlyPrivateUnchangedParentAndRegularSameMountFileAcceptedAsObservation()
    {
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.ObservedButNotAuthorized,
            RecoveryLinuxJournalScratchFactory.Evaluate(Parent(), Parent(),
                Created(), 1000));
        Assert.IsFalse(RecoveryLinuxJournalScratchFactory.ProductionCreateEnabled);
        Assert.IsFalse(RecoveryLinuxJournalScratchFactory.CanAuthorizePublish);
        Assert.IsFalse(RecoveryLinuxJournalScratchFactory.CanAuthorizeDelete);
    }

    [TestMethod]
    public void ParentRenameOrReplacementDetectedByHeldIdentity()
    {
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.ParentChanged,
            RecoveryLinuxJournalScratchFactory.Evaluate(Parent(), Parent(id: 44),
                Created(), 1000));
    }

    [TestMethod]
    public void CrossMountAndDeviceChangeRejected()
    {
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.CrossMountOrDevice,
            RecoveryLinuxJournalScratchFactory.Evaluate(Parent(), Parent(),
                Created(mount: 99), 1000));
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.CrossMountOrDevice,
            RecoveryLinuxJournalScratchFactory.Evaluate(Parent(), Parent(),
                Created(volume: 99), 1000));
    }

    [TestMethod]
    public void HardLinkAndAliasedFileIdRejected()
    {
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.IneligibleNewFile,
            RecoveryLinuxJournalScratchFactory.Evaluate(Parent(), Parent(),
                Created(links: 2), 1000));
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.IneligibleNewFile,
            RecoveryLinuxJournalScratchFactory.Evaluate(Parent(), Parent(),
                Created(id: 9), 1000));
    }

    [TestMethod]
    public void WrongOwnerAndInsecureParentRejected()
    {
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.IneligibleParent,
            RecoveryLinuxJournalScratchFactory.Evaluate(
                Parent(owner: 42), Parent(owner: 42), Created(), 1000));
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.IneligibleParent,
            RecoveryLinuxJournalScratchFactory.Evaluate(
                Parent(mode: 0x41F0), Parent(mode: 0x41F0), Created(), 1000));
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.IneligibleNewFile,
            RecoveryLinuxJournalScratchFactory.Evaluate(
                Parent(), Parent(), Created(owner: 77), 1000));
    }

    [TestMethod]
    public void UnknownOrWrongPlatformEvidenceFailsClosed()
    {
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.IneligibleParent,
            RecoveryLinuxJournalScratchFactory.Evaluate(default, default,
                Created(), 1000));
        var foreign = Parent() with {
            FileId = new RecoveryNativeFileId(RecoveryNativePlatform.Windows, 87, 9, 3)
        };
        Assert.AreEqual(RecoveryLinuxJournalScratchFinding.IneligibleParent,
            RecoveryLinuxJournalScratchFactory.Evaluate(foreign, foreign, Created(), 1000));
    }

    [TestMethod]
    public void InvalidParentHandleRejectedBeforeAnyNativeCreate()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryLinuxJournalScratchFactory.CreateExistingParentLab(
                invalid, Name()));
    }

    [TestMethod]
    public void InvalidNameRejectedBeforeNativePlatformOrSyscall()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryLinuxJournalScratchFactory.CreateExistingParentLab(
                invalid, "../attack"));
    }

    [TestMethod]
    public void WindowsCannotUsePathFallback()
    {
        Assert.IsFalse(RecoveryWindowsJournalScratchGate.NativeRelativeCreateReady);
        Assert.IsFalse(RecoveryWindowsJournalScratchGate.CanCreateByPath);
        Assert.IsFalse(RecoveryWindowsJournalScratchGate.CanAuthorizeProductionCreate);
    }

    [TestMethod]
    public void ExplicitOptInLinuxScratchCreateIsExclusiveAndNeverDeletesOnDispose()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("GD31J27CB_NATIVE_LAB") != "1")
            return;

        string dir = Path.Combine(Path.GetTempPath(), "getdoc-cb-lab-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // A default umask might produce 0755; the native lab explicitly
        // requires owner-only 0700 before issuing openat2(O_CREAT|O_EXCL).
        File.SetUnixFileMode(dir, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SafeFileHandle? heldParent = null;
        string scratch = Name(Guid.NewGuid().ToString("N"));
        try
        {
            int raw = OpenDirectory(dir, 0x10000 /* O_DIRECTORY */ |
                0x80000 /* O_CLOEXEC */ | 0 /* O_RDONLY */);
            if (raw < 0) throw new IOException("Fixture open(directory) failed.");
            heldParent = new SafeFileHandle((IntPtr)raw, ownsHandle: true);
            using (var lease = RecoveryLinuxJournalScratchFactory.CreateExistingParentLab(
                heldParent, scratch))
            {
                Assert.AreEqual(0L, lease.Stream.Length);
                Assert.IsFalse(lease.CanAuthorizePublish);
                Assert.IsFalse(lease.CanAuthorizeDelete);
                Assert.ThrowsExactly<IOException>(() =>
                {
                    using var forbidden = RecoveryLinuxJournalScratchFactory
                        .CreateExistingParentLab(heldParent, scratch);
                });
                Assert.AreEqual(RecoveryLinuxJournalScratchFinding.ObservedButNotAuthorized,
                    lease.InspectExistingHandles());
            }
            Assert.IsTrue(File.Exists(Path.Combine(dir, scratch)));
        }
        finally
        {
            heldParent?.Dispose();
            // Test-only cleanup of the unique isolated fixture. The
            // production factory NEVER removes a name or path.
            Directory.Delete(dir, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
}
