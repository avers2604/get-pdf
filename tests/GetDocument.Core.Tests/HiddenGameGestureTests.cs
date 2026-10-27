using GetDocument.Core.EasterEgg;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class HiddenGameGestureTests
{
    [TestMethod]
    public void UnlocksExactlyOnTenthQuickTapAndThenResets()
    {
        var gesture = new HiddenGameGesture();
        var first = new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 9; i++)
            Assert.IsFalse(gesture.Tap(first.AddMilliseconds(i * 300)));
        Assert.IsTrue(gesture.Tap(first.AddMilliseconds(2700)));
        Assert.IsFalse(gesture.Tap(first.AddMilliseconds(3000)));
    }

    [TestMethod]
    public void SlowOrBackwardsTapsResetGesture()
    {
        var gesture = new HiddenGameGesture();
        var first = DateTimeOffset.UtcNow;
        for (int i = 0; i < 9; i++)
            Assert.IsFalse(gesture.Tap(first.AddMilliseconds(i * 100)));
        Assert.IsFalse(gesture.Tap(first.AddSeconds(10)));
        Assert.IsFalse(gesture.Tap(first.AddSeconds(9)));
    }
}
