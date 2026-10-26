using GetDocument.Core.Printing;
using GetDocument.Desktop.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Printing.Tests;

/// <summary>
/// Exercises the SAME LocalPrintBroker process/argv code that ships in GET
/// Document. Fake CUPS commands are placed first on PATH only inside this
/// isolated test process; no real queue, network or printer is ever touched.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LocalCupsSimulatorTests
{
    [TestMethod]
    public async Task DiscoversQueuesDefaultAndRealDriverBins()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        var queues = await LocalPrintBroker.PrintersAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "GET_SIMULATOR" }, queues.ToArray());
        Assert.AreEqual("GET_SIMULATOR",
            await LocalPrintBroker.DefaultPrinterAsync(CancellationToken.None));

        var trays = await LocalPrintBroker.TraysAsync("GET_SIMULATOR", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Auto", "Tray1" },
            trays.Select(x => x.Value).ToArray());
    }

    [TestMethod]
    public async Task SubmitsExactArgumentVectorWithUnicodePdfPathAndFrozenDigest()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        using var frozen = await cups.CreateSnapshotAsync("ПТО Шаврова — печать.pdf");
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 3, "2-3", 2,
            PrintDuplex.OneSided, PrintPaper.A4, PrintOrientation.Portrait,
            PrintColorMode.Monochrome, PrintPagesPerSheet.One,
            PrintTrayOptions.Make("InputSlot", "Tray1"));
        string response = await LocalPrintBroker.SubmitPdfAsync(
            frozen.Path, plan, CancellationToken.None, frozen.Sha256Hex);
        StringAssert.Contains(response, "очередь CUPS");
        string[] args = await cups.ReadArgumentsAsync();
        CollectionAssert.AreEqual(new[]
        {
            "-d", "GET_SIMULATOR", "-n", "2", "-P", "2-3",
            "-o", "media=A4", "-o", "sides=one-sided",
            "-o", "orientation-requested=3",
            "-o", "print-color-mode=monochrome",
            "-o", "InputSlot=Tray1",
            "--", frozen.Path
        }, args);
    }

    [TestMethod]
    public async Task FullDocumentFourUpOmitsOutputPageRange()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 8, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4,
            pagesPerSheet: PrintPagesPerSheet.Four);
        await LocalPrintBroker.SubmitPdfAsync(frozen.Path, plan,
            CancellationToken.None, frozen.Sha256Hex);
        string[] args = await cups.ReadArgumentsAsync();
        CollectionAssert.Contains(args, "number-up=4");
        CollectionAssert.DoesNotContain(args, "-P");
    }

    [TestMethod]
    public async Task DriverWithoutSelectedTrayRejectsBeforeSubmitting()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4,
            tray: PrintTrayOptions.Make("InputSlot", "Tray2"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LocalPrintBroker.SubmitPdfAsync(frozen.Path, plan,
                CancellationToken.None, frozen.Sha256Hex));
        Assert.IsFalse(File.Exists(cups.ArgumentsFile),
            "No lp command should run when a tray is no longer advertised.");
    }

    [TestMethod]
    public async Task ModifiedSnapshotIsBlockedBeforePrinterDiscoveryOrLp()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        await File.WriteAllTextAsync(frozen.Path, "%PDF-1.7\\nchanged");
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LocalPrintBroker.SubmitPdfAsync(frozen.Path, plan,
                CancellationToken.None, frozen.Sha256Hex));
        Assert.IsFalse(File.Exists(cups.ArgumentsFile));
    }

    [TestMethod]
    public async Task QueueDisappearsBeforeSubmissionAndNeverCallsLp()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        cups.HideQueue();
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LocalPrintBroker.SubmitPdfAsync(frozen.Path, plan,
                CancellationToken.None, frozen.Sha256Hex));
        Assert.IsFalse(File.Exists(cups.ArgumentsFile));
    }

    [TestMethod]
    public async Task UnadvertisedColorIsDeniedBeforeSpooling()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        cups.DenyColor();
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4, color: PrintColorMode.Color);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LocalPrintBroker.SubmitPdfAsync(frozen.Path, plan,
                CancellationToken.None, frozen.Sha256Hex));
        Assert.IsFalse(File.Exists(cups.ArgumentsFile));
    }

    [TestMethod]
    public async Task AlreadyCancelledPrintCannotReachAnyPrinterCommand()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            LocalPrintBroker.SubmitPdfAsync(frozen.Path, plan,
                cancellation.Token, frozen.Sha256Hex));
        Assert.IsFalse(File.Exists(cups.ArgumentsFile));
    }

    [TestMethod]
    public async Task A3AndDuplexAreRejectedWhenQueueDoesNotAdvertiseThem()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        var a3 = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A3);
        var duplex = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.LongEdge, PrintPaper.A4);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LocalPrintBroker.SubmitPdfAsync(frozen.Path, a3,
                CancellationToken.None, frozen.Sha256Hex));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LocalPrintBroker.SubmitPdfAsync(frozen.Path, duplex,
                CancellationToken.None, frozen.Sha256Hex));
        Assert.IsFalse(File.Exists(cups.ArgumentsFile));
    }

    [TestMethod]
    public async Task AdvertisedA3AndDuplexReachLpWithExactDeviceOptions()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        cups.EnableA3AndDuplex();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.LongEdge, PrintPaper.A3);
        await LocalPrintBroker.SubmitPdfAsync(frozen.Path, plan,
            CancellationToken.None, frozen.Sha256Hex);
        string[] args = await cups.ReadArgumentsAsync();
        CollectionAssert.Contains(args, "media=A3");
        CollectionAssert.Contains(args, "sides=two-sided-long-edge");
    }

    [TestMethod]
    public async Task FailedCupsCommandIsAmbiguousAndMustNotClaimPrinted()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Fake CUPS executable tests require Linux.");

        using var cups = new FakeCups();
        cups.RejectPrint();
        using var frozen = await cups.CreateSnapshotAsync("test.pdf");
        var plan = PrintJobPlan.Create("GET_SIMULATOR", 1, "", 1,
            PrintDuplex.OneSided, PrintPaper.A4);
        var ex = await Assert.ThrowsExactlyAsync<PrintSubmissionException>(() =>
            LocalPrintBroker.SubmitPdfAsync(frozen.Path, plan,
                CancellationToken.None, frozen.Sha256Hex));
        Assert.AreEqual(PrintFailureKind.Indeterminate, ex.Failure.Kind);
        Assert.IsTrue(ex.Failure.MightBeQueued);
        StringAssert.Contains(ex.Message, "очеред");
        Assert.IsTrue(File.Exists(cups.ArgumentsFile),
            "The fake lp process did run and rejected the job.");
    }

    private sealed class FakeCups : IDisposable
    {
        private readonly string _originalPath;
        private readonly string? _previousArgsPath;
        private readonly string? _previousReject;
        private readonly string? _previousNoQueue;
        private readonly string? _previousNoColor;
        private readonly string? _previousFullCaps;
        private readonly string _folder;

        public FakeCups()
        {
            _folder = Path.Combine(Path.GetTempPath(), "gd22-fake-cups-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            ArgumentsFile = Path.Combine(_folder, "received-argv.txt");

            // Executed directly via ProcessStartInfo.ArgumentList, never using a shell.
            WriteExecutable("lpstat", """
                #!/bin/sh
                case "$1" in
                  -e) if [ "${GETDOC_FAKE_CUPS_NO_QUEUE:-0}" != "1" ]; then printf 'GET_SIMULATOR\n'; fi ;;
                  -d) printf 'system default destination: GET_SIMULATOR\n' ;;
                  -p) printf 'printer GET_SIMULATOR is idle.\n' ;;
                  *) exit 2 ;;
                esac
                """);
            WriteExecutable("lpoptions", """
                #!/bin/sh
                if [ "$1" != "-p" ] || [ "$2" != "GET_SIMULATOR" ] || [ "$3" != "-l" ]; then exit 2; fi
                printf 'InputSlot/Paper Source: *Auto Tray1\n'
                if [ "${GETDOC_FAKE_CUPS_FULL_CAPS:-0}" = "1" ]; then
                  printf 'PageSize/Page Size: *A4 A3\n'
                  printf 'Duplex/Two-sided: *None DuplexNoTumble DuplexTumble\n'
                else
                  printf 'PageSize/Page Size: *A4\n'
                  printf 'Duplex/Two-sided: *None\n'
                fi
                if [ "${GETDOC_FAKE_CUPS_NO_COLOR:-0}" != "1" ]; then
                  printf 'print-color-mode/Color Mode: *monochrome color\n'
                fi
                """);
            WriteExecutable("lp", """
                #!/bin/sh
                printf '%s\n' "$@" > "$GETDOC_FAKE_CUPS_ARGS"
                if [ "$GETDOC_FAKE_CUPS_REJECT" = "1" ]; then exit 1; fi
                printf 'request id is GET_SIMULATOR-42 (1 file(s))\n'
                """);
            _originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            _previousArgsPath = Environment.GetEnvironmentVariable("GETDOC_FAKE_CUPS_ARGS");
            _previousReject = Environment.GetEnvironmentVariable("GETDOC_FAKE_CUPS_REJECT");
            _previousNoQueue = Environment.GetEnvironmentVariable("GETDOC_FAKE_CUPS_NO_QUEUE");
            _previousNoColor = Environment.GetEnvironmentVariable("GETDOC_FAKE_CUPS_NO_COLOR");
            _previousFullCaps = Environment.GetEnvironmentVariable("GETDOC_FAKE_CUPS_FULL_CAPS");
            Environment.SetEnvironmentVariable("PATH", _folder + Path.PathSeparator + _originalPath);
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_ARGS", ArgumentsFile);
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_REJECT", "0");
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_NO_QUEUE", "0");
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_NO_COLOR", "0");
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_FULL_CAPS", "0");
        }

        public string ArgumentsFile { get; }

        public void RejectPrint() =>
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_REJECT", "1");

        public void HideQueue() =>
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_NO_QUEUE", "1");

        public void DenyColor() =>
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_NO_COLOR", "1");

        public void EnableA3AndDuplex() =>
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_FULL_CAPS", "1");

        public async Task<PrintFileSnapshot> CreateSnapshotAsync(string name)
        {
            string source = Path.Combine(_folder, name);
            await File.WriteAllTextAsync(source, "%PDF-1.7\nsynthetic-smoke-fixture");
            return await PrintFileSnapshot.CreateAsync(source, CancellationToken.None);
        }

        public async Task<string[]> ReadArgumentsAsync() =>
            (await File.ReadAllLinesAsync(ArgumentsFile))
                .Select(x => x.TrimEnd('\r')).ToArray();

        private void WriteExecutable(string name, string contents)
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllText(path, contents.Replace("\r\n", "\n"));
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_ARGS", _previousArgsPath);
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_REJECT", _previousReject);
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_NO_QUEUE", _previousNoQueue);
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_NO_COLOR", _previousNoColor);
            Environment.SetEnvironmentVariable("GETDOC_FAKE_CUPS_FULL_CAPS", _previousFullCaps);
            try { Directory.Delete(_folder, recursive: true); }
            catch (IOException) { }
        }
    }
}
