using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>
/// GD-31J.27bc: deterministic synthetic path changes between the
/// preliminary FileInfo inspection and the one FileStream.Open.
/// This does NOT exercise native openat(O_NOFOLLOW) / NT handle identity.
/// </summary>
[TestClass]
public sealed class OfficeRecoveryPreOpenIdentityTests
{
    [TestMethod]
    public void UnchangedRegularCopyOpensReadOnlyThroughTheGuard()
    {
        using var scratch = new Scratch();
        string candidate = scratch.CreateFile("candidate.docx", "ORDINARY_TEST_BYTES");
        int injected = 0;

        using var input = OfficeRecoveryStore.OpenRecoveryReadStreamForTest(
            candidate, _ => injected++);

        Assert.AreEqual(1, injected);
        Assert.IsTrue(input.CanRead);
        Assert.IsTrue(input.CanSeek);
        Assert.IsFalse(input.CanWrite);
        Assert.AreEqual((int)'O', input.ReadByte());
        Assert.AreEqual("ORDINARY_TEST_BYTES", File.ReadAllText(candidate));
    }

    [TestMethod]
    public void SymlinkInsertedAfterPreflightIsRejectedEvenWithIdenticalTargetBytes()
    {
        using var scratch = new Scratch();
        string original = scratch.CreateFile("candidate.docx", "SAME_VALID_TEST_BYTES");
        string unrelated = scratch.CreateFile("outside.docx", "SAME_VALID_TEST_BYTES");
        byte[] outsideBytes = File.ReadAllBytes(unrelated);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeRecoveryStore.OpenRecoveryReadStreamForTest(original, path =>
            {
                // A preflight based on the original regular file already passed.
                // The *same-content* symlink is then installed before FileStream.Open.
                File.Delete(path);
                File.CreateSymbolicLink(path, unrelated);
            }));

        Assert.IsNotNull(new FileInfo(original).LinkTarget);
        CollectionAssert.AreEqual(outsideBytes, File.ReadAllBytes(unrelated));
    }

    [TestMethod]
    public void RegularFileRewrittenAfterPreflightIsRejectedWithoutReading()
    {
        using var scratch = new Scratch();
        string candidate = scratch.CreateFile("candidate.xlsx", "SHORT");
        string replacement = "REWRITTEN_FILE_WITH_MORE_BYTES";

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OfficeRecoveryStore.OpenRecoveryReadStreamForTest(candidate,
                path => File.WriteAllText(path, replacement)));

        Assert.AreEqual(replacement, File.ReadAllText(candidate));
    }

    [TestMethod]
    public void FailedBeforeOpenHookLeavesTheOriginalUntouched()
    {
        using var scratch = new Scratch();
        string candidate = scratch.CreateFile("candidate.pptx", "KEEP_ORIGINAL_BYTES");
        byte[] original = File.ReadAllBytes(candidate);

        Assert.ThrowsExactly<IOException>(() =>
            OfficeRecoveryStore.OpenRecoveryReadStreamForTest(candidate,
                _ => throw new IOException("Synthetic pre-open IO error")));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(candidate));
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "getdoc-gd31j27bc-" + Guid.NewGuid().ToString("N"));

        public Scratch() => Directory.CreateDirectory(_root);

        public string CreateFile(string name, string content)
        {
            string file = Path.Combine(_root, name);
            File.WriteAllText(file, content);
            return file;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
