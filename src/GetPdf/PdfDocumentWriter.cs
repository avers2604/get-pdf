using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GetPdf;

public sealed record PdfSourcePageReference(string SourcePath, int SourcePage, int Rotation = 0);

/// <summary>
/// Copies PDF page objects; never sends documents to a server and never overwrites source files.
/// PDF signatures, AcroForms, tagging, attachments and annotation fidelity require separate tests.
/// </summary>
internal static class PdfDocumentWriter
{
    internal static void SaveMixed(string destination, IReadOnlyList<PdfSourcePageReference> pages, CancellationToken cancellation = default)
    {
        if (pages.Count < 1 || pages.Count > WorkstationPolicy.Current.MaxDocumentPages)
            throw new InvalidOperationException("Превышен лимит страниц документа или документ пуст.");
        RejectSourceOverwrite(Path.GetFullPath(destination), pages.Select(p => Path.GetFullPath(p.SourcePath)).ToArray());
        WriteAtomically(destination, temp =>
        {
            var inputs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var output = new PdfDocument();
                foreach (var page in pages)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string source = Path.GetFullPath(page.SourcePath);
                    if (!inputs.TryGetValue(source, out var input))
                    {
                        input = PdfReader.Open(source, PdfDocumentOpenMode.Import); inputs.Add(source, input);
                        PdfStructureGuard.EnsureSafeToRewrite(input);
                    }
                    CopyPages(input, output, [new PdfPageReference(page.SourcePage, page.Rotation)], cancellation);
                }
                output.Save(temp);
            }
            finally { foreach (var input in inputs.Values) input.Dispose(); }
        }, () => pages.Count, cancellation);
    }
    internal static void AppendSourceRevision(string source, string editedSinglePage, string destination) => AppendSourcePages(source, editedSinglePage, destination, singlePage: true);
    internal static void AppendSourcePages(string source, string editedSinglePage, string destination, bool singlePage = false)
    {
        RejectSourceOverwrite(Path.GetFullPath(destination), [Path.GetFullPath(source), Path.GetFullPath(editedSinglePage)]);
        int count = 0;
        WriteAtomically(destination, temp =>
        {
            using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            using var edited = PdfReader.Open(editedSinglePage, PdfDocumentOpenMode.Import);
            PdfStructureGuard.EnsureSafeToRewrite(input); PdfStructureGuard.EnsureSafeToRewrite(edited);
            if ((singlePage && edited.PageCount != 1) || edited.PageCount < 1 || input.PageCount + edited.PageCount > 10000) throw new InvalidOperationException("Неверная версия страницы PDF.");
            using var output = new PdfDocument();
            foreach (var page in input.Pages) output.AddPage(page);
            foreach (var page in edited.Pages) output.AddPage(page); count = output.PageCount;
            output.Save(temp);
        }, () => count, CancellationToken.None);
    }
    public static void SaveAs(string sourcePath, string destinationPath, IReadOnlyList<PdfPageReference> pages, CancellationToken cancellation = default)
    {
        if (pages is null || pages.Count == 0)
            throw new ArgumentException("Нужна хотя бы одна страница.", nameof(pages));
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        RejectSourceOverwrite(destination, [source]);
        WriteAtomically(destination, temp =>
        {
            using var original = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            PdfStructureGuard.EnsureSafeToRewrite(original);
            using var output = new PdfDocument();
            CopyPages(original, output, pages, cancellation);
            output.Save(temp);
        }, () => pages.Count, cancellation);
    }

    public static void Merge(string sourcePath, IReadOnlyList<PdfPageReference> pages,
        IReadOnlyList<string> additionalPaths, string destinationPath, CancellationToken cancellation = default)
    {
        if (pages is null || pages.Count == 0)
            throw new ArgumentException("Нужно выбрать исходные страницы.", nameof(pages));
        if (additionalPaths is null || additionalPaths.Count == 0)
            throw new ArgumentException("Добавьте хотя бы один PDF.", nameof(additionalPaths));
        var sources = new[] { Path.GetFullPath(sourcePath) }
            .Concat(additionalPaths.Select(Path.GetFullPath)).ToArray();
        var destination = Path.GetFullPath(destinationPath);
        RejectSourceOverwrite(destination, sources);
        foreach (var source in sources)
            if (new FileInfo(source).Length > WorkstationPolicy.Current.MaxPdfInputMiB * 1024L * 1024L)
                throw new InvalidDataException("PDF превышает корпоративный лимит размера.");
        int expectedMergePageCount = 0;
        WriteAtomically(destination, temp =>
        {
            // Imported resources can be referenced until output.Save completes:
            // keep every source PDF alive through the final serialization.
            var inputs = new List<PdfDocument>();
            try
            {
                foreach (var source in sources)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
                    inputs.Add(input);
                    PdfStructureGuard.EnsureSafeToRewrite(input);
                    if (input.PageCount > WorkstationPolicy.Current.MaxDocumentPages)
                        throw new InvalidDataException("Превышен лимит страниц PDF.");
                }
                using var output = new PdfDocument();
                CopyPages(inputs[0], output, pages, cancellation);
                foreach (var input in inputs.Skip(1))
                    for (int index = 0; index < input.PageCount; index++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        output.AddPage(input.Pages[index]);
                    }
                if (output.PageCount > WorkstationPolicy.Current.MaxDocumentPages)
                    throw new InvalidDataException("Объединённый PDF превышает лимит страниц.");
                expectedMergePageCount = output.PageCount;
                output.Save(temp);
            }
            finally { foreach (var input in inputs) input.Dispose(); }
        }, () => expectedMergePageCount, cancellation);
    }

    private static void CopyPages(PdfDocument input, PdfDocument output, IReadOnlyList<PdfPageReference> pages, CancellationToken cancellation)
    {
        foreach (var planned in pages)
        {
            cancellation.ThrowIfCancellationRequested();
            if (planned.SourcePage < 0 || planned.SourcePage >= input.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pages), "Недействительный номер страницы.");
            if (planned.Rotation is not (0 or 90 or 180 or 270))
                throw new ArgumentException("Недопустимый угол поворота.", nameof(pages));
            if (planned.Rectangles is { Count: > 0 } &&
                (planned.Rotation != 0 || !PdfMarkupPolicy.CanPlace(input.Pages[planned.SourcePage])))
                throw new InvalidOperationException(
                    "Векторная рамка не поддерживается для повёрнутой или обрезанной PDF-страницы.");
            if (planned.Rectangles?.Any(rect => rect is null || !rect.IsValid) == true ||
                planned.Rectangles is { Count: > 128 })
                throw new ArgumentException("Некорректная разметка PDF.", nameof(pages));
            if (planned.Notes is { Count: > 0 } &&
                (planned.Rotation != 0 || !PdfMarkupPolicy.CanPlace(input.Pages[planned.SourcePage])))
                throw new InvalidOperationException("Нельзя добавить заметку на повёрнутую или нестандартную PDF-страницу.");
            if (planned.Notes is { Count: > 32 } ||
                planned.Notes?.Any(note => note is null || !note.IsValid) == true)
                throw new ArgumentException("Некорректные текстовые заметки PDF.", nameof(pages));
            var page = output.AddPage(input.Pages[planned.SourcePage]);
            // Rotate is clockwise PDF page metadata, and the text/image contents remain vector.
            page.Rotate = ((page.Rotate + planned.Rotation) % 360 + 360) % 360;
            bool hasStamp = !string.IsNullOrEmpty(planned.Stamp);
            bool hasRectangles = planned.Rectangles is { Count: > 0 };
            bool hasNotes = planned.Notes is { Count: > 0 };
            if (hasStamp || hasRectangles || hasNotes)
            {
                // PDFsharp may reject opening a second XGraphics context for the
                // same output page. Paint every visible overlay in one pass.
                using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                if (hasStamp)
                    AddVisibleStamp(graphics, page, planned.Stamp);
                if (hasRectangles)
                    DrawVectorRectangles(graphics, page, planned.Rectangles!);
                if (hasNotes)
                    DrawTextNotes(graphics, page, planned.Notes!);
            }
        }
    }

    private static void AddVisibleStamp(XGraphics graphics, PdfPage page, string stamp)
    {
        if (stamp.Length > 80 || stamp.Any(char.IsControl))
            throw new ArgumentException("Недопустимый текст штампа.", nameof(stamp));
        var font = new XFont("Arial", 15, XFontStyleEx.Bold);
        graphics.DrawString(stamp, font, XBrushes.DarkRed,
            new XRect(20, 20, Math.Max(30, page.Width.Point - 40), 38),
            XStringFormats.TopLeft);
    }

    private static void DrawVectorRectangles(XGraphics graphics, PdfPage page, IReadOnlyList<PdfRectangleMarkup> rectangles)
    {
        // These are flattened visible vector outlines, NOT PDF /Annots and NOT redaction.
        var pen = new XPen(XColors.Red, 2);
        foreach (var rectangle in rectangles)
        {
            var box = PdfMarkupPolicy.ToPoints(rectangle, page.Width.Point, page.Height.Point);
            graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        }
    }

    private static void DrawTextNotes(XGraphics graphics, PdfPage page, IReadOnlyList<PdfTextNote> notes)
    {
        var formatter = new XTextFormatter(graphics);
        var font = new XFont("Arial", 9, XFontStyleEx.Regular);
        foreach (var note in notes)
        {
            double x = note.X * page.Width.Point;
            double y = note.Y * page.Height.Point;
            double width = .4 * page.Width.Point;
            double height = .12 * page.Height.Point;
            graphics.DrawRectangle(new XPen(XColors.DarkRed, 1), XBrushes.LightYellow,
                x, y, width, height);
            formatter.DrawString(note.Text, font, XBrushes.DarkRed,
                new XRect(x + 4, y + 3, width - 8, height - 6));
        }
    }

    private static void RejectSourceOverwrite(string destination, IEnumerable<string> sources)
    {
        if (sources.Any(path => string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Нельзя перезаписать оригинал. Выберите другое имя выходного PDF.");
    }

    private static void WriteAtomically(string destination, Action<string> write,
        Func<int> expectedPageCount, CancellationToken cancellation)
    {
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, ".gupscan-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            write(temp);
            cancellation.ThrowIfCancellationRequested();
            // Never replace a user's existing output with unparseable or incomplete PDF.
            PdfOutputVerifier.Verify(temp, expectedPageCount());
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* Cleanup best effort; never delete the source. */ }
        }
    }
}
