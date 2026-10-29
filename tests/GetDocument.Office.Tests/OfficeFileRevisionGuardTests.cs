using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

/// <summary>Fast, local file tests; no LibreOffice, network, Qt or CEF required.</summary>
[TestClass]
public sealed class OfficeFileRevisionGuardTests
{
    [TestMethod]
    public async Task OriginalDocumentUnchangedBetweenOpenAndSaveIsAccepted()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        await guard.EnsureUnchangedAsync();
    }

    [TestMethod]
    public async Task RejectsExternalEditEvenWhenLengthAndTimestampAreRestored()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        var timestamp = File.GetLastWriteTimeUtc(fixture.Source);
        await File.WriteAllTextAsync(fixture.Source, "REPLACED"); // same length as ORIGINAL
        File.SetLastWriteTimeUtc(fixture.Source, timestamp);
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => guard.EnsureUnchangedAsync());
        Assert.AreEqual("REPLACED", await File.ReadAllTextAsync(fixture.Source));
    }

    [TestMethod]
    public async Task RejectsExternalAtomicReplacement()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        string staged = Path.Combine(fixture.Root, "new.docx");
        await File.WriteAllTextAsync(staged, "NEWDATA!");
        File.Move(staged, fixture.Source, overwrite: true);
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => guard.EnsureUnchangedAsync());
    }

    [TestMethod]
    public async Task RejectsDeletedOriginalRatherThanRecreatingIt()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        File.Delete(fixture.Source);
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => guard.EnsureUnchangedAsync());
        Assert.IsFalse(File.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task AcceptsNextRevisionOnlyAfterExplicitSuccessfulSaveConfirmation()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        await File.WriteAllTextAsync(fixture.Source, "OUR SAVED DATA");
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => guard.EnsureUnchangedAsync());
        // Production callers only invoke this AFTER the UNO store() succeeds.
        await guard.AcceptSavedAsync(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(fixture.Source))));
        await guard.EnsureUnchangedAsync();
        await File.WriteAllTextAsync(fixture.Source, "COLLEAGUE DATA");
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => guard.EnsureUnchangedAsync());
    }

    [TestMethod]
    public async Task StaleOriginalNeverCallsTheUnoSaveCallback()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        var save = new OfficeRevisionSaveCoordinator(guard);
        await File.WriteAllTextAsync(fixture.Source, "COLLEAGUE DATA");
        int invocations = 0;
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => save.SaveAsync(_ =>
            {
                invocations++;
                return Task.FromResult(string.Empty);
            }));
        Assert.AreEqual(0, invocations);
        Assert.AreEqual("COLLEAGUE DATA", await File.ReadAllTextAsync(fixture.Source));
    }

    [TestMethod]
    public async Task FailedNativeStoreDoesNotRefreshExpectedRevision()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        var save = new OfficeRevisionSaveCoordinator(guard);
        await Assert.ThrowsExactlyAsync<IOException>(() => save.SaveAsync(async _ =>
        {
            // A failing native write may have partially changed the file.
            await File.WriteAllTextAsync(fixture.Source, "PARTIAL!");
            throw new IOException("Simulated UNO store failure");
        }));
        int invocations = 0;
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => save.SaveAsync(_ =>
            {
                invocations++;
                return Task.FromResult(string.Empty);
            }));
        Assert.AreEqual(0, invocations);
    }

    [TestMethod]
    public async Task SuccessfulNativeSaveAdvancesExpectedRevision()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        var save = new OfficeRevisionSaveCoordinator(guard);
        await save.SaveAsync(async _ =>
        {
            await File.WriteAllTextAsync(fixture.Source, "SAVED ONCE");
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(fixture.Source)));
        });
        await save.SaveAsync(async _ =>
        {
            await File.WriteAllTextAsync(fixture.Source, "SAVED TWICE");
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(fixture.Source)));
        });
        Assert.AreEqual("SAVED TWICE", await File.ReadAllTextAsync(fixture.Source));
    }

    [TestMethod]
    public async Task RejectsNativeReceiptForDifferentFileEvenAfterStoreReturnedSuccess()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        var save = new OfficeRevisionSaveCoordinator(guard);
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => save.SaveAsync(async expected =>
            {
                Assert.AreEqual(64, expected.Length);
                await File.WriteAllTextAsync(fixture.Source, "SAVED WITH RACE");
                return new string('A', 64); // valid shape, wrong content
            }));
        await Assert.ThrowsExactlyAsync<OfficeFileConflictException>(
            () => guard.EnsureUnchangedAsync());
    }

    [TestMethod]
    public async Task RejectsMissingNativeReceiptRatherThanAcceptingStaleData()
    {
        using var fixture = new Fixture();
        var guard = await OfficeFileRevisionGuard.CreateAsync(fixture.Source);
        var save = new OfficeRevisionSaveCoordinator(guard);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => save.SaveAsync(_ => Task.FromResult("")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "getdoc-revision-guard-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "memo.docx");

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Source, "ORIGINAL"); // eight ASCII bytes
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
