using System.Text.Json;

namespace GetDocument.Office.EuroOfficeDesktop;

/// <summary>
/// GD-31J.31: bounded, redact-first report of an explicitly requested
/// read-only synthetic recovery inspection. It does NOT read file contents
/// (only the existing GD-31J.30 journal inspector does limited journal reads),
/// write report files, delete/move pending entries, restore, publish or grant
/// WORM/GET-system permissions. JSON omits ALL filesystem basenames/paths.
/// </summary>
public static class EuroOfficeSyntheticRecoveryReport
{
    private const string Schema = "getdoc-synthetic-recovery-report/1";
    private const int PageSize = 32;
    private const int MaxJsonChars = 16384;

    public sealed record CountByState(
        EuroOfficeSyntheticSnapshotJournal.Observation State, int Count);

    public sealed record Finding(
        string? AttemptId,
        EuroOfficeSyntheticSnapshotJournal.Observation State,
        string LastStage,
        string Guidance)
    {
        public bool CleanupAuthorized => false;
        public bool AuthorizedForPublication => false;
    }

    public sealed record Page(
        int PageIndex, int PageCount, int TotalFindings,
        IReadOnlyList<CountByState> Counts, IReadOnlyList<Finding> Findings)
    {
        public bool ReadOnly => true;
        public bool ManualReviewRequired => true;
        public bool CleanupAuthorized => false;
        public bool AuthorizedForPublication => false;
        public bool DurabilityProven => false;

        /// <summary>
        /// Produce bounded UTF-8-compatible JSON *in memory only*.
        /// The caller must separately decide whether to copy it to an
        /// approved diagnostic channel. There is NO implicit export to disk.
        /// </summary>
        public string ToJson()
        {
            var result = new
            {
                schema = Schema,
                scope = "synthetic-offline-reviewed-only",
                pageIndex = PageIndex,
                pageCount = PageCount,
                totalFindings = TotalFindings,
                readOnly = ReadOnly,
                manualReviewRequired = ManualReviewRequired,
                cleanupAuthorized = CleanupAuthorized,
                authorizedForPublication = AuthorizedForPublication,
                durabilityProven = DurabilityProven,
                counts = Counts.Select(x => new
                {
                    state = x.State.ToString(),
                    count = x.Count
                }).ToArray(),
                findings = Findings.Select(x => new
                {
                    attemptId = x.AttemptId,
                    state = x.State.ToString(),
                    lastStage = x.LastStage,
                    guidance = x.Guidance,
                    cleanupAuthorized = x.CleanupAuthorized,
                    authorizedForPublication = x.AuthorizedForPublication
                }).ToArray()
            };
            string json = JsonSerializer.Serialize(result);
            if (json.Length > MaxJsonChars)
                throw new InvalidDataException("Превышен лимит диагностического отчёта.");
            return json;
        }
    }

    /// <summary>
    /// Explicit manual invocation. There is no timer, scheduled task or
    /// production launch path. The caller provides a private synthetic staging
    /// directory that GD-31J.30 checks for links, UNC and bounded scan size.
    /// </summary>
    public static Page Build(
        string stagingRoot, int pageIndex = 0, bool labApproved = false)
    {
        if (!labApproved)
            throw new NotSupportedException(
                "Диагностика аварийных файлов разрешена только в синтетической лаборатории.");
        if (pageIndex is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(pageIndex),
                "Только ограниченная постраничная диагностика.");
        IReadOnlyList<EuroOfficeSyntheticSnapshotJournal.Report> source =
            EuroOfficeSyntheticSnapshotJournal.Inspect(stagingRoot, labApproved: true);
        int total = source.Count;
        int pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        if (pageIndex >= pages)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        // Sort solely by allowlisted enum and validated journal GUID. Never
        // sort/return a potentially hostile or personally revealing basename.
        var ordered = source
            .OrderBy(x => x.State)
            .ThenBy(x => x.AttemptId?.ToString("N") ?? "", StringComparer.Ordinal)
            .ToArray();
        var counts = Enum.GetValues<EuroOfficeSyntheticSnapshotJournal.Observation>()
            .Select(state => new CountByState(state,
                ordered.Count(item => item.State == state)))
            .ToArray();
        Finding[] page = ordered.Skip(pageIndex * PageSize).Take(PageSize)
            .Select(x => new Finding(
                x.AttemptId?.ToString("N"),
                x.State,
                SafeStage(x.LastStage),
                GuidanceFor(x.State)))
            .ToArray();
        return new Page(pageIndex, pages, total, counts, page);
    }

    private static string SafeStage(string input) => input switch
    {
        "intent" => "intent",
        "pending_flushed" => "pending_flushed",
        "linked" => "linked",
        "completed" => "completed",
        _ => "unknown"
    };

    private static string GuidanceFor(EuroOfficeSyntheticSnapshotJournal.Observation state) =>
        state switch
        {
            EuroOfficeSyntheticSnapshotJournal.Observation.PendingNeedsReview =>
                "Незавершённая запись; проверить вручную в изолированной лаборатории.",
            EuroOfficeSyntheticSnapshotJournal.Observation.PendingAfterLink =>
                "Возможны две ссылки; проверить последовательность без удаления файлов.",
            EuroOfficeSyntheticSnapshotJournal.Observation.CompleteObserved =>
                "Завершение наблюдается, но подлинность и долговечность не подтверждены.",
            EuroOfficeSyntheticSnapshotJournal.Observation.UncertainAfterLink =>
                "Конечная запись есть; журнал завершения не подтверждён.",
            EuroOfficeSyntheticSnapshotJournal.Observation.NoOutputObserved =>
                "Журнал обнаружен, результат отсутствует; проверить причину.",
            EuroOfficeSyntheticSnapshotJournal.Observation.MissingFinal =>
                "Журнал утверждает завершение, конечный файл не обнаружен.",
            EuroOfficeSyntheticSnapshotJournal.Observation.MalformedJournal =>
                "Повреждённый или неподтверждённый журнал; требуется ручная проверка.",
            EuroOfficeSyntheticSnapshotJournal.Observation.UnknownPending =>
                "Неидентифицированный временный файл; не удалять автоматически.",
            _ => "Только ручная изолированная диагностика."
        };
}
