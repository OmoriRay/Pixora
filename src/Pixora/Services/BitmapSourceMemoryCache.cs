using System.IO;
using System.Windows.Media.Imaging;

namespace Pixora.Services;

public sealed class BitmapSourceMemoryCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _usage = new();
    private long _maxBytes;
    private long _totalEstimatedBytes;

    public BitmapSourceMemoryCache(long maxBytes)
    {
        _maxBytes = Math.Max(1, maxBytes);
    }

    public static BitmapSourceMemoryCache FromMegabytes(int maxMegabytes)
    {
        return new BitmapSourceMemoryCache((long)Math.Max(1, maxMegabytes) * 1024 * 1024);
    }

    public long MaxBytes
    {
        get
        {
            lock (_gate)
            {
                return _maxBytes;
            }
        }
    }

    public long TotalEstimatedBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalEstimatedBytes;
            }
        }
    }

    public void SetMaxMegabytes(int maxMegabytes)
    {
        SetMaxBytes((long)Math.Max(1, maxMegabytes) * 1024 * 1024);
    }

    public void SetMaxBytes(long maxBytes)
    {
        lock (_gate)
        {
            _maxBytes = Math.Max(1, maxBytes);
            TrimToBudget();
        }
    }

    public void TrimToBytes(long maxBytes)
    {
        lock (_gate)
        {
            TrimToBudget(Math.Max(1, maxBytes));
        }
    }

    public bool TryGet(string key, string path, out BitmapSource? bitmap)
    {
        var stamp = GetFileStamp(path);
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var entry)
                && entry.Length == stamp.Length
                && entry.LastWriteTimeUtcTicks == stamp.LastWriteTimeUtcTicks)
            {
                Touch(entry.UsageNode);
                bitmap = entry.Bitmap;
                return true;
            }

            if (entry is not null)
            {
                RemoveCore(key, entry);
            }
        }

        bitmap = null;
        return false;
    }

    public void Add(string key, string path, BitmapSource bitmap)
    {
        var stamp = GetFileStamp(path);
        var estimatedBytes = EstimateDecodedBytes(bitmap);

        lock (_gate)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                RemoveCore(key, existing);
            }

            var usageNode = _usage.AddFirst(key);
            _items[key] = new CacheEntry(bitmap, estimatedBytes, stamp.Length, stamp.LastWriteTimeUtcTicks, usageNode);
            _totalEstimatedBytes += estimatedBytes;
            TrimToBudget();
        }
    }

    public void RemoveByPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            var keys = _items.Keys
                .Where(key => key.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keys)
            {
                if (_items.TryGetValue(key, out var entry))
                {
                    RemoveCore(key, entry);
                }
            }
        }
    }

    private void TrimToBudget()
    {
        TrimToBudget(_maxBytes);
    }

    private void TrimToBudget(long maxBytes)
    {
        while (_items.Count > 1 && _totalEstimatedBytes > maxBytes && _usage.Last is not null)
        {
            var oldest = _usage.Last.Value;
            _usage.RemoveLast();
            if (_items.Remove(oldest, out var entry))
            {
                _totalEstimatedBytes -= entry.EstimatedBytes;
            }
        }
    }

    private void RemoveCore(string key, CacheEntry entry)
    {
        _items.Remove(key);
        _totalEstimatedBytes -= entry.EstimatedBytes;
        _usage.Remove(entry.UsageNode);
    }

    private void Touch(LinkedListNode<string> node)
    {
        _usage.Remove(node);
        _usage.AddFirst(node);
    }

    private static FileStamp GetFileStamp(string path)
    {
        var fileInfo = new FileInfo(path);
        return new FileStamp(fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
    }

    private static long EstimateDecodedBytes(BitmapSource bitmap)
    {
        try
        {
            var bitsPerPixel = Math.Max(32, bitmap.Format.BitsPerPixel);
            var bytesPerPixel = Math.Max(4, (bitsPerPixel + 7) / 8);
            return checked((long)bitmap.PixelWidth * bitmap.PixelHeight * bytesPerPixel);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks);

    private sealed record CacheEntry(
        BitmapSource Bitmap,
        long EstimatedBytes,
        long Length,
        long LastWriteTimeUtcTicks,
        LinkedListNode<string> UsageNode);
}
