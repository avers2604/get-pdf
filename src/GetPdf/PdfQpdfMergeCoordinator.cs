namespace GetPdf;

/// <summary>
/// Coordinates bounded, ordered, multi-level qpdf merges. All intermediate
/// output files are private, stale levels are deleted after use, and each
/// merge plus final copy checks available disk space. No guarantee of a hard
/// disk reservation: other processes can consume free space concurrently.
/// </summary>
internal static class PdfQpdfMergeCoordinator
{
    public static async Task MergeAsync(
        IReadOnlyList<string> sources,
        string destinationPath,
        Func<IReadOnlyList<string>, string, CancellationToken, Task> mergeGroupAsync,
        CancellationToken cancellation = default,
        string? scratchRoot = null,
        Func<string, long>? availableBytes = null)
    {
        ArgumentNullException.ThrowIfNull(mergeGroupAsync);
        cancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var files = sources.Select(Path.GetFullPath).ToList();
        if (files.Count == 0)
            throw new ArgumentException("Нет OCR-фрагментов.", nameof(sources));
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new ArgumentException("Повторный OCR-фрагмент.", nameof(sources));
        if (files.Any(path => string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Нельзя перезаписывать исходный фрагмент OCR.");

        var root = Path.GetFullPath(scratchRoot ?? Path.GetDirectoryName(destination)!);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Рабочий каталог сборки PDF не существует.");

        var temp = Path.Combine(root, "qpdf-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        // Track only files created by us, never user-owned OCR chunks.
        var intermediate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int level = 0;
            while (files.Count > 1)
            {
                cancellation.ThrowIfCancellationRequested();
                var groups = PdfQpdfMergePlanner.Partition(files);
                if (groups.Count == files.Count)
                    throw new InvalidOperationException("Пути OCR слишком длинны для qpdf.");

                var next = new List<string>(groups.Count);
                for (int i = 0; i < groups.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var group = groups[i];
                    if (group.Count == 1)
                    {
                        next.Add(group[0]);
                        continue;
                    }

                    var result = Path.Combine(temp, $"level-{level:D3}-part-{i:D5}.pdf");
                    // Recheck disk space before EVERY native invocation.
                    PdfQpdfDiskBudget.RequireAvailableSpace(temp, group, availableBytes);
                    await mergeGroupAsync(group, result, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (!File.Exists(result) || new FileInfo(result).Length == 0)
                        throw new InvalidDataException("qpdf не создал промежуточный PDF.");
                    intermediate.Add(result);
                    next.Add(result);
                }

                // No result from a previous merge level is needed after
                // the entire new level completed. Free space immediately.
                var retained = new HashSet<string>(next, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (intermediate.Contains(file) && !retained.Contains(file))
                    {
                        File.Delete(file);
                        intermediate.Remove(file);
                    }
                }
                files = next;
                level++;
            }

            cancellation.ThrowIfCancellationRequested();
            var destinationDir = Path.GetDirectoryName(destination)!;
            // Final copy may land on a different volume from the OCR session.
            PdfQpdfDiskBudget.RequireAvailableSpace(destinationDir, [files[0]], availableBytes);
            var stagedCopy = Path.Combine(destinationDir,
                ".getpdf-qpdf-copy-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                File.Copy(files[0], stagedCopy);
                cancellation.ThrowIfCancellationRequested();
                // Replace existing output only after the entire copy completed.
                File.Move(stagedCopy, destination, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(stagedCopy)) File.Delete(stagedCopy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
