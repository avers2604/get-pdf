using System.Xml.Linq;

namespace GetDocument.ReleaseGate;

/// <summary>
/// Dependency-free adversarial tests for the release gate itself. Runs before
/// trusting any report; a broken gate can never certify its own release.
/// </summary>
internal static class ReleaseGateSelfTests
{
    internal static int Run()
    {
        var failures = new List<string>();
        int cases = 0;

        void Check(string title, Func<bool> condition)
        {
            cases++;
            try
            {
                if (!condition()) failures.Add(title);
            }
            catch (Exception ex)
            {
                failures.Add(title + " (" + ex.GetType().Name + ")");
            }
        }

        var requiredCore = ReleaseReportEvaluator.RequiredTests("Core", "linux");
        var valid = MakeTrx(requiredCore.Select(name => (Name: name, Status: "Passed")));
        Check("standard TRX passes", () =>
            ReleaseReportEvaluator.EvaluateDocument("Core", "linux", valid).Approved);
        Check("Linux printing mandatory tests exist", () =>
            ReleaseReportEvaluator.RequiredTests("Printing", "linux").Length >= 5);
        Check("Windows Office test is mandatory", () =>
            ReleaseReportEvaluator.RequiredTests("Office", "windows")
                .Contains("UnsavedOfficeEditsAreFrozenIntoPdfSnapshotForGetPrintWizard"));

        Check("one absent required test blocks", () =>
            !ReleaseReportEvaluator.EvaluateDocument("Core", "linux",
                MakeTrx(requiredCore.Take(3).Select(x => (x, "Passed")))).Approved);
        Check("substring impersonation blocks", () =>
            !ReleaseReportEvaluator.EvaluateDocument("Core", "linux",
                MakeTrx(requiredCore.Select(name =>
                    (Name: name == requiredCore[0] ? "Fake" + name : name,
                        Status: "Passed")))).Approved);
        Check("inconclusive mandatory test blocks", () =>
            !ReleaseReportEvaluator.EvaluateDocument("Core", "linux",
                MakeTrx(requiredCore.Select(name =>
                    (Name: name, Status: name == requiredCore[0]
                        ? "Inconclusive" : "Passed")))).Approved);
        Check("duplicated mandatory result with inconclusive blocks", () =>
            !ReleaseReportEvaluator.EvaluateDocument("Core", "linux",
                MakeTrx(requiredCore.Select(x => (x, "Passed"))
                    .Append((requiredCore[0], "Inconclusive")))).Approved);
        Check("unknown test outcome blocks", () =>
            !ReleaseReportEvaluator.EvaluateDocument("Core", "linux",
                MakeTrx(requiredCore.Select(x => (x, "Passed"))
                    .Append(("UnknownTask", "SuccessMaybe")))).Approved);
        Check("summary Failed blocks even if individual tests passed", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            doc.Root!.Element(ns + "ResultSummary")!
                .SetAttributeValue("outcome", "Failed");
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("empty success summary is never trusted", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            doc.Root!.Element(ns + "ResultSummary")!.SetAttributeValue("outcome", "");
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("unknown or inconclusive summary is never trusted", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            doc.Root!.Element(ns + "ResultSummary")!.SetAttributeValue("outcome", "Inconclusive");
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("executed count cannot be less than passed count", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            doc.Root!.Element(ns + "ResultSummary")!.Element(ns + "Counters")!
                .SetAttributeValue("executed", 0);
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("not-executed count must agree with total and executed", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            doc.Root!.Element(ns + "ResultSummary")!.Element(ns + "Counters")!
                .SetAttributeValue("notExecuted", 1);
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("missing TRX run ID cannot attest acceptance", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            doc.Root!.Attribute("id")!.Remove();
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("forged non-GUID run ID cannot attest acceptance", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            doc.Root!.SetAttributeValue("id", "not-a-guid");
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("one TRX cannot impersonate two separate suites", () =>
        {
            var doc = MakeTrx([("Ordinary", "Passed")]);
            var images = ReleaseReportEvaluator.EvaluateDocument("Images", "linux", doc);
            var revisions = ReleaseReportEvaluator.EvaluateDocument("Revisions", "linux", doc);
            return images.Approved && revisions.Approved &&
                   !ReleaseReportEvaluator.AreIndependentRuns([images, revisions]);
        });
        Check("separate TRX run IDs are accepted", () =>
        {
            var images = ReleaseReportEvaluator.EvaluateDocument("Images", "linux",
                MakeTrx([("Ordinary", "Passed")]));
            var revisions = ReleaseReportEvaluator.EvaluateDocument("Revisions", "linux",
                MakeTrx([("Ordinary", "Passed")]));
            return images.Approved && revisions.Approved &&
                   ReleaseReportEvaluator.AreIndependentRuns([images, revisions]);
        });
        Check("failed optional test blocks", () =>
            !ReleaseReportEvaluator.EvaluateDocument("Core", "linux",
                MakeTrx(requiredCore.Select(x => (x, "Passed"))
                    .Append(("OtherFailure", "Failed")))).Approved);
        Check("misleading TRX counters block", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            doc.Root!.Element(ns + "ResultSummary")!
                .Element(ns + "Counters")!.SetAttributeValue("passed", 99);
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("wrong XML root blocks", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            doc.Root!.Name = "FakeTestRun";
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("missing TRX ResultSummary blocks", () =>
        {
            var doc = MakeTrx(requiredCore.Select(x => (x, "Passed")));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            doc.Root!.Element(ns + "ResultSummary")!.Remove();
            return !ReleaseReportEvaluator.EvaluateDocument("Core", "linux", doc).Approved;
        });
        Check("zero-result fake blocks", () =>
            !ReleaseReportEvaluator.EvaluateDocument("Images", "linux",
                MakeTrx([])).Approved);
        Check("healthy Linux printing report passes", () =>
            ReleaseReportEvaluator.EvaluateDocument("Printing", "linux",
                MakeTrx(ReleaseReportEvaluator.RequiredTests("Printing", "linux")
                    .Select(x => (x, "Passed")))).Approved);
        Check("healthy Windows Office report passes", () =>
            ReleaseReportEvaluator.EvaluateDocument("Office", "windows",
                MakeTrx(ReleaseReportEvaluator.RequiredTests("Office", "windows")
                    .Select(x => (x, "Passed")))).Approved);

        string folder = Path.Combine(Path.GetTempPath(),
            "gd26-gate-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Check("missing report blocks", () =>
                !ReleaseReportEvaluator.EvaluateFile("Core", "linux",
                    Path.Combine(folder, "missing.trx")).Approved);
            string corrupt = Path.Combine(folder, "corrupt.trx");
            File.WriteAllText(corrupt, "<TestRun");
            Check("truncated TRX blocks", () =>
                !ReleaseReportEvaluator.EvaluateFile("Core", "linux", corrupt).Approved);
            // Keep the TRX otherwise fully valid: if the XML parser were to
            // permit DTDs, this fixture would pass all mandatory Core checks.
            // This proves that DTD blocking works rather than merely detecting
            // an unrelated wrong XML namespace or missing test results.
            string validTrx = MakeTrx(requiredCore.Select(name =>
                (Name: name, Status: "Passed"))).ToString(SaveOptions.DisableFormatting);
            const string externalEntity =
                "<!DOCTYPE TestRun [<!ENTITY dangerous SYSTEM 'file:///etc/passwd'>]>";
            string maliciousTrx = externalEntity +
                validTrx.Replace("<TestRun ", "<TestRun operator=\"&dangerous;\" ",
                    StringComparison.Ordinal);
            File.WriteAllText(corrupt, maliciousTrx);
            Check("XML DTD/entity resolution blocks", () =>
                !ReleaseReportEvaluator.EvaluateFile("Core", "linux", corrupt).Approved);
        }
        finally { Directory.Delete(folder, recursive: true); }

        foreach (string failure in failures)
            Console.Error.WriteLine("SELF-TEST FAIL: " + failure);
        Console.WriteLine("GD-26 release gate self-check: " +
            (cases - failures.Count) + "/" + cases + " assertions passed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static XDocument MakeTrx(IEnumerable<(string Name, string Status)> scenarios)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var tests = scenarios.ToArray();
        int passed = tests.Count(x => x.Status == "Passed");
        int failed = tests.Count(x => x.Status == "Failed");
        return new XDocument(
            new XElement(ns + "TestRun",
                new XAttribute("id", Guid.NewGuid()),
                new XElement(ns + "Results",
                    tests.Select((test, i) => new XElement(ns + "UnitTestResult",
                        new XAttribute("testId", Guid.NewGuid()),
                        new XAttribute("testName", "GET.Tests." + test.Name),
                        new XAttribute("outcome", test.Status)))),
                new XElement(ns + "ResultSummary",
                    new XAttribute("outcome", failed > 0 ? "Failed" : "Completed"),
                    new XElement(ns + "Counters",
                        new XAttribute("total", tests.Length),
                        new XAttribute("executed", tests.Length),
                        new XAttribute("passed", passed),
                        new XAttribute("failed", failed)))));
    }
}
