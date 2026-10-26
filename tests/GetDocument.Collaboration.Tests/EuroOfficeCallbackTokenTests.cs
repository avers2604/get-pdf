using GetDocument.Collaboration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Collaboration.Tests;

[TestClass]
public sealed class EuroOfficeCallbackTokenTests
{
    [TestMethod]
    public void BodyTokenHasPriorityOverPotentiallyDifferentHeaderToken()
    {
        Assert.AreEqual("signed.body", EuroOfficeCallbackToken.Select(
            """{"key":"abc","status":2,"token":"signed.body"}""",
            "Bearer signed.header"));
    }

    [TestMethod]
    public void UsesOnlyAuthorizationBearerHeaderWhenNoBodyTokenExists()
    {
        Assert.AreEqual("signed.header", EuroOfficeCallbackToken.Select(
            """{"key":"abc","status":2}""", "Bearer signed.header"));
    }

    [TestMethod]
    public void MalformedInBodyTokenNeverFallsBackToAValidHeader()
    {
        foreach (string payload in new[] {
            """{"token":null,"key":"abc"}""",
            """{"token":123}""",
            """{"token":""}""",
        })
            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                EuroOfficeCallbackToken.Select(payload, "Bearer signed.header"));
    }

    [TestMethod]
    public void NoJwtAndMalformedJsonFailClosed()
    {
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCallbackToken.Select("""{"status":2}""", null));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            EuroOfficeCallbackToken.Select("{incomplete", "Bearer token"));
    }
}
