using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace GetPdf.Tests;

[TestClass]
public sealed class PdfCacheMemoryBudgetTests
{
    [TestMethod]
    public void EvictsByCostWithoutExceedingLimit()
    {
        var freed = new List<string>();
        using var cache = new PdfLruCache<int, string>(10, freed.Add, 5, s => s.Length);
        cache.Add(1, "aa");
        cache.Add(2, "bb");
        Assert.AreEqual(4L, cache.EstimatedCost);
        cache.Add(3, "ccc");
        Assert.AreEqual(5L, cache.EstimatedCost);
        Assert.IsFalse(cache.TryGet(1, out _));
        Assert.IsTrue(cache.TryGet(2, out _));
        CollectionAssert.AreEqual(new[] { "aa" }, freed.ToArray());
    }

    [TestMethod]
    public void OversizedElementDoesNotCorruptCache()
    {
        using var cache = new PdfLruCache<int, string>(3, _ => { }, 2, s => s.Length);
        cache.Add(1, "a");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => cache.Add(2, "abc"));
        Assert.AreEqual(1L, cache.EstimatedCost);
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void DuplicateKeyRetainsOneValue()
    {
        var discarded = new List<string>();
        using (var cache = new PdfLruCache<int, string>(3, discarded.Add, 5, s => s.Length))
        {
            cache.Add(1, "a");
            cache.Add(1, "abc");
            Assert.AreEqual(3L, cache.EstimatedCost);
            Assert.AreEqual(1, cache.Count);
        }
        CollectionAssert.AreEqual(new[] { "a", "abc" }, discarded.ToArray());
    }
}