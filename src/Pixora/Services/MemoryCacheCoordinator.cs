namespace Pixora.Services;

public sealed record MemoryCacheSnapshot(
    long MainImageBudgetBytes,
    long MainImageEstimatedBytes,
    long PreviewBudgetBytes,
    long PreviewEstimatedBytes,
    bool IsUnderPressure);

public sealed class MemoryCacheCoordinator
{
    private const double PressureThreshold = 0.90;
    private const long MinimumMainImageBudgetBytes = 64L * 1024 * 1024;
    private const long MinimumPreviewBudgetBytes = 32L * 1024 * 1024;

    private readonly ImageCache _imageCache;
    private readonly BitmapSourceMemoryCache _previewCache;

    public MemoryCacheCoordinator(ImageCache imageCache, BitmapSourceMemoryCache previewCache)
    {
        _imageCache = imageCache;
        _previewCache = previewCache;
    }

    public void ApplyConfiguredBudgets(int mainImageMegabytes, int previewMegabytes)
    {
        _imageCache.SetMaxMegabytes(mainImageMegabytes);
        _previewCache.SetMaxMegabytes(previewMegabytes);
    }

    public bool ShouldReduceBackgroundLoading(bool isProtectionEnabled)
    {
        if (!isProtectionEnabled)
        {
            return false;
        }

        var memoryInfo = GC.GetGCMemoryInfo();
        return IsUnderPressure(memoryInfo.MemoryLoadBytes, memoryInfo.HighMemoryLoadThresholdBytes);
    }

    public void TrimForMemoryPressure()
    {
        _imageCache.TrimToBytes(Math.Max(MinimumMainImageBudgetBytes, _imageCache.MaxBytes / 2));
        _previewCache.TrimToBytes(Math.Max(MinimumPreviewBudgetBytes, _previewCache.MaxBytes / 2));
    }

    public MemoryCacheSnapshot CaptureSnapshot(bool isProtectionEnabled)
    {
        return new MemoryCacheSnapshot(
            _imageCache.MaxBytes,
            _imageCache.TotalEstimatedBytes,
            _previewCache.MaxBytes,
            _previewCache.TotalEstimatedBytes,
            ShouldReduceBackgroundLoading(isProtectionEnabled));
    }

    public static bool IsUnderPressure(long memoryLoadBytes, long highMemoryLoadThresholdBytes)
    {
        return highMemoryLoadThresholdBytes > 0
            && memoryLoadBytes >= (long)(highMemoryLoadThresholdBytes * PressureThreshold);
    }
}
