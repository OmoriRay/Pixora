using System.Windows.Media.Imaging;

namespace Pixora.Models;

public sealed class ImageDocument
{
    public ImageDocument(
        string path,
        BitmapSource bitmap,
        string formatName,
        long fileSize,
        DateTime lastWriteTime,
        IReadOnlyList<ImageAnimationFrame>? animationFrames = null,
        bool isVideo = false,
        int? pixelWidth = null,
        int? pixelHeight = null,
        bool isPreview = false,
        bool isLargeImagePreview = false)
    {
        Path = path;
        Bitmap = bitmap;
        FormatName = formatName;
        FileSize = fileSize;
        LastWriteTime = lastWriteTime;
        AnimationFrames = animationFrames ?? [];
        IsVideo = isVideo;
        PixelWidth = pixelWidth.GetValueOrDefault(bitmap.PixelWidth);
        PixelHeight = pixelHeight.GetValueOrDefault(bitmap.PixelHeight);
        IsLargeImagePreview = isLargeImagePreview;
        IsPreview = isPreview || isLargeImagePreview;
    }

    public string Path { get; }

    public BitmapSource Bitmap { get; }

    public string FormatName { get; }

    public long FileSize { get; }

    public DateTime LastWriteTime { get; }

    public IReadOnlyList<ImageAnimationFrame> AnimationFrames { get; }

    public bool IsVideo { get; }

    public bool IsPreview { get; }

    public bool IsLargeImagePreview { get; }

    public bool IsAnimated => AnimationFrames.Count > 1;

    public string FileName => System.IO.Path.GetFileName(Path);

    public int PixelWidth { get; }

    public int PixelHeight { get; }
}
