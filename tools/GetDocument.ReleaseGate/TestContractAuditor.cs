using System.Text.RegularExpressions;

namespace GetDocument.ReleaseGate;

/// <summary>
/// Pre-build source-contract check. Missing/renamed mandatory MSTest methods
/// used to be discovered only AFTER the entire physical release laboratory.
/// This is an early diagnostic, never a substitute for actual Passed TRX.
/// </summary>
internal static class TestContractAuditor
{
    private static readonly Regex Method = new(
        @"\bpublic\s+(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+([A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> Audit(string repositoryRoot, string platform)
    {
        var problems = new List<string>();
        if (platform is not ("windows" or "linux") || !Directory.Exists(repositoryRoot))
        {
            problems.Add("Укажите существующий каталог репозитория и платформу windows/linux.");
            return problems;
        }

        foreach (string suite in ReleaseReportEvaluator.ExpectedSuites(platform))
        {
            string folder = suite == "LegacyPdf"
                ? Path.Combine(repositoryRoot, "tests", "GetPdf.Tests")
                : Path.Combine(repositoryRoot, "tests", "GetDocument." + suite + ".Tests");
            if (!Directory.Exists(folder))
            {
                problems.Add(suite + ": отсутствует каталог тестового проекта.");
                continue;
            }
            string csproj = suite == "LegacyPdf"
                ? "GetPdf.Tests.csproj"
                : "GetDocument." + suite + ".Tests.csproj";
            if (!File.Exists(Path.Combine(folder, csproj)))
                problems.Add(suite + ": отсутствует файл проекта тестов.");

            foreach (string method in ReleaseReportEvaluator.RequiredTests(suite, platform))
            {
                if (!HasRequiredMethod(folder, method))
                    problems.Add(suite + ": обязательный тест " + method +
                        " не найден среди объявленных MSTest методов.");
            }
        }

        return problems;
    }

    internal static bool HasRequiredMethod(string testDirectory, string exactMethodName)
    {
        if (string.IsNullOrWhiteSpace(exactMethodName) ||
            !Directory.Exists(testDirectory)) return false;

        foreach (string path in Directory.EnumerateFiles(
            testDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(testDirectory, path);
            string[] parts = relative.Split(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (parts.Any(x => x.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                               x.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                continue;

            bool attributed = false;
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // An attribute must start the source line; matching a word
                // inside a comment/string would give a false contract approval.
                if (trimmed.StartsWith("[TestMethod]", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[DataTestMethod]", StringComparison.Ordinal))
                    attributed = true;
                if (!attributed) continue;

                Match match = Method.Match(trimmed);
                if (match.Success)
                {
                    if (match.Groups[1].Value.Equals(exactMethodName,
                        StringComparison.Ordinal)) return true;
                    attributed = false;
                }
                else if (trimmed.StartsWith("public ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("private ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("internal ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("class ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("}", StringComparison.Ordinal))
                    attributed = false;
            }
        }
        return false;
    }
}
