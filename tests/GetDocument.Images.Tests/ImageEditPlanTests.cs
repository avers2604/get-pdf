using GetDocument.Images;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Images.Tests;

[TestClass]
public sealed class ImageEditPlanTests
{
    [TestMethod]
    public void RotationChangesCanvasDimensions()
    {
        var plan = new ImageEditPlan(ClockwiseQuarterTurns: 1);
        var bounds = plan.ValidateAndCalculate(1200, 800);
        Assert.AreEqual(800, bounds.Width);
        Assert.AreEqual(1200, bounds.Height);
    }

    [TestMethod]
    public void TwoQuarterTurnsPreserveDimensions()
    {
        var plan = new ImageEditPlan(ClockwiseQuarterTurns: 2, FlipHorizontal: true);
        var bounds = plan.ValidateAndCalculate(1600, 900);
        Assert.AreEqual(1600, bounds.Width);
        Assert.AreEqual(900, bounds.Height);
    }

    [TestMethod]
    public void CropIsRelativeToRotatedCanvas()
    {
        var plan = new ImageEditPlan(ClockwiseQuarterTurns: 1,
            Crop: new ImageCrop(10, 20, 300, 400));
        var bounds = plan.ValidateAndCalculate(1200, 800);
        Assert.AreEqual(300, bounds.Width);
        Assert.AreEqual(400, bounds.Height);
    }

    [TestMethod]
    public void CropCannotEscapeCanvas()
    {
        var plan = new ImageEditPlan(Crop: new ImageCrop(900, 0, 300, 400));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => plan.ValidateAndCalculate(1000, 600));
        var empty = new ImageEditPlan(Crop: new ImageCrop(0, 0, 0, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => empty.ValidateAndCalculate(100, 100));
    }

    [TestMethod]
    public void InvalidRotationAndPixelBudgetsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ImageEditPlan(ClockwiseQuarterTurns: 4).ValidateAndCalculate(100, 200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ImageEditPlan().ValidateAndCalculate(0, 200));
        Assert.ThrowsExactly<InvalidDataException>(
            () => new ImageEditPlan().ValidateAndCalculate(20000, 18000));
    }

    [TestMethod]
    public void RefusesUnknownOrDangerousImageExtensions()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ImageEditPlan.ValidateSourceExtension("danger.svg"));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ImageEditPlan.ValidateSourceExtension("multi-page.tiff"));
        ImageEditPlan.ValidateSourceExtension("image.png");
        ImageEditPlan.ValidateSourceExtension("photo.JPG");
        ImageEditPlan.ValidateSourceExtension("image.webp");
    }

    [TestMethod]
    public void SaveAsAllowsPngAndJpegButRejectsOtherDestinationTypes()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            ImageEditPlan.ValidateDestinationExtension("revised.pdf"));
        ImageEditPlan.ValidateDestinationExtension("revised.png");
        ImageEditPlan.ValidateDestinationExtension("revised.jpg");
        ImageEditPlan.ValidateDestinationExtension("revised.JPEG");
    }

    [TestMethod]
    public void OperationsNotRequestedAreTreatedAsNoChange()
    {
        Assert.IsFalse(new ImageEditPlan().HasChanges);
        Assert.IsTrue(new ImageEditPlan(FlipHorizontal: true).HasChanges);
        Assert.IsTrue(new ImageEditPlan(Crop: new ImageCrop(0, 0, 20, 20)).HasChanges);
    }
}
