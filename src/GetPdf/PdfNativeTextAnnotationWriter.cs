using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

namespace GetPdf;

/// <summary>
/// Conservative opt-in export of native /Text, /Square and /Highlight annotations.
/// A fresh PDF is produced without changing the source. Existing annotations,
/// actions, forms, signatures, attachments and other complex objects fail closed.
/// Square/highlight have explicit vector /AP streams; verify in external viewers.
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
            throw new InvalidDataException("Выберите от 1 до 24 PDF-аннотаций.");
        // Only the three audited native PDF annotation types are allowed.
        if (drafts.Any(d => d is null || !d.IsValid ||
            d.Kind is not ("text" or "square" or "highlight")))
            throw new NotSupportedException("Неподдерживаемый тип PDF-аннотации.");
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
                    if (spec.Kind == "text")
                    {
                        var note = new PdfTextAnnotation
                        {
                            Contents = spec.Text,
                            Rectangle = new PdfRectangle(new PdfSharp.Drawing.XRect(
                                spec.X0, spec.Y0, spec.X1 - spec.X0, spec.Y1 - spec.Y0))
                        };
                        revised.Pages[spec.PageIndex].Annotations.Add(note);
                    }
                    else
                    {
                        AddShape(revised, revised.Pages[spec.PageIndex], spec);
                    }
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
                        string expectedSubtype = expected[j].Kind switch
                        {
                            "text" => "/Text",
                            "square" => "/Square",
                            "highlight" => "/Highlight",
                            _ => throw new InvalidDataException("Неизвестная аннотация.")
                        };
                        if (dict is null || dict.Elements.GetName("/Subtype") != expectedSubtype)
                            throw new InvalidDataException("Тип PDF-аннотации изменился при сохранении.");
                        if (expected[j].Kind == "text")
                        {
                            if (dict.Elements.GetString("/Contents") != expected[j].Text)
                                throw new InvalidDataException("Текст PDF-комментария изменился.");
                        }
                        else
                        {
                            var appearance = dict.Elements.GetDictionary("/AP");
                            if (appearance?.Elements.GetObject("/N") is not PdfDictionary form ||
                                form.Stream is null || form.Stream.Length == 0)
                                throw new InvalidDataException("PDF-аннотация не имеет визуального представления.");
                            if (expected[j].Kind == "highlight" &&
                                dict.Elements.GetArray("/QuadPoints")?.Elements.Count != 8)
                                throw new InvalidDataException("Повреждены координаты выделения PDF.");
                        }
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

    // PDFsharp 6.2.4 exposes PdfDictionary.CreateStream and Internals.AddObject.
    // /AP uses *local* BBox coordinates, never the absolute /Rect PDF positions.
    private static void AddShape(PdfDocument pdf, PdfPage page, PdfNativeAnnotationSpec spec)
    {
        double w = spec.X1 - spec.X0, h = spec.Y1 - spec.Y0;
        if (w <= 0 || h <= 0 || !double.IsFinite(w) || !double.IsFinite(h))
            throw new InvalidDataException("Недопустимая геометрия PDF-аннотации.");
        bool highlight = spec.Kind == "highlight";
        var appearance = new PdfDictionary(pdf);
        appearance.Elements.SetName("/Type", "/XObject");
        appearance.Elements.SetName("/Subtype", "/Form");
        appearance.Elements["/BBox"] = new PdfArray(pdf,
            new PdfReal(0), new PdfReal(0), new PdfReal(w), new PdfReal(h));
        var resources = new PdfDictionary(pdf);
        string commands;
        if (highlight)
        {
            // A transparent yellow Form XObject using normal Multiply blend;
            // text underneath remains visible rather than being painted over.
            var state = new PdfDictionary(pdf);
            state.Elements.SetName("/Type", "/ExtGState");
            state.Elements["/ca"] = new PdfReal(0.35);
            state.Elements.SetName("/BM", "/Multiply");
            var states = new PdfDictionary(pdf);
            states.Elements.SetObject("/GS1", state);
            resources.Elements.SetObject("/ExtGState", states);
            commands = FormattableString.Invariant(
                $"q /GS1 gs 1 0.85 0 rg 0 0 {w:0.###} {h:0.###} re f Q");
        }
        else
        {
            double inset = Math.Min(1.5, Math.Min(w, h) / 6);
            commands = FormattableString.Invariant(
                $"q 0.78 0.063 0.18 RG 1.5 w {inset:0.###} {inset:0.###} " +
                $"{Math.Max(0, w - 2 * inset):0.###} {Math.Max(0, h - 2 * inset):0.###} re S Q");
        }
        appearance.Elements.SetObject("/Resources", resources);
        appearance.CreateStream(Encoding.ASCII.GetBytes(commands));
        pdf.Internals.AddObject(appearance);

        var annot = new PdfDictionary(pdf);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", highlight ? "/Highlight" : "/Square");
        annot.Elements["/Rect"] = new PdfRectangle(
            new PdfSharp.Drawing.XRect(spec.X0, spec.Y0, w, h));
        annot.Elements["/F"] = new PdfInteger(4); // print annotation
        annot.Elements["/C"] = highlight
            ? new PdfArray(pdf, new PdfReal(1), new PdfReal(0.85), new PdfReal(0))
            : new PdfArray(pdf, new PdfReal(0.78), new PdfReal(0.063), new PdfReal(0.18));
        if (highlight)
        {
            // PDF QuadPoints: top-left, top-right, bottom-left, bottom-right.
            annot.Elements["/QuadPoints"] = new PdfArray(pdf,
                new PdfReal(spec.X0), new PdfReal(spec.Y1),
                new PdfReal(spec.X1), new PdfReal(spec.Y1),
                new PdfReal(spec.X0), new PdfReal(spec.Y0),
                new PdfReal(spec.X1), new PdfReal(spec.Y0));
        }
        var ap = new PdfDictionary(pdf);
        ap.Elements.SetReference("/N", appearance);
        annot.Elements.SetObject("/AP", ap);
        pdf.Internals.AddObject(annot);
        page.Annotations.Elements.Add(annot.ReferenceNotNull);
    }
}
