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
            if (File.Exists(cachePath))
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

    public ThumbnailDiskCacheStatistics GetStatistics()
    {
        var files = GetCacheFiles();
        return new ThumbnailDiskCacheStatistics(files.Count, files.Sum(GetFileLength));
    }

    public ThumbnailDiskCacheCleanupResult TrimToBytes(long maxBytes)
    {
        var targetBytes = Math.Max(1, maxBytes);
        var files = GetCacheFiles()
            .OrderBy(static file => GetLastAccessTimeUtc(file))
            .ThenBy(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var totalBytes = files.Sum(GetFileLength);
        var removedFileCount = 0;
        long removedBytes = 0;

        foreach (var file in files)
        {
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

    public ThumbnailDiskCacheCleanupResult Clear()
    {
        var files = GetCacheFiles();
        var removedFileCount = 0;
        long removedBytes = 0;
        foreach (var file in files)
        {
            try
            {
                removedBytes += GetFileLength(file);
                file.Delete();
                removedFileCount++;
            }
            catch
            {
            }
        }

        return new ThumbnailDiskCacheCleanupResult(removedFileCount, removedBytes, 0);
    }

    private string GetCachePath(string path, int maxWidth, int maxHeight)
    {
        var fileInfo = new FileInfo(path);
        var key = string.Join(
            "|",
            Path.GetFullPath(path),
            fileInfo.Length.ToString(CultureInfo.InvariantCulture),
            fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            maxWidth.ToString(CultureInfo.InvariantCulture),
            maxHeight.ToString(CultureInfo.InvariantCulture));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(Folder, $"{hash}.png");
    }

    private List<FileInfo> GetCacheFiles()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return [];
            }

            return Directory.EnumerateFiles(Folder, "*.png", SearchOption.TopDirectoryOnly)
                .Select(static path => new FileInfo(path))
                .Where(static file => file.Exists)
                .ToList();
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
