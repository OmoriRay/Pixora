using Pixora.Models;

namespace Pixora.Services;

public sealed class ImageCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _usage = new();
    private long _maxBytes;
    private long _totalEstimatedBytes;

    public ImageCache(long maxBytes)
    {
        _maxBytes = Math.Max(1, maxBytes);
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

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public static ImageCache FromMegabytes(int maxMegabytes)
    {
        return new ImageCache((long)Math.Max(1, maxMegabytes) * 1024 * 1024);
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

    public bool TryGet(string path, out ImageDocument? document)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(path, out var entry))
            {
                Touch(entry.UsageNode);
                document = entry.Document;
                return true;
            }
        }

        document = null;
        return false;
    }

    public void Add(ImageDocument document)
    {
        if (document.IsAnimated)
        {
            return;
        }

        lock (_gate)
        {
            var estimatedBytes = EstimateDecodedBytes(document);
            if (_items.TryGetValue(document.Path, out var existing))
            {
                _totalEstimatedBytes -= existing.EstimatedBytes;
            }

            var usageNode = existing?.UsageNode ?? _usage.AddFirst(document.Path);
            _items[document.Path] = new CacheEntry(document, estimatedBytes, usageNode);
            _totalEstimatedBytes += estimatedBytes;
            Touch(usageNode);
            TrimToBudget();
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            if (_items.Remove(path, out var entry))
            {
                _totalEstimatedBytes -= entry.EstimatedBytes;
                _usage.Remove(entry.UsageNode);
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

    private static long EstimateDecodedBytes(ImageDocument document)
    {
        try
        {
            var bitsPerPixel = Math.Max(32, document.Bitmap.Format.BitsPerPixel);
            var bytesPerPixel = Math.Max(4, (bitsPerPixel + 7) / 8);
            return checked((long)document.PixelWidth * document.PixelHeight * bytesPerPixel);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private void Touch(LinkedListNode<string> node)
    {
        _usage.Remove(node);
        _usage.AddFirst(node);
    }

    private sealed record CacheEntry(ImageDocument Document, long EstimatedBytes, LinkedListNode<string> UsageNode);
}
