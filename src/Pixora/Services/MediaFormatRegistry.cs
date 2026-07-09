using System.IO;

namespace Pixora.Services;

public static class MediaFormatRegistry
{
    private static readonly string[] StillImageExtensions =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".bmp", ".gif",
        ".webp", ".tif", ".tiff", ".ico", ".cur", ".heic", ".heif", ".avif",
        ".avifs", ".jxr", ".wdp", ".hdp", ".hdr",
    ];

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".mpeg",
        ".mpg", ".3gp", ".3g2", ".ts", ".m2ts", ".mts",
    ];

    private static readonly HashSet<string> StillImageExtensionSet =
        new(StillImageExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VideoExtensionSet =
        new(VideoExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> LikelyAnimatedImageExtensionSet =
        new([".gif", ".apng"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> SupportedStillImageExtensions => StillImageExtensions;

    public static IReadOnlyList<string> SupportedVideoExtensions => VideoExtensions;

    public static bool IsSupportedStillImagePath(string path)
    {
        return StillImageExtensionSet.Contains(Path.GetExtension(path));
    }

    public static bool IsSupportedVideoPath(string path)
    {
        return VideoExtensionSet.Contains(Path.GetExtension(path));
    }

    public static bool IsLikelyAnimatedImagePath(string path)
    {
        return LikelyAnimatedImageExtensionSet.Contains(Path.GetExtension(path));
    }

    public static bool IsSupportedMediaPath(string path)
    {
        return IsSupportedStillImagePath(path) || IsSupportedVideoPath(path);
    }
}
