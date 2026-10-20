using System.Windows.Media.Imaging;
namespace GetPdf;
/// <summary>Creates temporary PNGs solely for WPF PDF printing, not scanning.</summary>
internal static class RecoverySession
{
    public static async Task<string> SnapshotAsync(string folder, BitmapSource image)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var policy = WorkstationPolicy.Current;
        long allowance = RecoveryDiskBudget.GetWriteAllowance(folder,
            policy.MaxRecoverySessionMiB * 1024L * 1024L, policy.RecoveryReserveMiB * 1024L * 1024L);
        string name = "recovery-" + Guid.NewGuid().ToString("N") + ".png";
        string file = Path.Combine(folder, name);
        BitmapSource frozen = image.Clone();
        frozen.Freeze();
        await Task.Run(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frozen));
            try
            {
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var bounded = new RecoveryBoundedStream(stream, allowance, leaveOpen: true);
                encoder.Save(bounded);
                bounded.Flush();
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        });
        return name;
    }
}
