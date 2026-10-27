using System.Xml;
using System.Xml.Linq;

namespace GetDocument.ReleaseGate;

internal sealed record TestOutcome(string Name, string Status);

internal sealed record SuiteResult(
    string Suite, int Passed, int Failed, int Other,
    IReadOnlyList<string> MissingRequired, IReadOnlyList<string> IntegrityErrors,
    Guid? RunId = null)
{
    public bool Approved => Passed > 0 && Failed == 0 &&
                            MissingRequired.Count == 0 && IntegrityErrors.Count == 0;
}

internal static class ReleaseReportEvaluator
{
    private const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private const long MaxReportBytes = 32L * 1024 * 1024;

    internal static readonly string[] Suites =
    [
        "Core", "Office", "Printing", "Images", "Revisions",
        "Collaboration", "WopiGateway"
    ];

    internal static IReadOnlyList<string> ExpectedSuites(string platform) =>
        platform == "windows" ? [.. Suites, "LegacyPdf"] : Suites;

    internal static SuiteResult EvaluateFile(string suite, string platform, string file)
    {
        if (!File.Exists(file))
            return FailedReport(suite, platform, "Отсутствует TRX-отчёт.");
        try
        {
            var info = new FileInfo(file);
            if (info.Length is <= 0 or > MaxReportBytes)
                return FailedReport(suite, platform, "TRX имеет недопустимый размер.");

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxReportBytes,
                MaxCharactersFromEntities = 4096, // DTD is prohibited; ordinary XML escapes are allowed.
                IgnoreComments = true
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            return EvaluateDocument(suite, platform, document);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException)
        {
            return FailedReport(suite, platform, "Повреждённый или недоступный TRX.");
        }
    }

    internal static SuiteResult EvaluateDocument(string suite, string platform, XDocument document)
    {
        var errors = new List<string>();
        if (document.Root?.Name != XName.Get("TestRun", TrxNamespace))
            return FailedReport(suite, platform, "Корневой элемент не является стандартным TestRun TRX.");

        Guid? runId = Guid.TryParse((string?)document.Root!.Attribute("id"),
            out Guid parsedId) && parsedId != Guid.Empty ? parsedId : null;
        if (runId is null)
            errors.Add("У TRX отсутствует допустимый идентификатор TestRun.");

        XNamespace ns = TrxNamespace;
        var resultsRoot = document.Root.Element(ns + "Results");
        var summary = document.Root.Element(ns + "ResultSummary");
        var counters = summary?.Element(ns + "Counters");

        if (resultsRoot is null) errors.Add("Отсутствует раздел Results.");
        if (summary is null || counters is null)
            errors.Add("Отсутствует ResultSummary/Counters.");
        string summaryOutcome = ((string?)summary?.Attribute("outcome") ?? "").Trim();
        if (!summaryOutcome.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
            !summaryOutcome.Equals("Passed", StringComparison.OrdinalIgnoreCase))
            errors.Add("ResultSummary не подтверждает успешное завершение тестов.");

        var results = resultsRoot?.Elements(ns + "UnitTestResult").Select(element =>
            new TestOutcome(
                ((string?)element.Attribute("testName") ?? "").Trim(),
                ((string?)element.Attribute("outcome") ?? "Unknown").Trim()
            )).ToArray() ?? [];

        if (results.Length == 0) errors.Add("Нет результатов UnitTestResult.");
        if (results.Any(x => x.Name.Length == 0))
            errors.Add("TRX содержит тесты без идентификатора.");
        string[] knownOutcomes = ["Passed", "Failed", "Error", "Timeout", "Aborted",
            "Inconclusive", "NotExecuted", "Skipped", "Pending"];
        if (results.Any(x => !knownOutcomes.Contains(x.Status,
                StringComparer.OrdinalIgnoreCase)))
            errors.Add("TRX содержит неизвестный статус выполнения теста.");

        int passed = results.Count(x => x.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase));
        int failed = results.Count(x => x.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
            x.Status.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            x.Status.Equals("Timeout", StringComparison.OrdinalIgnoreCase) ||
            x.Status.Equals("Aborted", StringComparison.OrdinalIgnoreCase));
        int other = results.Length - passed - failed;

        if (counters is not null)
        {
            static int? Counter(XElement node, string name) =>
                int.TryParse((string?)node.Attribute(name), out int value) && value >= 0
                    ? value : null;
            int? total = Counter(counters, "total");
            int? summaryPassed = Counter(counters, "passed");
            int? summaryFailed = Counter(counters, "failed");
            int? executed = Counter(counters, "executed");
            int? notExecuted = counters.Attribute("notExecuted") is null
                ? null : Counter(counters, "notExecuted");
            if (total is null || summaryPassed is null || summaryFailed is null ||
                executed is null || total != results.Length || summaryPassed != passed ||
                summaryFailed != failed || executed < passed + failed ||
                executed > total ||
                (counters.Attribute("notExecuted") is not null &&
                    (notExecuted is null || notExecuted != total - executed)))
                errors.Add("Счётчики TRX не совпадают с результатами тестов.");
        }

        string[] required = RequiredTests(suite, platform);
        string[] missing = required.Where(name =>
        {
            var matched = results.Where(result =>
                MatchesExactTestName(result.Name, name)).ToArray();
            return matched.Length == 0 || matched.Any(result =>
                !result.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase));
        }).ToArray();

        return new SuiteResult(suite, passed, failed, other, missing, errors, runId);
    }

    /// <summary>Each project must provide evidence from a distinct execution, not a copied TRX.</summary>
    internal static bool AreIndependentRuns(IReadOnlyList<SuiteResult> reports) =>
        reports.Count > 0 && reports.All(report => report.RunId is not null) &&
        reports.Select(report => report.RunId!.Value).Distinct().Count() == reports.Count;

    // Do not accept e.g. TestFoo_Fake instead of TestFoo. Fully-qualified
    // MSTest class names in TRX have the exact method as the last segment.
    private static bool MatchesExactTestName(string fullName, string required) =>
        fullName.Equals(required, StringComparison.Ordinal) ||
        fullName.EndsWith("." + required, StringComparison.Ordinal);

    internal static string[] RequiredTests(string suite, string platform) => suite switch
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
            ["UnsavedOfficeEditsAreFrozenIntoPdfSnapshotForGetPrintWizard"],
        "Office" => ["SaveCopyFiltersFollowTheTargetExtension"],
        _ => []
    };

    private static SuiteResult FailedReport(string suite, string platform, string message) =>
        new(suite, 0, 0, 0, RequiredTests(suite, platform), [message]);
}
