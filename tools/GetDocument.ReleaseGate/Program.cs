using System.Text.Json;
using System.Xml.Linq;

namespace GetDocument.ReleaseGate;

/// <summary>
/// No NuGet packages, network, test framework or printer required. Examines
/// reports produced by the already-executed MSTest.Sdk/MTP test applications.
/// Fails if critical print regression tests are missing or inconclusive.
/// </summary>
internal static class Program
{
    private sealed record SuiteResult(string Suite, int Passed, int Failed, int Other,
        IReadOnlyList<string> MissingRequired);

    private static readonly string[] AllSuites =
    [
        "Core", "Office", "Printing", "Images", "Revisions",
        "Collaboration", "WopiGateway"
    ];

    private static int Main(string[] args)
    {
        if (args.Length != 2 ||
            (args[1] != "linux" && args[1] != "windows") ||
            !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: GetDocument.ReleaseGate <trx-directory> <linux|windows>");
            return 2;
        }

        string platform = args[1];
        var suites = platform == "windows"
            ? AllSuites.Append("LegacyPdf")
            : AllSuites.AsEnumerable();
        var reports = new List<SuiteResult>();
        bool success = true;
        foreach (string suite in suites)
        {
            var path = Path.Combine(args[0], suite + ".trx");
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"FAIL: missing {suite}.trx");
                success = false;
                continue;
            }
            try
            {
                var doc = XDocument.Load(path, LoadOptions.None);
                var results = doc.Descendants()
                    .Where(node => node.Name.LocalName == "UnitTestResult")
                    .Select(node => (
                        Name: (string?)node.Attribute("testName") ?? "",
                        Outcome: (string?)node.Attribute("outcome") ?? "Unknown"
                    )).ToArray();

                int passed = results.Count(x =>
                    x.Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase));
                int failed = results.Count(x =>
                    x.Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                    x.Outcome.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                    x.Outcome.Equals("Timeout", StringComparison.OrdinalIgnoreCase) ||
                    x.Outcome.Equals("Aborted", StringComparison.OrdinalIgnoreCase));
                int other = results.Length - passed - failed;

                string[] required = suite switch
                {
                    "Core" =>
                    [
                        "SnapshotPrintBytesStayUnchangedAfterOriginalIsEdited",
                        "TamperedStagedPdfIsRejected",
                        "CupsReceiptIsBoundToExpectedQueueAndNumericJobId",
                        "UsesArgumentListAndDoesNotInvokeShellOrPrinterDialog"
                    ],
                    "Printing" when platform == "linux" =>
                    [
                        "SubmitsExactArgumentVectorWithUnicodePdfPathAndFrozenDigest",
                        "FullDocumentFourUpOmitsOutputPageRange",
                        "MissingLocalLpIsDefiniteFailureBeforeSubmission",
                        "ExcessivePrinterOutputIsRejectedWithoutUnboundedBuffering",
                        "UnboundedDriverOutputIsFullyDrainedButNeverBuffered"
                    ],
                    "Printing" => ["UnboundedDriverOutputIsFullyDrainedButNeverBuffered"],
                    "Office" when platform == "windows" =>
                    [
                        "UnsavedOfficeEditsAreFrozenIntoPdfSnapshotForGetPrintWizard"
                    ],
                    "Office" => ["SaveCopyFiltersFollowTheTargetExtension"],
                    _ => []
                };

                string[] missing = required.Where(name => !results.Any(result =>
                    result.Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase) &&
                    result.Name.Contains(name, StringComparison.Ordinal))).ToArray();

                reports.Add(new SuiteResult(suite, passed, failed, other, missing));
                bool ok = passed > 0 && failed == 0 && missing.Length == 0;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {suite}: " +
                    $"passed={passed}, failed={failed}, other={other}, " +
                    $"missing-critical={missing.Length}");
                foreach (var name in missing)
                    Console.Error.WriteLine("  critical test not passed: " + name);
                if (!ok) success = false;
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException
                or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine($"FAIL: unreadable {suite}.trx ({ex.GetType().Name})");
                success = false;
            }
        }
        var summary = new
        {
            Platform = platform,
            Approved = success,
            Suites = reports,
            RequiredSuiteCount = platform == "windows"
                ? AllSuites.Length + 1 : AllSuites.Length
        };
        File.WriteAllText(Path.Combine(args[0], "get-document-release-gate.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        if (!success)
            Console.Error.WriteLine(
                "RELEASE BLOCKED: a required test/report failed, was skipped or did not run.");
        else
            Console.WriteLine("PASS: mandatory automated release tests completed. " +
                              "Physical MFP/Office GUI acceptance still required.");
        return success ? 0 : 1;
    }
}
