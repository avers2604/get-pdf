using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeNativeSessionCoordinatorTests
{
    [TestMethod]
    public async Task ConnectsAndReportsDiagnosticStatusWithoutGrantingDocumentRights()
    {
        var ui = new FakeView();
        await using var coordinator = new EuroOfficeNativeSessionCoordinator(ui);
        var transport = new FakeTransport();
        Guid tab = Guid.NewGuid(), session = Guid.NewGuid();
        await coordinator.StartAsync(tab, session, "test-only", "Лаборатория",
            transport, labApproved: true, CancellationToken.None);
        Assert.AreEqual(1, transport.Attaches);
        Assert.AreEqual(1, ui.Opened);
        Assert.IsTrue(ui.Connected);
        await coordinator.ResizeAsync(tab, session, 1200, 800, 1.5, CancellationToken.None);
        Assert.AreEqual(1, transport.Resizes);
        await coordinator.CloseAsync(tab, session, confirmedDiscard: false, CancellationToken.None);
        Assert.AreEqual(1, transport.Closes);
        Assert.AreEqual(1, transport.DisposeCalls);
        Assert.AreEqual(1, ui.Removed);
        Assert.AreEqual(0, ui.Failures);
    }

    [TestMethod]
    public async Task RefusesUnapprovedHostBeforeAnyIpc()
    {
        var ui = new FakeView();
        await using var coordinator = new EuroOfficeNativeSessionCoordinator(ui);
        var transport = new FakeTransport();
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            coordinator.StartAsync(Guid.NewGuid(), Guid.NewGuid(), "test", "Test", transport,
                labApproved: false, CancellationToken.None));
        Assert.AreEqual(0, transport.Attaches);
        Assert.AreEqual(0, ui.Opened);
    }

    [TestMethod]
    public async Task CrashReportsUiFailureAndNeverReopensAutomatically()
    {
        var ui = new FakeView();
        await using var coordinator = new EuroOfficeNativeSessionCoordinator(ui);
        var transport = new FakeTransport();
        Guid tab = Guid.NewGuid(), session = Guid.NewGuid();
        await coordinator.StartAsync(tab, session, "test", "Test", transport,
            labApproved: true, CancellationToken.None);
        transport.SignalExit();
        await coordinator.ObserveHostExitAsync(tab, session, CancellationToken.None);
        Assert.AreEqual(1, ui.Failures);
        Assert.AreEqual(0, ui.Removed);
        Assert.AreEqual(1, ui.Opened);
        Assert.ThrowsExactly<NotSupportedException>(() => coordinator.AuthorizeDocumentOperations());
        await coordinator.CloseAsync(tab, session, confirmedDiscard: false, CancellationToken.None);
        Assert.AreEqual(1, ui.Removed);
    }

    [TestMethod]
    public async Task UnverifiedChangesRequireExplicitDiscardBeforeRemoval()
    {
        var ui = new FakeView { UnverifiedChanges = true };
        await using var coordinator = new EuroOfficeNativeSessionCoordinator(ui);
        var transport = new FakeTransport();
        Guid tab = Guid.NewGuid(), session = Guid.NewGuid();
        await coordinator.StartAsync(tab, session, "test", "Test", transport, true,
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.CloseAsync(tab, session, confirmedDiscard: false, CancellationToken.None));
        Assert.AreEqual(0, transport.Closes);
        Assert.AreEqual(0, ui.Removed);
        await coordinator.CloseAsync(tab, session, confirmedDiscard: true, CancellationToken.None);
        Assert.AreEqual(1, ui.Removed);
    }

    [TestMethod]
    public async Task StaleHostIdCannotResizeOrCloseNewSession()
    {
        var ui = new FakeView();
        await using var coordinator = new EuroOfficeNativeSessionCoordinator(ui);
        var transport = new FakeTransport();
        Guid tab = Guid.NewGuid(), session = Guid.NewGuid();
        await coordinator.StartAsync(tab, session, "test", "Test", transport, true,
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            coordinator.ResizeAsync(tab, Guid.NewGuid(), 800, 600, 1, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            coordinator.CloseAsync(tab, Guid.NewGuid(), false, CancellationToken.None));
        Assert.AreEqual(0, ui.Removed);
    }

    [TestMethod]
    public async Task FailedAttachDisposesProcessAndDoesNotExposeTab()
    {
        var ui = new FakeView();
        await using var coordinator = new EuroOfficeNativeSessionCoordinator(ui);
        var transport = new FakeTransport { FailAttach = true };
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            coordinator.StartAsync(Guid.NewGuid(), Guid.NewGuid(), "test", "Test", transport,
                true, CancellationToken.None));
        Assert.AreEqual(1, transport.DisposeCalls);
        Assert.AreEqual(0, ui.Opened);
    }

    [TestMethod]
    public async Task LateExitFromPreviouslyClosedSessionDoesNotDamageOtherTab()
    {
        var ui = new FakeView();
        await using var coordinator = new EuroOfficeNativeSessionCoordinator(ui);
        var a = new FakeTransport();
        var b = new FakeTransport();
        Guid ta = Guid.NewGuid(), tb = Guid.NewGuid(), sa = Guid.NewGuid(), sb = Guid.NewGuid();
        await coordinator.StartAsync(ta, sa, "a", "A", a, true, CancellationToken.None);
        await coordinator.StartAsync(tb, sb, "b", "B", b, true, CancellationToken.None);
        await coordinator.CloseAsync(ta, sa, false, CancellationToken.None);
        await coordinator.ObserveHostExitAsync(ta, sa, CancellationToken.None);
        Assert.AreEqual(0, ui.Failures);
        await coordinator.ResizeAsync(tb, sb, 1024, 768, 1, CancellationToken.None);
        Assert.AreEqual(1, b.Resizes);
    }

    private sealed class FakeView : IEuroOfficeNativeSessionView
    {
        public int Opened, Removed, Failures;
        public bool Connected, UnverifiedChanges;
        public Task OpenAsync(Guid tab, Guid session, string document, string title, CancellationToken ct)
        { ++Opened; return Task.CompletedTask; }
        public Task ConnectedAsync(Guid tab, Guid session, CancellationToken ct)
        { Connected = true; return Task.CompletedTask; }
        public Task HostLostAsync(Guid tab, Guid session, CancellationToken ct)
        { ++Failures; return Task.CompletedTask; }
        public Task<bool> HasUnverifiedChangesAsync(Guid tab, Guid session, CancellationToken ct)
            => Task.FromResult(UnverifiedChanges);
        public Task RemoveAsync(Guid tab, Guid session, CancellationToken ct)
        { ++Removed; return Task.CompletedTask; }
    }

    private sealed class FakeTransport : IEuroOfficeNativeSessionTransport
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attaches, Resizes, Closes, DisposeCalls;
        public bool FailAttach;
        public Task AttachAsync(CancellationToken ct)
        {
            ++Attaches;
            return FailAttach ? Task.FromException(new IOException("fixture")) : Task.CompletedTask;
        }
        public Task ResizeAsync(int width, int height, double scale, CancellationToken ct)
        { ++Resizes; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken ct)
        { ++Closes; _exit.TrySetResult(); return Task.CompletedTask; }
        public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);
        public void SignalExit() => _exit.TrySetResult();
        public ValueTask DisposeAsync()
        { ++DisposeCalls; _exit.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
