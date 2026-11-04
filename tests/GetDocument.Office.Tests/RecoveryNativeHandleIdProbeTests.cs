using System.Buffers.Binary;
using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bp. These test cases are authored but NOT executed in this stage.
/// Real OS probe cases require separate, approved Windows/Astra offline gates.
/// </summary>
[TestClass]
public sealed class RecoveryNativeHandleIdProbeTests
{
    [TestMethod]
    public void LinuxStatxParserUsesExact256ByteLayoutAndDeviceInode()
    {
        byte[] buffer = SyntheticStatx();
        RecoveryNativeFileId id = RecoveryNativeHandleIdProbe.ParseLinuxStatx(buffer);
        Assert.AreEqual(RecoveryNativePlatform.Linux, id.Platform);
        Assert.AreEqual(((ulong)8 << 32) | 3UL, id.DeviceOrVolume);
        Assert.AreEqual(123456789UL, id.ObjectLow);
        Assert.AreEqual(0UL, id.ObjectHigh);
        Assert.IsTrue(id.IsComplete);
    }

    [TestMethod]
    public void LinuxStatxParserRejectsMissingInodeOrMountBits()
    {
        byte[] buffer = SyntheticStatx();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0x101);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeHandleIdProbe.ParseLinuxStatx(buffer));
        buffer = SyntheticStatx();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0x1000);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeHandleIdProbe.ParseLinuxStatx(buffer));
    }

    [TestMethod]
    public void LinuxStatxParserRejectsZeroMountOrZeroDeviceAndSymlinks()
    {
        byte[] buffer = SyntheticStatx();
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(144, 8), 0);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeHandleIdProbe.ParseLinuxStatx(buffer));
        buffer = SyntheticStatx();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(136, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(140, 4), 0);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeHandleIdProbe.ParseLinuxStatx(buffer));
        buffer = SyntheticStatx();
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(28, 2), 0xA000);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeHandleIdProbe.ParseLinuxStatx(buffer));
    }

    [TestMethod]
    public void LinuxStatxParserRejectsIncompleteOrMalformedBuffer()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            RecoveryNativeHandleIdProbe.ParseLinuxStatx(new byte[255]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RecoveryNativeHandleIdProbe.ParseLinuxStatx(new byte[256]));
    }

    [TestMethod]
    public void WindowsFileIdInfoLayoutHasFull128BitIdentifier()
    {
        Assert.AreEqual(24, RecoveryNativeHandleIdProbe.WindowsFileIdInfoBytes);
        Assert.IsTrue(RecoveryNativeHandleIdProbe.WindowsUsesFileIdInfo);
    }

    [TestMethod]
    public void InvalidAndClosedHandlesAreRejectedBeforeNativeCall()
    {
        using var scratch = new Scratch();
        using var handle = File.OpenHandle(scratch.File, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        handle.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(handle));
    }

    [TestMethod]
    public void RepeatedNativeReadOfOneLiveHandleIsStableWithoutPathReopen()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        using var handle = File.OpenHandle(scratch.File, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        RecoveryNativeFileId first =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(handle);
        RecoveryNativeFileId again =
            RecoveryNativeHandleIdProbe.ReadFromExistingHandle(handle);
        Assert.IsTrue(first.IsComplete);
        Assert.AreEqual(first, again);
        Assert.AreEqual("PRESERVE", File.ReadAllText(scratch.File));
    }

    [TestMethod]
    public void TwoLiveHandlesForSameFileReportSameIdentity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        using var first = File.OpenHandle(scratch.File, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = File.OpenHandle(scratch.File, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.AreEqual(RecoveryNativeHandleIdProbe.ReadFromExistingHandle(first),
                        RecoveryNativeHandleIdProbe.ReadFromExistingHandle(second));
    }

    [TestMethod]
    public void DifferentFilesOnOneVolumeHaveDifferentNativeIdentifiers()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        using var scratch = new Scratch();
        string other = Path.Combine(scratch.Root, "other.bin");
        File.WriteAllText(other, "OTHER");
        using var first = File.OpenHandle(scratch.File, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = File.OpenHandle(other, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var one = RecoveryNativeHandleIdProbe.ReadFromExistingHandle(first);
        var two = RecoveryNativeHandleIdProbe.ReadFromExistingHandle(second);
        Assert.AreEqual(one.DeviceOrVolume, two.DeviceOrVolume);
        Assert.AreNotEqual(one, two);
        Assert.AreEqual("PRESERVE", File.ReadAllText(scratch.File));
        Assert.AreEqual("OTHER", File.ReadAllText(other));
    }

    private static byte[] SyntheticStatx()
    {
        byte[] value = new byte[256];
        // Linux struct statx mask, stx_mode, stx_ino, stx_dev_major/minor,
        // stx_mnt_id: fixed published Linux UAPI offsets on little-endian.
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0, 4), 0x1101);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(28, 2), 0x8000);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(32, 8), 123456789);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(136, 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(140, 4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(144, 8), 42);
        return value;
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bp-handle-" + Guid.NewGuid().ToString("N"));
        internal string File => Path.Combine(Root, "candidate.bin");

        internal Scratch()
        {
            Directory.CreateDirectory(Root);
            System.IO.File.WriteAllText(File, "PRESERVE");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
