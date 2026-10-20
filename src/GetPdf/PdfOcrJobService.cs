using System.Security.Cryptography;

namespace GetPdf;

/// <summary>
/// Coordinates the transactional lifetime of a local PDF OCR operation.
/// The caller supplies a short-batch NAPS2 export callback, while this service
/// owns chunk verification, scratch cleanup, reassembly, source identity checks,
/// and the single final publish operation. Independent of WPF and NAPS2 types.
/// </summary>
internal static class PdfOcrJobService
{
    public static void ValidatePaths(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OCR нельзя сохранять поверх оригинала PDF.");
        if (!string.Equals(Path.GetExtension(source), ".pdf", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(destination), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("OCR принимает и сохраняет только файлы PDF.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Исходный PDF не найден.", source);
    }

    public static int ValidateBatches(IReadOnlyList<PdfOcrBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0) throw new ArgumentException("Нет пакетов OCR.", nameof(batches));
        int nextPage = 0;
        foreach (var batch in batches)
        {
            if (batch.FirstSourcePage != nextPage ||
                batch.PageCount < 1 || batch.PageCount > PdfOcrBatchPlanner.DefaultMaxPages)
                throw new ArgumentException(
                    "Пакеты OCR должны идти по порядку, без пропусков и превышения лимита.", nameof(batches));
            nextPage = checked(nextPage + batch.PageCount);
        }
        return nextPage;
    }

    public static async Task RunAsync(
        string sessionFolder,
        string sourcePath,
        string destinationPath,
        IReadOnlyList<PdfOcrBatch> batches,
        Func<PdfOcrBatch, string, CancellationToken, Task> exportBatchAsync,
        Action<int, int>? reportCompleted = null,
        CancellationToken cancellation = default,
        bool? qpdfAvailable = null,
        Func<string, long>? availableSpaceBytes = null,
        Func<string, string>? volumeIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(exportBatchAsync);
        ValidatePaths(sourcePath, destinationPath);
        int expectedCount = ValidateBatches(batches);
        // Fail before the first NAPS2 export for a large document when its
        // required native backend is unavailable. The optional capability
        // override permits deterministic unit tests without installed qpdf.
        // Small jobs do not execute qpdf and must remain usable even if
        // an optional qpdf installation is incomplete or unapproved.
        bool nativeRequired = expectedCount > PdfOcrAssemblyPolicy.MaxManagedPages;
        bool nativeReady = nativeRequired &&
            (qpdfAvailable ?? PdfQpdfValidator.IsAvailable);
        var backend = PdfOcrAssemblyPolicy.Select(expectedCount, nativeReady);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var original = new FileInfo(source);
        long originalLength = original.Length;
        DateTime originalWriteUtc = original.LastWriteTimeUtc;
        // Reject a known low-disk situation BEFORE exporting the first OCR
        // page. Per-chunk and per-qpdf-stage checks still guard later growth.
        string targetDir = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(targetDir);
        PdfOcrDiskPreflight.RequireSufficientSpace(
            expectedCount, originalLength, sessionFolder, targetDir,
            availableSpaceBytes, volumeIdentifier);
        // Stream the original through SHA-256: no full-file byte[] allocation.
        static byte[] HashFile(string path)
        {
            using var file = File.OpenRead(path);
            return SHA256.HashData(file);
        }
        byte[] originalHash = HashFile(source);

        var staging = Path.Combine(targetDir,
            ".gupscan-ocr-" + Guid.NewGuid().ToString("N") + ".pdf");

        void VerifySource()
        {
            var current = new FileInfo(source);
            if (!current.Exists || current.Length != originalLength ||
                current.LastWriteTimeUtc != originalWriteUtc)
                throw new InvalidDataException(
                    "Исходный PDF изменился во время OCR. Результат не сохранён.");
        }

        try
        {
            using var scratch = PdfOcrScratch.Create(sessionFolder);
            for (int i = 0; i < batches.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                VerifySource();
                var path = scratch.GetChunkPath(i);
                await exportBatchAsync(batches[i], path, cancellation);
                cancellation.ThrowIfCancellationRequested();
                VerifySource();
                PdfOutputVerifier.Verify(path, batches[i].PageCount);
                scratch.RegisterChunk(i);
                reportCompleted?.Invoke(i + 1, batches.Count);
            }

            cancellation.ThrowIfCancellationRequested();
            VerifySource();

            // One-chunk OCR should remain byte-exact: avoid reconstructing
            // annotations or OCR text resources through a second PDF library.
            // Multi-chunk assembly is CPU/IO intensive; run it off the WPF UI thread.
            if (scratch.ChunkFiles.Count == 1)
                await Task.Run(() => File.Copy(scratch.ChunkFiles[0], staging), cancellation);
            else if (backend == PdfOcrAssemblyBackend.OfflineQpdf)
                await PdfQpdfChunkAssembler.MergeAndVerifyAsync(
                    scratch.ChunkFiles, staging, expectedCount, cancellation,
                    scratch.DirectoryPath);
            else
                await Task.Run(() => PdfOcrChunkAssembler.Assemble(
                    scratch.ChunkFiles, staging, expectedCount, cancellation), cancellation);

            cancellation.ThrowIfCancellationRequested();
            VerifySource();
            // File size/mtime may be restored by another process; compare the
            // source bytes, not only metadata, before committing output.
            if (!CryptographicOperations.FixedTimeEquals(originalHash, HashFile(source)))
                throw new InvalidDataException(
                    "Содержимое исходного PDF изменилось во время OCR. Результат не сохранён.");
            // The native backend checks --check and --show-npages without
            // opening the entire final 500-page file through PDFsharp.
            if (backend == PdfOcrAssemblyBackend.OfflineQpdf)
                await PdfQpdfChunkAssembler.VerifyAsync(staging, expectedCount, cancellation);
            else
                PdfOutputVerifier.Verify(staging, expectedCount);
            cancellation.ThrowIfCancellationRequested();
            VerifySource();
            File.Move(staging, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch (IOException) { /* best-effort; original and destination are untouched */ }
        }
    }
}
