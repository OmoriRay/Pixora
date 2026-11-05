using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pixora.Services;

namespace Pixora.SmokeTests;

internal static class ThumbnailDiskCacheRegressionTests
{
    public static void Run(string root)
    {
        var folder = Path.Combine(root, "test-output", "thumbnail-cache-regressions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        AssertCorruptCacheHeals(folder);
        AssertClearReportsLockedFiles(folder);
        AssertConcurrentCacheAccess(folder);
        AssertLegacyCacheIsNotReused(folder);
        AssertTrimKeepsLeastRecentlyUsedOrder(folder);
    }

    private static void AssertCorruptCacheHeals(string root)
    {
        var folder = Path.Combine(root, "corrupt");
        var source = Path.Combine(root, "corrupt-source.png");
        File.WriteAllBytes(source, [1]);
        var cache = new ThumbnailDiskCache(folder);
        var thumbnail = CreateThumbnail();
        cache.Save(source, 64, 64, thumbnail);
        var cachePath = Directory.GetFiles(folder, "*.png").Single();
        byte[] corruptBytes = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0];
        File.WriteAllBytes(cachePath, corruptBytes);
        Assert(!cache.TryLoad(source, 64, 64, out var corrupt) && corrupt is null,
            "A truncated PNG cache entry should be a cache miss.");

        // 被占用时保留文件，写入失败不应逃逸，也不应遗留临时文件。
        using (var held = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            cache.Save(source, 64, 64, thumbnail);
        }

        Assert(File.ReadAllBytes(cachePath).SequenceEqual(corruptBytes), "A locked cache entry should remain intact.");
        Assert(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "A failed cache replacement should clean up its temporary file.");
        cache.Save(source, 64, 64, thumbnail);
        Assert(cache.TryLoad(source, 64, 64, out var repaired) && repaired is { PixelWidth: 2, PixelHeight: 2 },
            "Saving after a bad PNG cache miss should repair the existing entry after its lock is released.");

        // 有效的新文件不能被随后一次普通 Save 删除或覆盖。
        var validBytes = File.ReadAllBytes(cachePath);
        cache.Save(source, 64, 64, CreateThumbnail(width: 3));
        Assert(File.ReadAllBytes(cachePath).SequenceEqual(validBytes), "Save should preserve an already valid replacement cache entry.");
    }

    private static void AssertClearReportsLockedFiles(string root)
    {
        var folder = Path.Combine(root, "clear");
        Directory.CreateDirectory(folder);
        var lockedPath = Path.Combine(folder, "locked.png");
        var removablePath = Path.Combine(folder, "removable.png");
        File.WriteAllBytes(lockedPath, new byte[37]);
        File.WriteAllBytes(removablePath, new byte[19]);
        var cache = new ThumbnailDiskCache(folder);
        using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = cache.ClearAsync().GetAwaiter().GetResult();
            Assert(result.RemovedFileCount == 1 && result.RemovedBytes == 19 && result.RemainingBytes == 37,
                $"Clear should only count successful deletions and report locked bytes: got {result}.");
            Assert(File.Exists(lockedPath) && !File.Exists(removablePath), "Clear should retain the locked entry and remove the unlocked one.");
        }

        var retried = cache.Clear();
        Assert(retried.RemovedFileCount == 1 && retried.RemovedBytes == 37 && retried.RemainingBytes == 0,
            "Clear should remove and account for a formerly locked entry after its lock is released.");
    }

    private static void AssertConcurrentCacheAccess(string root)
    {
        var folder = Path.Combine(root, "concurrent");
        var source = Path.Combine(root, "concurrent-source.png");
        File.WriteAllBytes(source, [2]);
        var cache = new ThumbnailDiskCache(folder);
        var thumbnail = CreateThumbnail();
        Parallel.For(0, 24, iteration =>
        {
            cache.Save(source, 64, 64, thumbnail);
            cache.TryLoad(source, 64, 64, out _);
        });
        cache.Save(source, 64, 64, thumbnail);
        Assert(cache.TryLoad(source, 64, 64, out var loaded) && loaded is not null,
            "Concurrent cache writers and readers should leave a decodable entry.");
        Assert(Directory.EnumerateFiles(folder, "*.png").Count() == 1 && !Directory.EnumerateFiles(folder, "*.tmp").Any(),
            "Concurrent cache access should leave one cache entry without temporary files.");
    }

    private static void AssertTrimKeepsLeastRecentlyUsedOrder(string root)
    {
        var folder = Path.Combine(root, "trim");
        Directory.CreateDirectory(folder);
        var oldest = Path.Combine(folder, "oldest.png");
        var middle = Path.Combine(folder, "middle.png");
        var newest = Path.Combine(folder, "newest.png");
        File.WriteAllBytes(oldest, new byte[11]);
        File.WriteAllBytes(middle, new byte[13]);
        File.WriteAllBytes(newest, new byte[17]);
        var baseline = DateTime.UtcNow.AddDays(-3);
        File.SetLastAccessTimeUtc(oldest, baseline);
        File.SetLastAccessTimeUtc(middle, baseline.AddDays(1));
        File.SetLastAccessTimeUtc(newest, baseline.AddDays(2));
        var cache = new ThumbnailDiskCache(folder);
        var belowBudget = cache.TrimToBytes(41);
        Assert(belowBudget == new ThumbnailDiskCacheCleanupResult(0, 0, 41),
            "Trimming an already satisfied budget should preserve every entry.");
        var trimmed = cache.TrimToBytes(30);
        Assert(trimmed == new ThumbnailDiskCacheCleanupResult(1, 11, 30),
            "Trimming over budget should account for the oldest entry only.");
        Assert(!File.Exists(oldest) && File.Exists(middle) && File.Exists(newest),
            "Over-budget trimming should retain the more recently accessed entries.");
    }

    private static void AssertLegacyCacheIsNotReused(string root)
    {
        var folder = Path.Combine(root, "legacy");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(root, "legacy-source.png");
        File.WriteAllBytes(source, [3]);
        var info = new FileInfo(source);
        var oldKey = string.Join("|", Path.GetFullPath(source), info.Length.ToString(CultureInfo.InvariantCulture),
            info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), "64", "64");
        var oldHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldKey))).ToLowerInvariant();
        var oldCachePath = Path.Combine(folder, $"{oldHash}.png");
        using (var stream = File.Create(oldCachePath))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(CreateThumbnail(width: 3)));
            encoder.Save(stream);
        }

        var cache = new ThumbnailDiskCache(folder);
        Assert(!cache.TryLoad(source, 64, 64, out _), "A legacy rendering cache key should not be reused.");
        cache.Save(source, 64, 64, CreateThumbnail());
        Assert(cache.TryLoad(source, 64, 64, out var current) && current?.PixelWidth == 2,
            "The current rendering version should write and load its own entry.");
        Assert(File.Exists(oldCachePath) && cache.GetStatistics().FileCount == 2,
            "Legacy cache entries should remain available for normal capacity cleanup.");
    }

    private static BitmapSource CreateThumbnail(int width = 2)
    {
        var bitmap = BitmapSource.Create(width, 2, 96, 96, PixelFormats.Bgra32, null, new byte[width * 2 * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
