using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

namespace GetPdf;

/// <summary>
/// Conservative opt-in export of native /Text comment annotations only.
/// A fresh PDF is produced without changing the source. Existing annotations,
/// actions, forms, signatures, attachments and other complex objects fail closed.
/// Square/highlight remain preview-only until appearance streams are validated.
/// </summary>
internal static class PdfNativeTextAnnotationWriter
{
    internal static void SaveNewCopy(
        string source, string output,
        IReadOnlyList<PdfNativeAnnotationDraft> drafts,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count is < 1 or > PdfNativeAnnotationPolicy.MaxAnnotations)
            throw new InvalidDataException("Выберите от 1 до 24 текстовых комментариев.");
        // Refuse mixed visual-only drafts before any file system writes.
        if (drafts.Any(d => d is null || !d.IsValid || d.Kind != "text"))
            throw new NotSupportedException(
                "Только текстовые PDF-комментарии имеют поддерживаемый формат /Text. " +
                "Рамки и выделение пока доступны как эскизы.");
        if (!PdfSaveSafety.TryValidateNewCopy(output, [source], out var reason))
            throw new IOException(reason);
        cancellation.ThrowIfCancellationRequested();

        string inputPath = Path.GetFullPath(source);
        if (!File.Exists(inputPath) ||
            new FileInfo(inputPath).Length is < 1 or > PdfNativeAnnotationPolicy.MaxPdfBytes)
            throw new InvalidDataException("PDF отсутствует или превышает предел аннотаций 32 МиБ.");

        using var original = PdfReader.Open(inputPath, PdfDocumentOpenMode.Import);
        if (drafts.Any(d => d.PageIndex >= original.PageCount))
            throw new InvalidDataException("Страница комментария отсутствует в PDF.");
        var specs = drafts.Select(d => d.ToPdfSpec(
            original.Pages[d.PageIndex].Width.Point,
            original.Pages[d.PageIndex].Height.Point)).ToArray();
        PdfNativeAnnotationPolicy.ValidateDocument(original, specs);

        string destination = Path.GetFullPath(output);
        string folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        string stage = Path.Combine(folder, ".getpdf-annotations-" +
            Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using (var revised = new PdfDocument())
            {
                foreach (var page in original.Pages)
                {
                    cancellation.ThrowIfCancellationRequested();
                    revised.AddPage(page);
                }
                foreach (var spec in specs)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var note = new PdfTextAnnotation
                    {
                        Contents = spec.Text,
                        Rectangle = new PdfRectangle(new PdfSharp.Drawing.XRect(
                            spec.X0, spec.Y0, spec.X1 - spec.X0, spec.Y1 - spec.Y0))
                    };
                    revised.Pages[spec.PageIndex].Annotations.Add(note);
                }
                revised.Save(stage);
            }
            cancellation.ThrowIfCancellationRequested();
            PdfOutputVerifier.Verify(stage, original.PageCount);
            // An ordinary page-count check is insufficient for annotations:
            // round-trip every produced /Text object and verify its contents.
            using (var checkedPdf = PdfReader.Open(stage, PdfDocumentOpenMode.Import))
            {
                for (int i = 0; i < checkedPdf.PageCount; i++)
                {
                    var expected = specs.Where(s => s.PageIndex == i).ToArray();
                    var annots = checkedPdf.Pages[i].Elements.GetObject("/Annots") as PdfArray;
                    if (expected.Length == 0)
                    {
                        if (annots is not null && annots.Elements.Count > 0)
                            throw new InvalidDataException("Неожиданные PDF-аннотации.");
                        continue;
                    }
                    if (annots is null || annots.Elements.Count != expected.Length)
                        throw new InvalidDataException("Не все PDF-комментарии сохранились.");
                    for (int j = 0; j < expected.Length; j++)
                    {
                        var item = annots.Elements[j];
                        var dict = item is PdfReference reference
                            ? reference.Value as PdfDictionary : item as PdfDictionary;
                        if (dict is null || dict.Elements.GetName("/Subtype") != "/Text" ||
                            dict.Elements.GetString("/Contents") != expected[j].Text)
                            throw new InvalidDataException("PDF-комментарий изменился при сохранении.");
                    }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(stage, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(stage))
            {
                try { File.Delete(stage); }
                catch (IOException) { /* Next session cleanup can retry. */ }
            }
        }
    }
}
