using System.Security.Cryptography;
using NetVips;

namespace GetDocument.Images;

/// <summary>
/// Source-first image editor (libvips). New PNG/JPEG revisions are written
/// in privately leased staging on the destination volume and published using
/// a non-overwriting rename. Originals/WORM files are never overwritten.
/// Native decoding requires an isolated process before release.
/// </summary>
public static class ImageEditService
{
    public const long MaxInputBytes = 32L * 1024 * 1024;

    public static string FingerprintSource(string path)
    {
        ImageEditPlan.ValidateSourceExtension(path);
        string full = Path.GetFullPath(path);
        using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaxInputBytes)
            throw new InvalidDataException("Изображение пусто или превышает лимит 32 МиБ.");
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static void SaveNewRevision(
        string source, string destination, ImageEditPlan plan, string? expectedSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ImageEditPlan.ValidateSourceExtension(source);
        ImageEditPlan.ValidateDestinationExtension(destination);
        string input = Path.GetFullPath(source);
        string target = Path.GetFullPath(destination);
        StringComparison comparer = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(input, target, comparer) || File.Exists(target) || Directory.Exists(target))
            throw new IOException("Существующий файл или оригинал нельзя перезаписывать.");
        if (!plan.HasChanges)
            throw new ArgumentException("Для новой редакции выберите операцию над изображением.", nameof(plan));

        string before = FingerprintSource(input);
        if (expectedSha256 is not null &&
            !string.Equals(before, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Изображение изменилось после открытия. Перечитайте оригинал.");

        string folder = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Папка назначения не найдена.");

        // Cleanup is limited to old, verified GET Document scratch in this
        // exact output folder. The new operation holds an exclusive lease.
        using var scratch = ImageExportScratch.Create(folder, Path.GetExtension(target));

        // Random access is intentional: a 90° rotation/flip requires it.
        using var image = NetVips.Image.NewFromFile(input,
            access: Enums.Access.Random, failOn: Enums.FailOn.Error, revalidate: true);
        _ = plan.ValidateAndCalculate(image.Width, image.Height);

        var intermediates = new List<NetVips.Image>();
        NetVips.Image current = image;
        try
        {
            if (plan.ClockwiseQuarterTurns != 0)
            {
                current = current.Rot(plan.ClockwiseQuarterTurns switch
                {
                    1 => Enums.Angle.D90,
                    2 => Enums.Angle.D180,
                    _ => Enums.Angle.D270
                });
                intermediates.Add(current);
            }
            if (plan.FlipHorizontal)
            {
                current = current.Flip(Enums.Direction.Horizontal);
                intermediates.Add(current);
            }
            if (plan.Crop is { } crop)
            {
                current = current.ExtractArea(crop.X, crop.Y, crop.Width, crop.Height);
                intermediates.Add(current);
            }

            // libvips selects the codec by scratch extension: PNG is lossless,
            // JPEG is lossy. Never publish an incomplete encoding.
            current.WriteToFile(scratch.StagingPath);
        }
        finally
        {
            for (int i = intermediates.Count - 1; i >= 0; --i)
                intermediates[i].Dispose();
        }

        // Detect external modifications during long native image processing.
        if (!string.Equals(FingerprintSource(input), before, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Оригинал изменился во время обработки, сохранение отменено.");

        File.Move(scratch.StagingPath, target, overwrite: false);
        // On process termination before this move, only an owned scratch
        // remains. On termination after the move, the final file survives.
    }
}
