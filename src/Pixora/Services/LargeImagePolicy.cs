using System.IO;

namespace Pixora.Services;

public static class LargeImagePolicy
{
    public const long FullResolutionPixelLimit = 120_000_000;

    public const long SafePreviewSourcePixelLimit = 2_000_000_000;

    public const int DefaultPreviewMaximumSide = 4096;

    public const int HighPerformancePreviewMaximumSide = 8192;

    public const int HighPerformancePreviewBudgetMegabytes = 1024;

    public static long GetPixelCount(int width, int height)
    {
        return width <= 0 || height <= 0 ? 0 : (long)width * height;
    }

    public static bool RequiresSafetyPreview(int width, int height)
    {
        return GetPixelCount(width, height) > FullResolutionPixelLimit;
    }

    public static void ValidateSafetyPreviewSource(int width, int height)
    {
        ValidateSourceDimensions(width, height);
        var pixelCount = GetPixelCount(width, height);
        if (pixelCount > SafePreviewSourcePixelLimit)
        {
            throw new InvalidDataException(
                $"图片包含 {pixelCount:N0} 像素，已超过安全预览上限 {SafePreviewSourcePixelLimit:N0} 像素。");
        }
    }

    public static void ValidateFullResolutionSource(int width, int height)
    {
        ValidateSourceDimensions(width, height);
        var pixelCount = GetPixelCount(width, height);
        if (pixelCount > FullResolutionPixelLimit)
        {
            throw new InvalidDataException(
                $"图片包含 {pixelCount:N0} 像素，已超过完整解码上限 {FullResolutionPixelLimit:N0} 像素。若格式支持，Pixora 会自动使用超大图安全预览。");
        }
    }

    public static void ValidateSourceDimensions(int width, int height)
    {
        if (GetPixelCount(width, height) <= 0)
        {
            throw new InvalidDataException("图片尺寸无效。");
        }
    }

    public static (int Width, int Height) CalculatePreviewDecodeSize(
        int sourceWidth,
        int sourceHeight,
        int requestedMaxWidth,
        int requestedMaxHeight,
        int largeImageMaximumSide = 0)
    {
        ValidateSafetyPreviewSource(sourceWidth, sourceHeight);

        var maxWidth = Math.Max(1, requestedMaxWidth);
        var maxHeight = Math.Max(1, requestedMaxHeight);
        if (RequiresSafetyPreview(sourceWidth, sourceHeight))
        {
            if (largeImageMaximumSide > 0)
            {
                var safeMaximumSide = Math.Clamp(
                    largeImageMaximumSide,
                    DefaultPreviewMaximumSide,
                    HighPerformancePreviewMaximumSide);
                maxWidth = safeMaximumSide;
                maxHeight = safeMaximumSide;
            }
            else
            {
                maxWidth = Math.Min(maxWidth, DefaultPreviewMaximumSide);
                maxHeight = Math.Min(maxHeight, DefaultPreviewMaximumSide);
            }
        }

        var scale = Math.Min(
            1.0,
            Math.Min(
                maxWidth / Math.Max(1.0, sourceWidth),
                maxHeight / Math.Max(1.0, sourceHeight)));
        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    public static int ResolvePreviewMaximumSide(int previewCacheMegabytes)
    {
        return previewCacheMegabytes >= HighPerformancePreviewBudgetMegabytes
            ? HighPerformancePreviewMaximumSide
            : DefaultPreviewMaximumSide;
    }
}
