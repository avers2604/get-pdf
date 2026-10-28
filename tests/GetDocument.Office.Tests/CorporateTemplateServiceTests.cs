using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GetDocument.Office.Templates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class CorporateTemplateServiceTests
{
    private static CorporateTemplateData Sample(string content = "Основной текст") =>
        new("Трамвайный парк №5", "Иванов И.И.", "Начальнику отдела",
            "Обеспечение", content, "42", new DateOnly(2026, 10, 23));

    [TestMethod]
    public void CatalogHasThreeUniqueNamedTemplates()
    {
        var templates = CorporateTemplateService.All;
        Assert.AreEqual(3, templates.Count);
        Assert.AreEqual(3, templates.Select(x => x.Id).Distinct().Count());
        Assert.IsTrue(templates.All(x => !string.IsNullOrWhiteSpace(x.Name)));
    }

    [DataTestMethod]
    [DataRow(CorporateTemplateId.ServiceMemo)]
    [DataRow(CorporateTemplateId.Act)]
    [DataRow(CorporateTemplateId.Letter)]
    public void CreatesValidDocxWithoutLosingRussianFields(CorporateTemplateId type)
    {
        using var folder = Scratch.Create();
        string output = Path.Combine(folder.Path, "Документ.docx");
        CorporateTemplateService.CreateDocx(type, Sample(), output);
        using var package = WordprocessingDocument.Open(output, false);
        var lines = package.MainDocumentPart!.Document.Body!
            .Descendants<Text>().Select(x => x.Text).ToArray();
        var all = string.Join(" ", lines);
        StringAssert.Contains(all, "Трамвайный парк №5");
        StringAssert.Contains(all, "Обеспечение");
        StringAssert.Contains(all, "23.10.2026");
        Assert.IsTrue(new FileInfo(output).Length > 0);
    }

    [DataTestMethod]
    [DataRow(CorporateTemplateId.ServiceMemo)]
    [DataRow(CorporateTemplateId.Act)]
    [DataRow(CorporateTemplateId.Letter)]
    public void PilotTemplateUsesA4AndLegibleTitleWithoutChangingFieldContents(CorporateTemplateId type)
    {
        using var folder = Scratch.Create();
        string output = Path.Combine(folder.Path, "A4.docx");
        CorporateTemplateService.CreateDocx(type, Sample("Первая строка\nВторая строка"), output);

        using var package = WordprocessingDocument.Open(output, false);
        var body = package.MainDocumentPart!.Document.Body!;
        var properties = body.Elements<SectionProperties>().Single();
        var paper = properties.GetFirstChild<PageSize>()!;
        var margins = properties.GetFirstChild<PageMargin>()!;
        Assert.AreEqual(11906U, paper.Width!.Value, "A4 width in twips");
        Assert.AreEqual(16838U, paper.Height!.Value, "A4 height in twips");
        Assert.IsTrue(margins.Left!.Value >= 1134U, "A4 pilot template must have readable left margin");
        Assert.IsTrue(margins.Right!.Value >= 850U);

        var paragraphs = body.Elements<Paragraph>().ToArray();
        var title = paragraphs[0];
        Assert.AreEqual(JustificationValues.Center,
            title.ParagraphProperties?.Justification?.Val?.Value);
        Assert.IsNotNull(title.Descendants<Bold>().FirstOrDefault(), "Document title must be bold");
        Assert.IsTrue(paragraphs.Any(x => x.InnerText == "Первая строка"));
        Assert.IsTrue(paragraphs.Any(x => x.InnerText == "Вторая строка"));
    }

    [TestMethod]
    public void A4PilotTemplateDoesNotEmbedUnapprovedLogoOrExternalAssets()
    {
        using var folder = Scratch.Create();
        string output = Path.Combine(folder.Path, "Акт.docx");
        CorporateTemplateService.CreateDocx(CorporateTemplateId.Act, Sample(), output);
        using var package = WordprocessingDocument.Open(output, false);
        Assert.AreEqual(0, package.MainDocumentPart!.ImageParts.Count(),
            "Official brand assets are not yet approved for these pilot templates");
        Assert.IsFalse(package.MainDocumentPart.ExternalRelationships.Any());
    }

    [TestMethod]
    public void UserProvidedTemplateSyntaxIsRenderedAsPlainTextNotEvaluated()
    {
        var input = Sample("Символы: {{ 7 * 7 }}");
        string result = CorporateTemplateService.Preview(CorporateTemplateId.ServiceMemo, input);
        StringAssert.Contains(result, "{{ 7 * 7 }}");
        Assert.IsFalse(result.Contains("Символы: 49", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NeverOverwritesExistingCorporateFile()
    {
        using var folder = Scratch.Create();
        string dest = Path.Combine(folder.Path, "Оригинал.docx");
        File.WriteAllText(dest, "WORM");
        Assert.ThrowsExactly<IOException>(() =>
            CorporateTemplateService.CreateDocx(CorporateTemplateId.Act, Sample(), dest));
        Assert.AreEqual("WORM", File.ReadAllText(dest));
    }

    [TestMethod]
    public void RejectsInvalidValuesWithoutCreatingFiles()
    {
        using var folder = Scratch.Create();
        string dest = Path.Combine(folder.Path, "output.docx");
        Assert.ThrowsExactly<ArgumentException>(() =>
            CorporateTemplateService.CreateDocx(
                CorporateTemplateId.Letter, Sample() with { Department = "" }, dest));
        Assert.IsFalse(File.Exists(dest));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CorporateTemplateService.CreateDocx(
                CorporateTemplateId.Letter, Sample() with { Content = "abc\u0001" }, dest));
        Assert.IsFalse(File.Exists(dest));
    }

    [TestMethod]
    public void MultilineContentIsPersistedAsSeparateParagraphs()
    {
        using var folder = Scratch.Create();
        string dest = Path.Combine(folder.Path, "Текст.docx");
        CorporateTemplateService.CreateDocx(
            CorporateTemplateId.ServiceMemo, Sample("Строка 1\nСтрока 2"), dest);
        using var package = WordprocessingDocument.Open(dest, false);
        var lines = package.MainDocumentPart!.Document.Body!
            .Elements<Paragraph>().Select(p => p.InnerText).ToArray();
        Assert.IsTrue(lines.Contains("Строка 1"));
        Assert.IsTrue(lines.Contains("Строка 2"));
    }

    [TestMethod]
    public void RejectsWrongExtensionAndDoesNotCreateDirectories()
    {
        using var folder = Scratch.Create();
        Assert.ThrowsExactly<ArgumentException>(() =>
            CorporateTemplateService.CreateDocx(
                CorporateTemplateId.ServiceMemo, Sample(),
                Path.Combine(folder.Path, "bad.pdf")));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            CorporateTemplateService.CreateDocx(
                CorporateTemplateId.ServiceMemo, Sample(),
                Path.Combine(folder.Path, "missing", "new.docx")));
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; }
        private Scratch(string path) { Path = path; Directory.CreateDirectory(path); }
        public static Scratch Create() => new(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "get-template-" + Guid.NewGuid().ToString("N")));
        public void Dispose() { Directory.Delete(Path, recursive: true); }
    }
}
