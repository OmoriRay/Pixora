using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Pixora.Services;

public sealed record ThumbnailDiskCacheStatistics(int FileCount, long TotalBytes);

public sealed record ThumbnailDiskCacheCleanupResult(int RemovedFileCount, long RemovedBytes, long RemainingBytes);

public sealed class ThumbnailDiskCache
{
    // 包含缩略图渲染语义；变更时让旧版本缓存通过正常淘汰流程退出。
    private const string CacheFormatVersion = "v2";
    private const int WritesBeforeAutomaticCleanup = 32;
    private long _maxBytes;
    private int _writesSinceCleanup;

    public static string DefaultCacheFolder =>
        Path.Combine(
            AppInfo.LocalDataFolder,
            "thumbnail-cache");

    public ThumbnailDiskCache(string? folder = null, long? maxBytes = null)
    {
        Folder = string.IsNullOrWhiteSpace(folder) ? DefaultCacheFolder : folder;
        _maxBytes = Math.Max(1, maxBytes ?? ToBytes(ViewerSettings.DefaultThumbnailDiskCacheMegabytes));
    }

    public string Folder { get; }

    public long MaxBytes => Interlocked.Read(ref _maxBytes);

    public void SetMaxMegabytes(int megabytes)
    {
        SetMaxBytes(ToBytes(megabytes));
    }

    public void SetMaxBytes(long maxBytes)
    {
        Interlocked.Exchange(ref _maxBytes, Math.Max(1, maxBytes));
    }

    public bool TryLoad(string path, int maxWidth, int maxHeight, out BitmapSource? thumbnail)
    {
        thumbnail = null;
        try
        {
            var cachePath = GetCachePath(path, maxWidth, maxHeight);
            if (!File.Exists(cachePath))
            {
                return false;
            }

            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();

            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            thumbnail = bitmap;
            TryUpdateLastAccessTime(cachePath);
            return true;
        }
        catch
        {
            thumbnail = null;
            return false;
        }
    }

    public void Save(string path, int maxWidth, int maxHeight, BitmapSource thumbnail)
    {
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(Folder);
            var cachePath = GetCachePath(path, maxWidth, maxHeight);
            // 文件存在不代表内容有效。坏缓存应允许后续有效缩略图原子替换；
            // 不在读取失败时按路径删除，避免误删并发写入者刚替换的缓存。
            if (TryLoad(path, maxWidth, maxHeight, out _))
            {
                return;
            }

            tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(thumbnail));
                encoder.Save(stream);
            }

            File.Move(tempPath, cachePath, overwrite: true);
            tempPath = null;
            if (Interlocked.Increment(ref _writesSinceCleanup) >= WritesBeforeAutomaticCleanup)
            {
                Interlocked.Exchange(ref _writesSinceCleanup, 0);
                TrimToBytes(MaxBytes);
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }

    public Task<ThumbnailDiskCacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetStatistics(cancellationToken), cancellationToken);
    }

    public ThumbnailDiskCacheStatistics GetStatistics(CancellationToken cancellationToken = default)
    {
        var files = GetCacheFiles(cancellationToken);
        long totalBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes += GetFileLength(file);
        }

        return new ThumbnailDiskCacheStatistics(files.Count, totalBytes);
    }

    public Task<ThumbnailDiskCacheCleanupResult> TrimToBytesAsync(long maxBytes, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => TrimToBytes(maxBytes, cancellationToken), cancellationToken);
    }

    public ThumbnailDiskCacheCleanupResult TrimToBytes(long maxBytes, CancellationToken cancellationToken = default)
    {
        var targetBytes = Math.Max(1, maxBytes);
        var files = GetCacheFiles(cancellationToken);
        var totalBytes = files.Sum(GetFileLength);
        if (totalBytes <= targetBytes)
        {
            return new ThumbnailDiskCacheCleanupResult(0, 0, totalBytes);
        }

        files = files
            .OrderBy(static file => GetLastAccessTimeUtc(file))
            .ThenBy(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var removedFileCount = 0;
        long removedBytes = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalBytes <= targetBytes)
            {
                break;
            }

            try
            {
                var length = GetFileLength(file);
                file.Delete();
                totalBytes -= length;
                removedBytes += length;
                removedFileCount++;
            }
            catch
            {
            }
        }

        return new ThumbnailDiskCacheCleanupResult(removedFileCount, removedBytes, Math.Max(0, totalBytes));
    }

    public Task<ThumbnailDiskCacheCleanupResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Clear(cancellationToken), cancellationToken);
    }

    public ThumbnailDiskCacheCleanupResult Clear(CancellationToken cancellationToken = default)
    {
        var files = GetCacheFiles(cancellationToken);
        var removedFileCount = 0;
        long removedBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var length = GetFileLength(file);
                file.Delete();
                removedBytes += length;
                removedFileCount++;
            }
            catch
            {
            }
        }

        // 删除可能因占用而失败，期间也可能有新写入；返回当前实际剩余量。
        var remainingBytes = GetStatistics(cancellationToken).TotalBytes;
        return new ThumbnailDiskCacheCleanupResult(removedFileCount, removedBytes, remainingBytes);
    }

    private string GetCachePath(string path, int maxWidth, int maxHeight)
    {
        var fileInfo = new FileInfo(path);
        var key = string.Join(
            "|",
            CacheFormatVersion,
            Path.GetFullPath(path),
            fileInfo.Length.ToString(CultureInfo.InvariantCulture),
            fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            maxWidth.ToString(CultureInfo.InvariantCulture),
            maxHeight.ToString(CultureInfo.InvariantCulture));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(Folder, $"{hash}.png");
    }

    private List<FileInfo> GetCacheFiles(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return [];
            }

            var result = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(Folder, "*.png", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(path);
                if (file.Exists)
                {
                    result.Add(file);
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static DateTime GetLastAccessTimeUtc(FileInfo file)
    {
        try
        {
            var lastAccess = file.LastAccessTimeUtc;
            return lastAccess > DateTime.UnixEpoch ? lastAccess : file.LastWriteTimeUtc;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static void TryUpdateLastAccessTime(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
        }
    }

    private static long ToBytes(int megabytes)
    {
        return (long)Math.Max(1, megabytes) * 1024 * 1024;
    }

    private static long GetFileLength(FileInfo file)
    {
        try
        {
            return file.Exists ? file.Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
