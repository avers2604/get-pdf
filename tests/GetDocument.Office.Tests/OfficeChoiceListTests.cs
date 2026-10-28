using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class OfficeChoiceListTests
{
    [TestMethod]
    public void ParsesRussianItemsAndPreservesUserOrder()
    {
        string[] items = OfficeChoiceList.ParseLines("В работе\r\nЗавершено\nНа проверке");
        CollectionAssert.AreEqual(new[] { "В работе", "Завершено", "На проверке" }, items);
    }

    [TestMethod]
    public void TrimsWhitespaceButRejectsRepeatedValues()
    {
        var items = OfficeChoiceList.ParseLines(" В работе \n Завершено ");
        CollectionAssert.AreEqual(new[] { "В работе", "Завершено" }, items);
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeChoiceList.ParseLines("В работе\nв РАБОТЕ"));
    }

    [TestMethod]
    public void RejectsEmptyTooFewTooManyAndOversized()
    {
        Assert.ThrowsExactly<ArgumentException>(() => OfficeChoiceList.ParseLines(""));
        Assert.ThrowsExactly<ArgumentException>(() => OfficeChoiceList.ParseLines("один"));
        Assert.ThrowsExactly<ArgumentException>(() => OfficeChoiceList.ParseLines("A\n"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeChoiceList.ParseLines(string.Join("\n", Enumerable.Range(0, 21).Select(x => $"Пункт {x}"))));
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeChoiceList.ParseLines("A\n" + new string('Б', 41)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeChoiceList.ParseLines(string.Join("\n",
                Enumerable.Range(0, 8).Select(x => new string((char)('A' + x), 35)))));
    }

    [DataTestMethod]
    [DataRow("A\n=HYPERLINK(1)")]
    [DataRow("A\n+SUM(1)")]
    [DataRow("A\n@command")]
    [DataRow("A\n-B5")]
    [DataRow("A\nB;C")]
    [DataRow("A\nB\"C")]
    [DataRow("A\nB\tC")]
    [DataRow("A\nB\u0001C")]
    public void RejectsFormulaInjectionAndSyntaxMarkers(string lines)
    {
        Assert.ThrowsExactly<ArgumentException>(() => OfficeChoiceList.ParseLines(lines));
    }

    [TestMethod]
    public void ValidatedOptionsAreImmutableFromCallerPointOfView()
    {
        string[] source = ["План", "Факт"];
        var validated = OfficeChoiceList.Validate(source);
        source[0] = "ЗАМЕНА";
        Assert.AreEqual("План", validated[0]);
    }

    [TestMethod]
    public void NewFeaturesAreLimitedToWritableCalc()
    {
        foreach (string id in new[] { "calc-pivot-sum", "calc-validate-choice-list" })
        {
            Assert.IsTrue(OfficeFeatureCatalog.Require("calc", id, false).RequiresEdit);
            Assert.ThrowsExactly<InvalidOperationException>(() => OfficeFeatureCatalog.Require("calc", id, true));
            Assert.ThrowsExactly<NotSupportedException>(() => OfficeFeatureCatalog.Require("writer", id, false));
        }
    }

    [TestMethod]
    public void NonListCommandsMustNotAcceptChoicePayload()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeFeatureCatalog.ValidateOptions("calc-pivot-sum", ["Первый", "Второй"]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            OfficeFeatureCatalog.ValidateOptions("calc-validate-choice-list", null));
        CollectionAssert.AreEqual(new[] { "Первый", "Второй" },
            OfficeFeatureCatalog.ValidateOptions("calc-validate-choice-list", ["Первый", "Второй"])!.ToArray());
    }
}
