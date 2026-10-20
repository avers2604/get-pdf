using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfTextSelectionTests
{
    [TestMethod]
    public void DragSelectsWordsInTextOrderAndFormatsParagraphs()
    {
        var model = new PdfWordSelection([
            new PdfSelectableWord("Первый", new(.1, .1, .13, .03)),
            new PdfSelectableWord("тест", new(.25, .1, .1, .03)),
            new PdfSelectableWord("Строка", new(.1, .2, .15, .03))
        ]);
        Assert.AreEqual(0, model.HitTest(.13, .12));
        Assert.AreEqual(2, model.HitTest(.14, .22));
        Assert.AreEqual(-1, model.HitTest(.85, .85));
        Assert.AreEqual("Первый тест\nСтрока", model.Extract(0, 2));
        Assert.AreEqual("тест\nСтрока", model.Extract(2, 1));
    }

    [TestMethod]
    public void RejectsInvalidWordsAndOversizedSelection()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PdfWordSelection([
            new PdfSelectableWord("bad", new(-1, 0, .1, .1))
        ]));
        Assert.ThrowsExactly<ArgumentException>(() => new PdfWordSelection([
            new PdfSelectableWord("line\nsecret", new(.1, .1, .1, .1))
        ]));
        var words = Enumerable.Range(0, 110).Select(i =>
            new PdfSelectableWord(new string('x', 500),
                new(.01, .01, .01, .01))).ToArray();
        var selection = new PdfWordSelection(words);
        Assert.AreEqual("", selection.Extract(0, words.Length - 1));
    }

    [TestMethod]
    public void SingleWordAndEmptyDocumentAreSafe()
    {
        Assert.AreEqual("", new PdfWordSelection([]).Extract(0, 0));
        var selection = new PdfWordSelection([new("Трамвай", new(.1, .1, .2, .1))]);
        Assert.AreEqual("Трамвай", selection.Extract(0, 0));
    }
}
