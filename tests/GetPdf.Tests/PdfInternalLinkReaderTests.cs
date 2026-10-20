using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfInternalLinkReaderTests
{
    [TestMethod]
    public void OnlyLocalGoToLinksAreExposedForNavigation()
    {
        using var pdf = new PdfDocument();
        var p1 = pdf.AddPage();
        var p2 = pdf.AddPage();

        var direct = new PdfDictionary(pdf);
        direct.Elements.SetName("/Subtype", "/Link");
        var destination = new PdfArray(pdf);
        destination.Elements.Add(p2.Reference!);
        direct.Elements.SetObject("/Dest", destination);

        var uri = new PdfDictionary(pdf);
        uri.Elements.SetName("/Subtype", "/Link");
        var action = new PdfDictionary(pdf);
        action.Elements.SetName("/S", "/URI");
        action.Elements.SetString("/URI", "https://example.invalid/");
        uri.Elements.SetObject("/A", action);

        var array = new PdfArray(pdf);
        array.Elements.Add(direct);
        array.Elements.Add(uri);
        p1.Elements.SetObject("/Annots", array);

        var links = PdfInternalLinkReader.Read(pdf);
        Assert.IsTrue(links.TryGetValue(0, out var entries));
        Assert.IsNotNull(entries);
        Assert.HasCount(1, entries);
        Assert.AreEqual(1, entries[0].SourceTargetPage);
    }

    [TestMethod]
    public void EmptyDocumentPagesContainNoLinks()
    {
        using var pdf = new PdfDocument();
        pdf.AddPage();
        Assert.AreEqual(0, PdfInternalLinkReader.Read(pdf).Count);
    }
}
