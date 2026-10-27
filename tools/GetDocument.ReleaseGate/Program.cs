using System.Text.Json;

namespace GetDocument.ReleaseGate;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
            return ReleaseGateSelfTests.Run();

        if (args.Length != 2 || (args[1] != "linux" && args[1] != "windows") ||
            !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine(
                "Usage: GetDocument.ReleaseGate <trx-directory> <linux|windows> | --self-test");
            return 2;
        }

        string directory = Path.GetFullPath(args[0]);
        string platform = args[1];
        // An OS-specific sign-off is invalid if the reports were generated
        // by a different operating system, even when TRX filenames match.
        if ((platform == "windows" && !OperatingSystem.IsWindows()) ||
            (platform == "linux" && !OperatingSystem.IsLinux()))
        {
            Console.Error.WriteLine("RELEASE BLOCKED: target OS does not match laboratory OS.");
            return 2;
        }
        var suites = new List<SuiteResult>();

        foreach (string suite in ReleaseReportEvaluator.ExpectedSuites(platform))
        {
            SuiteResult report = ReleaseReportEvaluator.EvaluateFile(suite,
                platform, Path.Combine(directory, suite + ".trx"));
            suites.Add(report);
            Console.WriteLine($"{(report.Approved ? "PASS" : "FAIL")} {suite}: " +
                $"passed={report.Passed}, failed={report.Failed}, other={report.Other}, " +
                $"required-not-passed={report.MissingRequired.Count}, " +
                $"integrity-errors={report.IntegrityErrors.Count}");
            foreach (string name in report.MissingRequired)
                Console.Error.WriteLine("  required test not passed: " + name);
            foreach (string message in report.IntegrityErrors)
                Console.Error.WriteLine("  invalid report: " + message);
        }

        bool independentRuns = ReleaseReportEvaluator.AreIndependentRuns(suites);
        if (!independentRuns)
            Console.Error.WriteLine("RELEASE BLOCKED: duplicate or absent TRX TestRun IDs.");
        bool approved = suites.Count ==
            ReleaseReportEvaluator.ExpectedSuites(platform).Count &&
            independentRuns && suites.All(x => x.Approved);
        var summary = new
        {
            Platform = platform,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            RuntimeVersion = Environment.Version.ToString(),
            Approved = approved, // Backward-compatible automated gate field only.
            AutomatedTestsApproved = approved,
            IndependentRuns = independentRuns,
            HardwareAcceptance = "NotVerified",
            ReleaseAuthorized = false, // Human hardware/WORM acceptance is separate.
            Suites = suites,
            RequiredSuiteCount = ReleaseReportEvaluator.ExpectedSuites(platform).Count
        };

        // Atomic replace prevents another local tool from mistaking half-written
        // release evidence for a successfully completed acceptance run.
        string destination = Path.Combine(directory, "get-document-release-gate.json");
        string temp = Path.Combine(directory, "get-document-release-gate-" +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp,
                JsonSerializer.Serialize(summary, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        if (!approved)
            Console.Error.WriteLine(
                "RELEASE BLOCKED: missing, malformed, inconclusive or failed mandatory tests.");
        else
            Console.WriteLine("PASS: automated test reports verified. " +
                "Release still requires physical MFP and Office GUI acceptance.");
        return approved ? 0 : 1;
    }
}
