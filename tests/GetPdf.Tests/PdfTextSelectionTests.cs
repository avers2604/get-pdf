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
    public void OrdinaryTightA4LineSpacingMustCopyAsSeparateLines()
    {
        var model = new PdfWordSelection([
            new PdfSelectableWord("Управление", new(.10, .110, .19, .012)),
            new PdfSelectableWord("ГЭТ", new(.31, .111, .06, .012)),
            new PdfSelectableWord("Документ", new(.10, .130, .19, .012)),
            new PdfSelectableWord("номер", new(.31, .131, .10, .012))
        ]);
        Assert.AreEqual("Управление ГЭТ\nДокумент номер", model.Extract(0, 3));
    }

    [TestMethod]
    public void GlyphSelectionCopiesExactlyChosenLettersWithoutWordInsertedSpaces()
    {
        var glyphs = new PdfWordSelection([
            new PdfSelectableWord("Т", new(.10, .10, .015, .02)),
            new PdfSelectableWord("р", new(.12, .10, .015, .02), JoinWithPrevious: true),
            new PdfSelectableWord("а", new(.14, .10, .015, .02), JoinWithPrevious: true),
            new PdfSelectableWord("м", new(.16, .10, .015, .02), JoinWithPrevious: true),
            new PdfSelectableWord("вай", new(.20, .10, .045, .02), JoinWithPrevious: true),
            new PdfSelectableWord("СПб", new(.29, .10, .05, .02))
        ]);
        Assert.AreEqual("рам", glyphs.Extract(1, 3));
        Assert.AreEqual("Трамвай СПб", glyphs.Extract(0, 5));
        Assert.AreEqual("рам", glyphs.Extract(3, 1));
    }

    [TestMethod]
    public void GlyphSelectionPreservesLineBreakEvenForJoinedFragments()
    {
        var glyphs = new PdfWordSelection([
            new PdfSelectableWord("A", new(.1, .1, .02, .015)),
            new PdfSelectableWord("B", new(.1, .125, .02, .015), JoinWithPrevious: true)
        ]);
        Assert.AreEqual("A\\nB".Replace("\\n", "\n"), glyphs.Extract(0, 1));
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
    public void ExactCopyLimitDoesNotRejectJoinedGlyphs()
    {
        var words = Enumerable.Range(0, 100).Select(_ =>
            new PdfSelectableWord(new string('Ж', 500),
                new(.1, .1, .01, .01), JoinWithPrevious: true)).ToArray();
        var selection = new PdfWordSelection(words);
        string copy = selection.Extract(0, 99);
        Assert.AreEqual(PdfWordSelection.MaxCopiedChars, copy.Length);
        Assert.IsTrue(copy.All(ch => ch == 'Ж'));
        // A real separator counts towards the same bound.
        words[99] = words[99] with { JoinWithPrevious = false };
        Assert.AreEqual("", new PdfWordSelection(words).Extract(0, 99));
    }

    [TestMethod]
    public void ExactCopyLimitAllowsSingleWordAndRejectsOneMore()
    {
        var words = Enumerable.Range(0, 100).Select(_ =>
            new PdfSelectableWord(new string('x', 500),
                new(.1, .1, .01, .01), JoinWithPrevious: true)).ToList();
        Assert.AreEqual(50000, new PdfWordSelection(words).Extract(0, 99).Length);
        words.Add(new PdfSelectableWord("x", new(.1, .1, .01, .01),
            JoinWithPrevious: true));
        Assert.AreEqual("", new PdfWordSelection(words).Extract(0, 100));
    }

    [TestMethod]
    public void CancelledWordExtractionDoesNotOpenOrParseAnyPdf()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            PdfTextSelectionReader.Read(
                Path.Combine(Path.GetTempPath(), "not-real.pdf"),
                0, 0, cancel.Token));
    }

    [TestMethod]
    public void SingleWordAndEmptyDocumentAreSafe()
    {
        Assert.AreEqual("", new PdfWordSelection([]).Extract(0, 0));
        var selection = new PdfWordSelection([new("Трамвай", new(.1, .1, .2, .1))]);
        Assert.AreEqual("Трамвай", selection.Extract(0, 0));
    }
}
