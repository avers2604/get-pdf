namespace GetPdf;

/// <summary>Per-session recovery PNG quota. Does not reserve blocks or limit SDK source storage.</summary>
internal static class RecoveryDiskBudget
{
    internal static long GetWriteAllowance(string folder, long quotaBytes, long reserveBytes,
        Func<string, long>? availableBytes = null)
    {
        if (quotaBytes <= 0) throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        if (reserveBytes < 0) throw new ArgumentOutOfRangeException(nameof(reserveBytes));
        var target = Path.GetFullPath(folder);
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Недопустимый каталог резервных снимков.");
        long used = 0;
        foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.TopDirectoryOnly))
        {
            if (!RecoverySnapshotRetention.IsOwnedSnapshotName(Path.GetFileName(path))) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Недопустимая ссылка в резервных снимках.");
            var length = new FileInfo(path).Length;
            if (length > quotaBytes - used) throw Exhausted();
            used += length;
        }
        long free = availableBytes is null
            ? new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace
            : availableBytes(target);
        if (free <= reserveBytes) throw Exhausted();
        long allowance = Math.Min(quotaBytes - used, free - reserveBytes);
        if (allowance <= 0) throw Exhausted();
        return allowance;
    }

    private static IOException Exhausted() => new(
        "Недостаточно места или исчерпана квота резервных снимков. Сохраните документ вручную.");
}
