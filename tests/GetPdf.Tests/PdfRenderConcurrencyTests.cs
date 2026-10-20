using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfRenderConcurrencyTests
{
    [TestMethod]
    public async Task LimitsConcurrentNativePdfImportsToTwoAcrossTabs()
    {
        using var one = await PdfRenderConcurrency.EnterAsync();
        using var two = await PdfRenderConcurrency.EnterAsync();
        var third = PdfRenderConcurrency.EnterAsync();
        Assert.IsFalse(third.IsCompleted,
            "A third simultaneous importer must not start while both permits are in use.");
        one.Dispose();
        using var next = await third.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public async Task CancellationNeverConsumesAnotherImportPermit()
    {
        using var one = await PdfRenderConcurrency.EnterAsync();
        using var two = await PdfRenderConcurrency.EnterAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            using var _ = await PdfRenderConcurrency.EnterAsync(canceled.Token);
        });
    }
}
