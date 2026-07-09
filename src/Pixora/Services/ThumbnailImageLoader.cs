using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pixora.Services;

public sealed class ThumbnailImageLoader
{
    private readonly ThumbnailDiskCache _diskCache;

    public ThumbnailImageLoader(ThumbnailDiskCache diskCache)
    {
        _diskCache = diskCache;
    }

    public BitmapSource Load(
        string path,
        int maxWidth,
        int maxHeight,
        bool useDiskCache,
        CancellationToken cancellationToken)
    {
        if (useDiskCache
            && _diskCache.TryLoad(path, maxWidth, maxHeight, out var cached)
            && cached is not null)
        {
            return cached;
        }

        var thumbnail = Create(path, maxWidth, maxHeight, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (useDiskCache)
        {
            _diskCache.Save(path, maxWidth, maxHeight, thumbnail);
        }

        return thumbnail;
    }

    private static BitmapSource Create(string path, int maxWidth, int maxHeight, CancellationToken cancellationToken)
    {
        if (ImageCatalog.IsSupportedVideoPath(path))
        {
            return VideoThumbnailLoader.LoadThumbnail(path, Math.Max(maxWidth, maxHeight), cancellationToken);
        }

        if (TryLoadWicThumbnail(path, maxWidth, maxHeight, cancellationToken, out var wicThumbnail))
        {
            return wicThumbnail;
        }

        var document = ImageLoader.Load(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ScaleBitmapToBounds(document.Bitmap, maxWidth, maxHeight);
    }

    private static bool TryLoadWicThumbnail(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken,
        out BitmapSource thumbnail)
    {
        thumbnail = null!;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            int pixelWidth;
            int pixelHeight;
            using (var probeStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(
                    probeStream,
                    BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);

                if (decoder.Frames.Count == 0)
                {
                    return false;
                }

                pixelWidth = decoder.Frames[0].PixelWidth;
                pixelHeight = decoder.Frames[0].PixelHeight;
            }

            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var widthRatio = maxWidth / Math.Max(1.0, pixelWidth);
            var heightRatio = maxHeight / Math.Max(1.0, pixelHeight);
            var scale = Math.Min(1.0, Math.Min(widthRatio, heightRatio));
            var decodeWidth = Math.Max(1, (int)Math.Round(pixelWidth * scale));
            var decodeHeight = Math.Max(1, (int)Math.Round(pixelHeight * scale));

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (widthRatio <= heightRatio)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }
            else
            {
                bitmap.DecodePixelHeight = decodeHeight;
            }

            bitmap.StreamSource = stream;
            bitmap.EndInit();

            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            thumbnail = bitmap;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException
            or FileFormatException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }

    private static BitmapSource ScaleBitmapToBounds(BitmapSource source, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(
            maxWidth / Math.Max(1.0, source.PixelWidth),
            maxHeight / Math.Max(1.0, source.PixelHeight));
        scale = Math.Min(1.0, scale);
        if (scale >= 0.999)
        {
            return source;
        }

        var thumbnail = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        if (thumbnail.CanFreeze)
        {
            thumbnail.Freeze();
        }

        return thumbnail;
    }
}
