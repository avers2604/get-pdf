using GetDocument.Revisions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Revisions.Tests;

[TestClass]
public sealed class RevisionDiffTests
{
    [TestMethod]
    public void ComparesCyrillicLinesUsingExistingDiffPlex()
    {
        var changes = RevisionTextDiff.Compare("Приказ\nСтарый пункт", "Приказ\nНовый пункт");
        Assert.IsTrue(changes.Any(x => x.Kind == RevisionLineKind.Deleted && x.Text == "Старый пункт"));
        Assert.IsTrue(changes.Any(x => x.Kind == RevisionLineKind.Inserted && x.Text == "Новый пункт"));
        Assert.IsTrue(changes.Any(x => x.Kind == RevisionLineKind.Unchanged && x.Text == "Приказ"));
    }

    [TestMethod]
    public void WhitespaceChangesAreNotSilentlyDiscarded()
    {
        var changes = RevisionTextDiff.Compare("А Б", "А  Б");
        Assert.IsTrue(changes.Any(x => x.Kind != RevisionLineKind.Unchanged));
    }

    [TestMethod]
    public void RefusesOversizedDiffInputs()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RevisionTextDiff.Compare(new string('x', 120_000), "small"));
    }

    [TestMethod]
    public void EmptyDocumentsCanBeCompared()
    {
        Assert.AreEqual(0, RevisionTextDiff.Compare("", "").Count);
    }
}
