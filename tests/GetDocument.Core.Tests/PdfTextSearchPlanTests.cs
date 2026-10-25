using GetDocument.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PdfTextSearchPlanTests
{
    [TestMethod]
    public void RussianTextCanBeSearchedWithoutCaseSensitivity()
    {
        string query = PdfTextSearchPlan.NormalizeQuery("  ТРАМВАЙ  ");
        var match = PdfTextSearchPlan.FindInPage(
            2, "На линии трамвай 18. Второй Трамвай ждёт.", query);
        Assert.IsNotNull(match);
        Assert.AreEqual(2, match.PageIndex);
        Assert.AreEqual(2, match.Occurrences);
        StringAssert.Contains(match.Excerpt, "трамвай");
    }

    [TestMethod]
    public void SearchIsLiteralRatherThanRegex()
    {
        string query = PdfTextSearchPlan.NormalizeQuery("A[1].*");
        var found = PdfTextSearchPlan.FindInPage(0, "x A[1].* y", query);
        Assert.IsNotNull(found);
        Assert.AreEqual(1, found.Occurrences);
        Assert.IsNull(PdfTextSearchPlan.FindInPage(0, "A1xxx", query));
    }

    [TestMethod]
    public void EmptyControlAndOversizedQueriesAreRejected()
    {
        foreach (string input in new[] { "", "    ", "\n", "a\r\nb", new string('Я', 129) })
            Assert.ThrowsExactly<ArgumentException>(() => PdfTextSearchPlan.NormalizeQuery(input));
        Assert.ThrowsExactly<ArgumentNullException>(() => PdfTextSearchPlan.NormalizeQuery(null));
    }

    [TestMethod]
    public void NoMatchAndUnusableTextDoNotGenerateResults()
    {
        const string query = "депо";
        Assert.IsNull(PdfTextSearchPlan.FindInPage(0, "диспетчерская", query));
        Assert.IsNull(PdfTextSearchPlan.FindInPage(0, "", query));
        Assert.IsNull(PdfTextSearchPlan.FindInPage(0, null, query));
    }

    [TestMethod]
    public void ExcerptsNeverContainMultilineOrControlCharacters()
    {
        var match = PdfTextSearchPlan.FindInPage(0, "Предыдущая\nстрока\tТРАМВАЙ\r\nследующая",
            PdfTextSearchPlan.NormalizeQuery("трамвай"));
        Assert.IsNotNull(match);
        Assert.IsFalse(match.Excerpt.Contains('\n'));
        Assert.IsFalse(match.Excerpt.Contains('\r'));
        Assert.IsFalse(match.Excerpt.Contains('\t'));
        Assert.IsTrue(match.Excerpt.Length <= PdfTextSearchPlan.MaxExcerptLength);
    }

    [TestMethod]
    public void HugeTextAndFrequentMatchesAreBounded()
    {
        string query = PdfTextSearchPlan.NormalizeQuery("a");
        var match = PdfTextSearchPlan.FindInPage(0, new string('a', 300000), query);
        Assert.IsNotNull(match);
        Assert.AreEqual(PdfTextSearchPlan.MaxMatchesPerPage, match.Occurrences);
        Assert.IsTrue(match.IsPartial);
        Assert.IsTrue(match.Excerpt.Length <= PdfTextSearchPlan.MaxExcerptLength);
    }

    [TestMethod]
    public void InvalidPagesAndInvalidNormalizedQueriesFailClosed()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfTextSearchPlan.FindInPage(-1, "a", "a"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfTextSearchPlan.FindInPage(PdfPreviewPlan.MaxPages, "a", "a"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PdfTextSearchPlan.FindInPage(0, "a", ""));
    }
}
