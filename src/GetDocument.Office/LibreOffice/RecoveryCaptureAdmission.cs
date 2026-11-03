namespace GetDocument.Office.LibreOffice;

/// <summary>
/// GD-31J.27bl: conservative admission policy for NEW recovery captures.
/// This is a per-document pressure stop, not a global disk quota, distributed
/// lock, or authorization to clean up any prior user/recovery file.
/// </summary>
internal static class RecoveryCaptureAdmission
{
    // At 128 MiB maximum per accepted OOXML copy, 16 incomplete objects can
    // already impose significant storage pressure. Count links and unreadable
    // candidates too; none may be deleted to make room.
    internal const int MaxIncompleteCandidates = 16;

    // Includes valid manifests, unknown names, malformed manifests, links
    // and foreign entries. Do not let these evade the incomplete-name budget.
    internal const int MaxDirectoryEntriesBeforeCapture = 64;

    internal static void EnsureMayCapture(OfficeRecoveryIncompleteInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        // A partial enumeration cannot prove that the budget is available.
        // Fail closed before asking the exporter to create another file.
        if (inventory.Truncated)
            throw new IOException(
                "Новый аварийный снимок запрещён: каталог восстановления обследован не полностью.");

        if (inventory.ObservedEntries >= MaxDirectoryEntriesBeforeCapture)
            throw new IOException(
                "Превышен безопасный предел записей каталога аварийного восстановления. " +
                "Новые снимки заблокированы без удаления старых файлов.");

        if (inventory.Candidates.Count >= MaxIncompleteCandidates)
            throw new IOException(
                "Превышен безопасный лимит незавершённых аварийных копий. " +
                "Требуется проверка без автоматического удаления файлов.");
    }
}
