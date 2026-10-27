using GetDocument.WopiGateway;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.WopiGateway.Tests;

[TestClass]
public sealed class WopiHostModeTests
{
    [TestMethod]
    public void DefaultRemainsReadOnlyAndDoesNotPermitWrites()
    {
        Assert.AreEqual(WopiServerMode.ReadOnly, WopiHostMode.Resolve(null, false));
        Assert.AreEqual(WopiServerMode.ReadOnly, WopiHostMode.Resolve("", false));
        Assert.AreEqual(WopiServerMode.ReadOnly, WopiHostMode.Resolve("readonly", false));
    }

    [TestMethod]
    public void LiveEditRequiresAnExplicitlyAuthorizedProvider()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            WopiHostMode.Resolve("realtime", false));
        Assert.AreEqual(WopiServerMode.Realtime, WopiHostMode.Resolve("realtime", true));
    }

    [TestMethod]
    public void UnknownModesFailClosed()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WopiHostMode.Resolve("unsafe", true));
        Assert.ThrowsExactly<ArgumentException>(() => WopiHostMode.Resolve("demo", false));
    }
}
