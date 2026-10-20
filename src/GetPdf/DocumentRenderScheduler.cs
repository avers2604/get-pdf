using System.Windows.Media.Imaging;

namespace GetPdf;

internal readonly record struct DocumentRenderKey(int Page, long Revision, int PixelWidth);

/// <summary>Single reader, bounded detached bitmap cache. Invalidation never disposes an in-flight source.</summary>
internal sealed class DocumentRenderScheduler(int maxImages = 6, long maxBytes = 128L * 1024 * 1024)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<(DocumentRenderKey Key, BitmapSource Bitmap, long Bytes)> _cache = new();
    private long _generation;
    public int CachedCount => _cache.Count;
    public long CachedBytes { get; private set; }
    public void Invalidate() { Interlocked.Increment(ref _generation); _cache.Clear(); CachedBytes = 0; }
    public async Task<BitmapSource> GetAsync(DocumentRenderKey key,
        Func<CancellationToken, Task<BitmapSource>> render, CancellationToken cancellation)
    {
        long generation = _generation;
        await _gate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (generation != _generation) throw new OperationCanceledException();
            for (var node = _cache.First; node is not null; node = node.Next)
                if (node.Value.Key == key)
                {
                    var hit = node.Value.Bitmap;
                    _cache.Remove(node); _cache.AddFirst(node); return hit;
                }
            var bitmap = await render(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (generation != _generation) throw new OperationCanceledException();
            if (!bitmap.IsFrozen) bitmap.Freeze();
            long bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, bitmap.Format.BitsPerPixel / 8);
            if (bytes <= maxBytes && maxImages > 0)
            {
                _cache.AddFirst((key, bitmap, bytes)); CachedBytes += bytes;
                while (_cache.Count > maxImages || CachedBytes > maxBytes)
                { CachedBytes -= _cache.Last!.Value.Bytes; _cache.RemoveLast(); }
            }
            return bitmap;
        }
        finally { _gate.Release(); }
    }
}
