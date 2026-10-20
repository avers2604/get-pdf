using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf;

[TestClass]
public sealed class PdfTextIndexTests
{
    [TestMethod]
    public void SearchFindsCyrillicIgnoringCaseAndMapsReorderedPages()
    {
        var index = new PdfTextIndex(new[] { "АКТ о состоянии сети", "служебная записка", "Повторный АКТ осмотра" });
        var plan = new PdfPagePlan(3);
        plan.Move(2, 0);
        var hits = index.Find(plan.Pages, "акт");
        CollectionAssert.AreEqual(new[] { 0, 1 }, hits.Select(h => h.PageIndex).ToArray());
        Assert.IsTrue(hits[0].Snippet.Contains("АКТ", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SearchReturnsNothingForWhitespaceAndDeletedPages()
    {
        var index = new PdfTextIndex(new[] { "Поездка", "Скан" });
        var plan = new PdfPagePlan(2);
        plan.Delete(0);
        Assert.AreEqual(0, index.Find(plan.Pages, "   ").Count);
        Assert.AreEqual(0, index.Find(plan.Pages, "Поездка").Count);
    }

    [TestMethod]
    public void SearchResultSnippetIsBounded()
    {
        var index = new PdfTextIndex(new[] { new string('a', 300) + "целевой" + new string('z', 300) });
        var plan = new PdfPagePlan(1);
        var hit = index.Find(plan.Pages, "целевой").Single();
        Assert.IsTrue(hit.Snippet.Length <= 126);
    }
}
