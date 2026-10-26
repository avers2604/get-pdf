using System.Text;
using GetDocument.Desktop.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Printing.Tests;

[TestClass]
public sealed class BoundedProcessOutputTests
{
    [TestMethod]
    public async Task ShortOutputIsPreservedExactly()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("request id is GET-42\n"));
        using var reader = new StreamReader(input);
        var result = await BoundedProcessOutput.ReadAsync(reader, 100, CancellationToken.None);
        Assert.AreEqual("request id is GET-42\n", result.Value);
        Assert.IsFalse(result.WasTruncated);
    }

    [TestMethod]
    public async Task UnboundedDriverOutputIsFullyDrainedButNeverBuffered()
    {
        var huge = new string('x', 512 * 1024);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(huge));
        using var reader = new StreamReader(input);
        var result = await BoundedProcessOutput.ReadAsync(reader, 1024, CancellationToken.None);
        Assert.AreEqual(1024, result.Value.Length);
        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual(input.Length, input.Position);
    }

    [TestMethod]
    public async Task LimitExactlyMetIsNotReportedAsTruncated()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 4096)));
        using var reader = new StreamReader(input);
        var result = await BoundedProcessOutput.ReadAsync(reader, 4096, CancellationToken.None);
        Assert.AreEqual(4096, result.Value.Length);
        Assert.IsFalse(result.WasTruncated);
    }

    [TestMethod]
    public async Task CancellationIsPropagatedBeforeRead()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        using var reader = new StreamReader(input);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            BoundedProcessOutput.ReadAsync(reader, 64, cancellation.Token));
    }
}
