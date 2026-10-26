using System.Text;

namespace GetDocument.Desktop.Printing;

/// <summary>
/// Drains a potentially untrusted printer command's stdout or stderr without
/// retaining unbounded driver output or blocking the pipe on overflow.
/// Callers decide whether a truncated response is an error.
/// </summary>
internal sealed record ProcessOutput(string Value, bool WasTruncated);

internal static class BoundedProcessOutput
{
    internal static async Task<ProcessOutput> ReadAsync(
        StreamReader reader, int maxChars, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxChars is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxChars));

        var result = new StringBuilder(Math.Min(maxChars, 4096));
        char[] buffer = new char[2048];
        bool truncated = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) break;
            int remaining = Math.Max(0, maxChars - result.Length);
            int keep = Math.Min(read, remaining);
            if (keep != 0) result.Append(buffer, 0, keep);
            if (keep != read) truncated = true;
        }
        return new ProcessOutput(result.ToString(), truncated);
    }
}
