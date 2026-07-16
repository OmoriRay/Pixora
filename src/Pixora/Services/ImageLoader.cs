using ImageMagick;
using Pixora.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pixora.Services;

public static class ImageLoader
{
    private const long MaxHdrPixelCount = 40_000_000;
    private const int MaxAnimationFrameCount = 2_000;
    private const long MaxAnimationPixelFrames = 300_000_000;
    private static readonly TimeSpan DefaultAnimationDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinimumAnimationDelay = TimeSpan.FromMilliseconds(20);

    public static ImageDocument Load(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Image file does not exist.", path);
        }

        if (IsRadianceHdrPath(path))
        {
            return LoadRadianceHdr(path, cancellationToken);
        }

        try
        {
            return LoadWithWic(path, cancellationToken);
        }
        catch (Exception ex) when (CanFallbackToMagick(ex))
        {
            return LoadWithMagick(path, cancellationToken, ex);
        }
    }

    public static BitmapSource LoadPreview(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken,
        int largeImageMaximumSide = 0)
    {
        return LoadPreviewDocument(path, maxWidth, maxHeight, cancellationToken, largeImageMaximumSide).Bitmap;
    }

    public static ImageDocument LoadPreviewDocument(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken,
        int largeImageMaximumSide = 0)
    {
        try
        {
            return LoadPreviewDocumentWithWic(
                path,
                maxWidth,
                maxHeight,
                cancellationToken,
                largeImageMaximumSide);
        }
        catch (Exception ex) when (CanFallbackToMagick(ex))
        {
            return LoadPreviewDocumentWithMagick(
                path,
                maxWidth,
                maxHeight,
                cancellationToken,
                ex,
                largeImageMaximumSide);
        }
    }

    public static ImageDocument CreatePreviewDocument(string path, BitmapSource preview, CancellationToken cancellationToken)
    {
        try
        {
            return CreatePreviewDocumentWithWicMetadata(path, preview, cancellationToken);
        }
        catch (Exception ex) when (CanFallbackToMagick(ex))
        {
            return CreatePreviewDocumentWithMagickMetadata(path, preview, cancellationToken, ex);
        }
    }

    public static bool RequiresSafetyPreview(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Image file does not exist.", path);
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("Image has no displayable frames.");
            }

            var frame = decoder.Frames[0];
            LargeImagePolicy.ValidateSafetyPreviewSource(frame.PixelWidth, frame.PixelHeight);
            return LargeImagePolicy.RequiresSafetyPreview(frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (CanFallbackToMagick(ex))
        {
            var imageInfo = new MagickImageInfo(path);
            var (width, height) = GetMagickOrientedDimensions(imageInfo);
            LargeImagePolicy.ValidateSourceDimensions(width, height);
            if (IsTiffContainer(path, imageInfo.Format)
                && LargeImagePolicy.RequiresSafetyPreview(width, height))
            {
                return true;
            }

            LargeImagePolicy.ValidateSafetyPreviewSource(width, height);
            return LargeImagePolicy.RequiresSafetyPreview(width, height);
        }
    }

    private static ImageDocument LoadPreviewDocumentWithWic(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken,
        int largeImageMaximumSide)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Image file does not exist.", path);
        }

        var fileInfo = new FileInfo(path);
        maxWidth = Math.Max(1, maxWidth);
        maxHeight = Math.Max(1, maxHeight);

        using var metadataStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var decoder = BitmapDecoder.Create(
            metadataStream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);

        cancellationToken.ThrowIfCancellationRequested();
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("Image has no displayable frames.");
        }

        var frame = decoder.Frames[0];
        LargeImagePolicy.ValidateSafetyPreviewSource(frame.PixelWidth, frame.PixelHeight);
        var isLargeImagePreview = LargeImagePolicy.RequiresSafetyPreview(frame.PixelWidth, frame.PixelHeight);
        if (isLargeImagePreview && !SupportsNativePreviewScaling(decoder))
        {
            throw new InvalidDataException(
                "该格式的 WIC 解码器不能在解码阶段缩小超大图片；目前仅支持 JPEG 和 PNG 超大图安全预览。");
        }

        var orientation = ReadExifOrientation(frame);
        var (decodeWidth, decodeHeight) = LargeImagePolicy.CalculatePreviewDecodeSize(
            frame.PixelWidth,
            frame.PixelHeight,
            maxWidth,
            maxHeight,
            largeImageMaximumSide);
        var widthRatio = decodeWidth / Math.Max(1.0, frame.PixelWidth);
        var heightRatio = decodeHeight / Math.Max(1.0, frame.PixelHeight);

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        BitmapImage bitmap;
        try
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
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
        }
        catch (Exception ex) when (isLargeImagePreview && CanFallbackToMagick(ex))
        {
            throw new InvalidDataException(
                "该格式的超大图片无法在不完整解码的情况下生成安全预览。",
                ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var preview = ApplyExifOrientation(bitmap, orientation);
        if (preview.CanFreeze)
        {
            preview.Freeze();
        }

        var (orientedWidth, orientedHeight) = GetOrientedDimensions(frame.PixelWidth, frame.PixelHeight, orientation);
        return new ImageDocument(
            path,
            preview,
            GetFormatName(path, decoder),
            fileInfo.Length,
            fileInfo.LastWriteTime,
            [],
            pixelWidth: orientedWidth,
            pixelHeight: orientedHeight,
            isPreview: true,
            isLargeImagePreview: isLargeImagePreview);
    }

    private static ImageDocument LoadPreviewDocumentWithMagick(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken,
        Exception wicException,
        int largeImageMaximumSide)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            maxWidth = Math.Max(1, maxWidth);
            maxHeight = Math.Max(1, maxHeight);

            var fileInfo = new FileInfo(path);
            var imageInfo = new MagickImageInfo(path);
            var sourceFormat = imageInfo.Format;
            var (metadataWidth, metadataHeight) = GetMagickOrientedDimensions(imageInfo);
            LargeImagePolicy.ValidateSourceDimensions(metadataWidth, metadataHeight);
            if (IsTiffContainer(path, sourceFormat)
                && LargeImagePolicy.RequiresSafetyPreview(metadataWidth, metadataHeight))
            {
                return LoadTiffOverviewPreview(
                    path,
                    maxWidth,
                    maxHeight,
                    cancellationToken,
                    largeImageMaximumSide,
                    fileInfo,
                    metadataWidth,
                    metadataHeight);
            }

            LargeImagePolicy.ValidateSafetyPreviewSource(metadataWidth, metadataHeight);
            if (LargeImagePolicy.RequiresSafetyPreview(metadataWidth, metadataHeight))
            {
                throw new InvalidDataException(
                    "该格式需要 Magick.NET 完整解码，暂时无法安全生成超大图预览。");
            }

            using var image = new MagickImage(path);
            cancellationToken.ThrowIfCancellationRequested();

            image.AutoOrient();
            var width = checked((int)image.Width);
            var height = checked((int)image.Height);
            LargeImagePolicy.ValidateFullResolutionSource(width, height);
            var (previewWidth, previewHeight) = LargeImagePolicy.CalculatePreviewDecodeSize(
                width,
                height,
                maxWidth,
                maxHeight,
                largeImageMaximumSide);
            if (previewWidth < width || previewHeight < height)
            {
                image.Resize((uint)previewWidth, (uint)previewHeight);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var pngBytes = image.ToByteArray(MagickFormat.Png32);
            using var stream = new MemoryStream(pngBytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("Image has no displayable frames.");
            }

            var bitmap = decoder.Frames[0];
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return new ImageDocument(
                path,
                bitmap,
                $"{GetMagickFormatName(path, sourceFormat)} / Magick.NET",
                fileInfo.Length,
                fileInfo.LastWriteTime,
                [],
                pixelWidth: width,
                pixelHeight: height,
                isPreview: true);
        }
        catch (Exception ex) when (ex is MagickException or NotSupportedException or InvalidOperationException or IOException)
        {
            throw new InvalidDataException(
                "WIC preview failed; Magick.NET preview also failed.\nWIC error: "
                + wicException.Message
                + "\nMagick error: "
                + ex.Message,
                ex);
        }
    }

    private static ImageDocument LoadTiffOverviewPreview(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken,
        int largeImageMaximumSide,
        FileInfo fileInfo,
        int sourceWidth,
        int sourceHeight)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targetMaximumSide = largeImageMaximumSide > 0
            ? Math.Clamp(
                largeImageMaximumSide,
                LargeImagePolicy.DefaultPreviewMaximumSide,
                LargeImagePolicy.HighPerformancePreviewMaximumSide)
            : Math.Max(maxWidth, maxHeight);
        targetMaximumSide = Math.Max(1, targetMaximumSide);

        var candidates = new List<TiffOverviewCandidate>();
        var frameIndex = 0;
        var tiffSettings = new MagickReadSettings
        {
            Format = MagickFormat.Tiff,
        };
        foreach (var frameInfo in MagickImageInfo.ReadCollection(fileInfo, tiffSettings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (frameWidth, frameHeight) = GetMagickOrientedDimensions(frameInfo);
            candidates.Add(new TiffOverviewCandidate(frameIndex, frameWidth, frameHeight));
            frameIndex++;
        }

        var selected = TiffOverviewSelector.SelectBest(
            sourceWidth,
            sourceHeight,
            candidates,
            targetMaximumSide);
        if (!selected.HasValue)
        {
            throw new InvalidDataException(
                "该 BigTIFF 超过完整解码上限，并且没有可安全读取的内置缩略图或金字塔层。");
        }

        var selectedFrame = selected.Value;
        var readSettings = new MagickReadSettings
        {
            FrameIndex = checked((uint)selectedFrame.FrameIndex),
            FrameCount = 1,
        };

        var decodePixelLimit = TiffOverviewSelector.GetDecodePixelLimit(targetMaximumSide);
        if (selectedFrame.PixelCount > decodePixelLimit)
        {
            throw new InvalidDataException("BigTIFF 金字塔层尺寸超过当前性能档允许的安全解码预算。");
        }

        using var image = new MagickImage();
        image.Progress += (_, args) => args.Cancel = cancellationToken.IsCancellationRequested;
        try
        {
            using var imageStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            image.Read(imageStream, readSettings);
        }
        catch (MagickException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        image.AutoOrient();
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);
        if (LargeImagePolicy.GetPixelCount(width, height) > decodePixelLimit)
        {
            throw new InvalidDataException("BigTIFF 实际解码层超过当前性能档允许的安全解码预算。");
        }

        var (previewWidth, previewHeight) = LargeImagePolicy.CalculatePreviewDecodeSize(
            width,
            height,
            targetMaximumSide,
            targetMaximumSide);
        if (previewWidth < width || previewHeight < height)
        {
            image.Resize((uint)previewWidth, (uint)previewHeight);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pngBytes = image.ToByteArray(MagickFormat.Png32);
        using var stream = new MemoryStream(pngBytes);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("BigTIFF 金字塔层没有可显示的图像帧。");
        }

        var bitmap = decoder.Frames[0];
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return new ImageDocument(
            path,
            bitmap,
            $"{GetMagickFormatName(path, MagickFormat.Tiff)} / 金字塔安全预览 / Magick.NET",
            fileInfo.Length,
            fileInfo.LastWriteTime,
            [],
            pixelWidth: sourceWidth,
            pixelHeight: sourceHeight,
            isPreview: true,
            isLargeImagePreview: true);
    }

    private static ImageDocument CreatePreviewDocumentWithWicMetadata(string path, BitmapSource preview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Image file does not exist.", path);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);

        cancellationToken.ThrowIfCancellationRequested();
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("Image has no displayable frames.");
        }

        var frame = decoder.Frames[0];
        LargeImagePolicy.ValidateSafetyPreviewSource(frame.PixelWidth, frame.PixelHeight);
        var isLargeImagePreview = LargeImagePolicy.RequiresSafetyPreview(frame.PixelWidth, frame.PixelHeight);

        var orientation = ReadExifOrientation(frame);
        var (orientedWidth, orientedHeight) = GetOrientedDimensions(frame.PixelWidth, frame.PixelHeight, orientation);
        return new ImageDocument(
            path,
            preview,
            GetFormatName(path, decoder),
            fileInfo.Length,
            fileInfo.LastWriteTime,
            [],
            pixelWidth: orientedWidth,
            pixelHeight: orientedHeight,
            isPreview: true,
            isLargeImagePreview: isLargeImagePreview);
    }

    private static ImageDocument CreatePreviewDocumentWithMagickMetadata(
        string path,
        BitmapSource preview,
        CancellationToken cancellationToken,
        Exception wicException)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var fileInfo = new FileInfo(path);
            var imageInfo = new MagickImageInfo(path);
            var sourceFormat = imageInfo.Format;
            var (width, height) = GetMagickOrientedDimensions(imageInfo);
            LargeImagePolicy.ValidateSourceDimensions(width, height);
            if (!IsTiffContainer(path, sourceFormat)
                || !LargeImagePolicy.RequiresSafetyPreview(width, height))
            {
                LargeImagePolicy.ValidateSafetyPreviewSource(width, height);
            }

            var isLargeImagePreview = LargeImagePolicy.RequiresSafetyPreview(width, height);
            var isTiffOverviewPreview = isLargeImagePreview && IsTiffContainer(path, sourceFormat);

            return new ImageDocument(
                path,
                preview,
                isTiffOverviewPreview
                    ? $"{GetMagickFormatName(path, MagickFormat.Tiff)} / 金字塔安全预览 / Magick.NET"
                    : $"{GetMagickFormatName(path, sourceFormat)} / Magick.NET",
                fileInfo.Length,
                fileInfo.LastWriteTime,
                [],
                pixelWidth: width,
                pixelHeight: height,
                isPreview: true,
                isLargeImagePreview: isLargeImagePreview);
        }
        catch (Exception ex) when (ex is MagickException or NotSupportedException or InvalidOperationException or IOException)
        {
            throw new InvalidDataException(
                "WIC metadata failed; Magick.NET metadata also failed.\nWIC error: "
                + wicException.Message
                + "\nMagick error: "
                + ex.Message,
                ex);
        }
    }

    private static ImageDocument LoadWithWic(string path, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        using (var probeStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            var probeDecoder = BitmapDecoder.Create(
                probeStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (probeDecoder.Frames.Count == 0)
            {
                throw new InvalidDataException("Image has no displayable frames.");
            }

            var probeFrame = probeDecoder.Frames[0];
            LargeImagePolicy.ValidateFullResolutionSource(probeFrame.PixelWidth, probeFrame.PixelHeight);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        cancellationToken.ThrowIfCancellationRequested();

        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("Image has no displayable frames.");
        }

        var frame = decoder.Frames[0];
        LargeImagePolicy.ValidateFullResolutionSource(frame.PixelWidth, frame.PixelHeight);

        var orientation = ReadExifOrientation(frame);
        var animationFrames = LoadAnimationFrames(path, decoder, orientation, cancellationToken);
        var bitmap = animationFrames.Count > 0 ? animationFrames[0].Bitmap : ApplyExifOrientation(frame, orientation);
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return new ImageDocument(
            path,
            bitmap,
            GetFormatName(path, decoder),
            fileInfo.Length,
            fileInfo.LastWriteTime,
            animationFrames);
    }

    private static bool CanFallbackToMagick(Exception exception)
    {
        return exception is NotSupportedException
            or FileFormatException
            or COMException
            or IOException
            or ArgumentOutOfRangeException
            or ArgumentException;
    }

    private static bool SupportsNativePreviewScaling(BitmapDecoder decoder)
    {
        return decoder is JpegBitmapDecoder or PngBitmapDecoder;
    }

    private static ImageDocument LoadWithMagick(string path, CancellationToken cancellationToken, Exception wicException)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var fileInfo = new FileInfo(path);
            var imageInfo = new MagickImageInfo(path);
            var (metadataWidth, metadataHeight) = GetMagickOrientedDimensions(imageInfo);
            LargeImagePolicy.ValidateFullResolutionSource(metadataWidth, metadataHeight);

            using var image = new MagickImage(path);
            cancellationToken.ThrowIfCancellationRequested();

            image.AutoOrient();
            var width = checked((int)image.Width);
            var height = checked((int)image.Height);
            LargeImagePolicy.ValidateFullResolutionSource(width, height);

            var sourceFormat = image.Format;
            var pngBytes = image.ToByteArray(MagickFormat.Png32);
            using var stream = new MemoryStream(pngBytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("Image has no displayable frames.");
            }

            var bitmap = decoder.Frames[0];
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return new ImageDocument(
                path,
                bitmap,
                $"{GetMagickFormatName(path, sourceFormat)} / Magick.NET",
                fileInfo.Length,
                fileInfo.LastWriteTime,
                []);
        }
        catch (Exception ex) when (ex is MagickException or NotSupportedException or InvalidOperationException or IOException)
        {
            throw new InvalidDataException(
                "WIC decode failed; Magick.NET fallback also failed.\nWIC error: "
                + wicException.Message
                + "\nMagick error: "
                + ex.Message,
                ex);
        }
    }

    private static string GetMagickFormatName(string path, MagickFormat format)
    {
        if (format != MagickFormat.Unknown)
        {
            return format.ToString().ToUpperInvariant();
        }

        return Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
    }

    private static bool IsTiffContainer(string path, MagickFormat format)
    {
        if (format == MagickFormat.Tiff)
        {
            return true;
        }

        Span<byte> signature = stackalloc byte[4];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Read(signature) != signature.Length)
        {
            return false;
        }

        return signature.SequenceEqual("II*\0"u8)
            || signature.SequenceEqual("II+\0"u8)
            || signature.SequenceEqual("MM\0*"u8)
            || signature.SequenceEqual("MM\0+"u8);
    }

    private static (int Width, int Height) GetMagickOrientedDimensions(IMagickImageInfo imageInfo)
    {
        var width = checked((int)imageInfo.Width);
        var height = checked((int)imageInfo.Height);
        if (imageInfo.Orientation is OrientationType.LeftTop
            or OrientationType.RightTop
            or OrientationType.RightBottom
            or OrientationType.LeftBottom)
        {
            (width, height) = (height, width);
        }

        return (width, height);
    }

    private static bool IsRadianceHdrPath(string path)
    {
        return Path.GetExtension(path).Equals(".hdr", StringComparison.OrdinalIgnoreCase);
    }

    private static ImageDocument LoadRadianceHdr(string path, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var resolution = ReadRadianceHeader(stream);
        var pixelCount = (long)resolution.Width * resolution.Height;
        if (pixelCount <= 0)
        {
            throw new InvalidDataException("HDR image dimensions are invalid.");
        }

        if (pixelCount > MaxHdrPixelCount)
        {
            throw new InvalidDataException($"HDR image dimensions are too large; limit is {MaxHdrPixelCount:N0} pixels.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var rgbe = ReadRadiancePixels(stream, resolution, cancellationToken);
        var pixels = ToneMapRadiancePixels(rgbe, resolution.Width, resolution.Height);
        var bitmap = BitmapSource.Create(
            resolution.Width,
            resolution.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            checked(resolution.Width * 4));

        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return new ImageDocument(
            path,
            bitmap,
            "HDR / Radiance RGBE (SDR preview)",
            fileInfo.Length,
            fileInfo.LastWriteTime,
            []);
    }

    private static RadianceResolution ReadRadianceHeader(Stream stream)
    {
        var foundFormat = false;
        string? resolutionLine = null;

        while (ReadAsciiLine(stream) is { } line)
        {
            if (line.StartsWith("FORMAT=32-bit_rle_rgbe", StringComparison.OrdinalIgnoreCase))
            {
                foundFormat = true;
            }

            if (TryParseRadianceResolution(line, out _))
            {
                resolutionLine = line;
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                while (ReadAsciiLine(stream) is { } nextLine)
                {
                    if (string.IsNullOrWhiteSpace(nextLine))
                    {
                        continue;
                    }

                    resolutionLine = nextLine;
                    break;
                }

                break;
            }
        }

        if (!foundFormat)
        {
            throw new InvalidDataException("Only Radiance RGBE .hdr images are supported.");
        }

        if (resolutionLine is null || !TryParseRadianceResolution(resolutionLine, out var resolution))
        {
            throw new InvalidDataException("HDR image is missing a valid resolution line.");
        }

        return resolution;
    }

    private static string? ReadAsciiLine(Stream stream)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (value == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add((byte)value);
        }
    }

    private static bool TryParseRadianceResolution(string line, out RadianceResolution resolution)
    {
        resolution = default;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        var width = 0;
        var height = 0;
        var flipX = false;
        var flipY = false;

        for (var i = 0; i < parts.Length; i += 2)
        {
            var axis = parts[i];
            if (axis.Length != 2 || !int.TryParse(parts[i + 1], out var value) || value <= 0)
            {
                return false;
            }

            if (axis[1] is 'X' or 'x')
            {
                width = value;
                flipX = axis[0] == '-';
            }
            else if (axis[1] is 'Y' or 'y')
            {
                height = value;
                flipY = axis[0] == '+';
            }
            else
            {
                return false;
            }
        }

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        resolution = new RadianceResolution(width, height, flipX, flipY);
        return true;
    }

    private static byte[] ReadRadiancePixels(Stream stream, RadianceResolution resolution, CancellationToken cancellationToken)
    {
        var output = new byte[checked(resolution.Width * resolution.Height * 4)];

        for (var y = 0; y < resolution.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scanline = ReadRadianceScanline(stream, resolution.Width);
            var targetY = resolution.FlipY ? resolution.Height - 1 - y : y;

            for (var x = 0; x < resolution.Width; x++)
            {
                var targetX = resolution.FlipX ? resolution.Width - 1 - x : x;
                Buffer.BlockCopy(scanline, x * 4, output, (targetY * resolution.Width + targetX) * 4, 4);
            }
        }

        return output;
    }

    private static byte[] ReadRadianceScanline(Stream stream, int width)
    {
        var first = ReadRequiredByte(stream);
        var second = ReadRequiredByte(stream);
        var third = ReadRequiredByte(stream);
        var fourth = ReadRequiredByte(stream);

        if (width is >= 8 and <= 0x7FFF
            && first == 2
            && second == 2
            && (third & 0x80) == 0
            && ((third << 8) | fourth) == width)
        {
            return ReadRadianceRleScanline(stream, width);
        }

        var scanline = new byte[checked(width * 4)];
        scanline[0] = (byte)first;
        scanline[1] = (byte)second;
        scanline[2] = (byte)third;
        scanline[3] = (byte)fourth;
        ReadExactly(stream, scanline, 4, scanline.Length - 4);
        return scanline;
    }

    private static byte[] ReadRadianceRleScanline(Stream stream, int width)
    {
        var channels = new byte[checked(width * 4)];

        for (var channel = 0; channel < 4; channel++)
        {
            var x = 0;
            while (x < width)
            {
                var marker = ReadRequiredByte(stream);
                if (marker > 128)
                {
                    var count = marker - 128;
                    var value = (byte)ReadRequiredByte(stream);
                    if (count <= 0 || x + count > width)
                    {
                        throw new InvalidDataException("HDR RLE data is invalid.");
                    }

                    Array.Fill(channels, value, channel * width + x, count);
                    x += count;
                }
                else
                {
                    var count = marker;
                    if (count <= 0 || x + count > width)
                    {
                        throw new InvalidDataException("HDR RLE data is invalid.");
                    }

                    ReadExactly(stream, channels, channel * width + x, count);
                    x += count;
                }
            }
        }

        var scanline = new byte[checked(width * 4)];
        for (var x = 0; x < width; x++)
        {
            scanline[x * 4 + 0] = channels[x];
            scanline[x * 4 + 1] = channels[width + x];
            scanline[x * 4 + 2] = channels[width * 2 + x];
            scanline[x * 4 + 3] = channels[width * 3 + x];
        }

        return scanline;
    }

    private static byte[] ToneMapRadiancePixels(byte[] rgbe, int width, int height)
    {
        var pixelCount = width * height;
        var logLuminanceSum = 0.0;
        var luminanceCount = 0;

        for (var i = 0; i < pixelCount; i++)
        {
            var offset = i * 4;
            var exponent = rgbe[offset + 3];
            if (exponent == 0)
            {
                continue;
            }

            var scale = Math.Pow(2, exponent - 136);
            var red = rgbe[offset + 0] * scale;
            var green = rgbe[offset + 1] * scale;
            var blue = rgbe[offset + 2] * scale;
            var luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
            if (luminance > 0)
            {
                logLuminanceSum += Math.Log(luminance + 1e-6);
                luminanceCount++;
            }
        }

        var logAverage = luminanceCount > 0 ? Math.Exp(logLuminanceSum / luminanceCount) : 1.0;
        var exposure = 0.28 / Math.Max(logAverage, 1e-6);
        var pixels = new byte[checked(pixelCount * 4)];

        for (var i = 0; i < pixelCount; i++)
        {
            var sourceOffset = i * 4;
            var targetOffset = i * 4;
            var exponent = rgbe[sourceOffset + 3];
            if (exponent == 0)
            {
                pixels[targetOffset + 3] = 255;
                continue;
            }

            var scale = Math.Pow(2, exponent - 136) * exposure;
            pixels[targetOffset + 2] = ToneMapChannel(rgbe[sourceOffset + 0] * scale);
            pixels[targetOffset + 1] = ToneMapChannel(rgbe[sourceOffset + 1] * scale);
            pixels[targetOffset + 0] = ToneMapChannel(rgbe[sourceOffset + 2] * scale);
            pixels[targetOffset + 3] = 255;
        }

        return pixels;
    }

    private static byte ToneMapChannel(double value)
    {
        var mapped = value / (1 + value);
        mapped = Math.Pow(Math.Clamp(mapped, 0, 1), 1.0 / 2.2);
        return (byte)Math.Round(mapped * 255);
    }

    private static int ReadRequiredByte(Stream stream)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException("HDR image data is incomplete.");
        }

        return value;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var read = stream.Read(buffer, offset, count);
            if (read <= 0)
            {
                throw new EndOfStreamException("HDR image data is incomplete.");
            }

            offset += read;
            count -= read;
        }
    }

    private static IReadOnlyList<ImageAnimationFrame> LoadAnimationFrames(
        string path,
        BitmapDecoder decoder,
        int orientation,
        CancellationToken cancellationToken)
    {
        if (decoder.Frames.Count <= 1 || !CanAnimatePath(path))
        {
            return [];
        }

        if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return LoadGifAnimationFrames(decoder, cancellationToken);
        }

        if (decoder.Frames.Count > MaxAnimationFrameCount)
        {
            return [];
        }

        var totalPixelFrames = 0L;
        foreach (var frame in decoder.Frames)
        {
            totalPixelFrames += (long)frame.PixelWidth * frame.PixelHeight;
            if (totalPixelFrames > MaxAnimationPixelFrames)
            {
                return [];
            }
        }

        var frames = new List<ImageAnimationFrame>(decoder.Frames.Count);
        foreach (var frame in decoder.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bitmap = ApplyExifOrientation(frame, orientation);
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            frames.Add(new ImageAnimationFrame(bitmap, ReadFrameDelay(frame)));
        }

        return frames.Count > 1 ? frames : [];
    }

    private static IReadOnlyList<ImageAnimationFrame> LoadGifAnimationFrames(
        BitmapDecoder decoder,
        CancellationToken cancellationToken)
    {
        if (decoder.Frames.Count > MaxAnimationFrameCount)
        {
            return [];
        }

        var canvasSize = ReadGifCanvasSize(decoder);
        var canvasPixelCount = (long)canvasSize.Width * canvasSize.Height;
        if (canvasPixelCount <= 0 || canvasPixelCount > LargeImagePolicy.FullResolutionPixelLimit)
        {
            return [];
        }

        if (canvasPixelCount * decoder.Frames.Count > MaxAnimationPixelFrames)
        {
            return [];
        }

        var canvas = new byte[checked(canvasSize.Width * canvasSize.Height * 4)];
        var output = new List<ImageAnimationFrame>(decoder.Frames.Count);

        foreach (var frame in decoder.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = ReadGifFrameMetadata(frame);
            var previousCanvas = metadata.Disposal == 3 ? (byte[])canvas.Clone() : null;

            BlendFrame(canvas, canvasSize.Width, canvasSize.Height, frame, metadata);

            var snapshot = BitmapSource.Create(
                canvasSize.Width,
                canvasSize.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                canvas,
                canvasSize.Width * 4);

            if (snapshot.CanFreeze)
            {
                snapshot.Freeze();
            }

            output.Add(new ImageAnimationFrame(snapshot, metadata.Delay));

            switch (metadata.Disposal)
            {
                case 2:
                    ClearRect(canvas, canvasSize.Width, canvasSize.Height, metadata.Left, metadata.Top, frame.PixelWidth, frame.PixelHeight);
                    break;

                case 3 when previousCanvas is not null:
                    Buffer.BlockCopy(previousCanvas, 0, canvas, 0, canvas.Length);
                    break;
            }
        }

        return output.Count > 1 ? output : [];
    }

    private static GifCanvasSize ReadGifCanvasSize(BitmapDecoder decoder)
    {
        var metadata = decoder.Metadata as BitmapMetadata;
        var width = ReadMetadataInt(metadata, "/logscrdesc/Width");
        var height = ReadMetadataInt(metadata, "/logscrdesc/Height");

        if (width <= 0 || height <= 0)
        {
            width = decoder.Frames.Max(static frame => ReadGifFrameMetadata(frame).Left + frame.PixelWidth);
            height = decoder.Frames.Max(static frame => ReadGifFrameMetadata(frame).Top + frame.PixelHeight);
        }

        return new GifCanvasSize(width, height);
    }

    private static GifFrameMetadata ReadGifFrameMetadata(BitmapFrame frame)
    {
        var metadata = frame.Metadata as BitmapMetadata;
        return new GifFrameMetadata(
            ReadMetadataInt(metadata, "/imgdesc/Left"),
            ReadMetadataInt(metadata, "/imgdesc/Top"),
            Math.Max(0, ReadMetadataInt(metadata, "/grctlext/Disposal")),
            ReadMetadataBool(metadata, "/grctlext/TransparencyFlag"),
            ReadMetadataInt(metadata, "/grctlext/TransparentColorIndex"),
            ReadFrameDelay(frame));
    }

    private static bool ReadMetadataBool(BitmapMetadata? metadata, string query)
    {
        if (metadata is null)
        {
            return false;
        }

        try
        {
            if (!metadata.ContainsQuery(query))
            {
                return false;
            }

            return metadata.GetQuery(query) switch
            {
                bool boolValue => boolValue,
                byte byteValue => byteValue != 0,
                ushort ushortValue => ushortValue != 0,
                short shortValue => shortValue != 0,
                int intValue => intValue != 0,
                uint uintValue => uintValue != 0,
                _ => false,
            };
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static int ReadMetadataInt(BitmapMetadata? metadata, string query)
    {
        if (metadata is null)
        {
            return 0;
        }

        try
        {
            if (!metadata.ContainsQuery(query))
            {
                return 0;
            }

            return metadata.GetQuery(query) switch
            {
                byte byteValue => byteValue,
                ushort ushortValue => ushortValue,
                short shortValue => Math.Max(0, (int)shortValue),
                int intValue => Math.Max(0, intValue),
                uint uintValue => uintValue > int.MaxValue ? int.MaxValue : (int)uintValue,
                _ => 0,
            };
        }
        catch (NotSupportedException)
        {
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static void BlendFrame(byte[] canvas, int canvasWidth, int canvasHeight, BitmapFrame source, GifFrameMetadata metadata)
    {
        if (TryBlendIndexedFrame(canvas, canvasWidth, canvasHeight, source, metadata))
        {
            return;
        }

        BitmapSource frame = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var frameWidth = frame.PixelWidth;
        var frameHeight = frame.PixelHeight;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return;
        }

        var stride = checked(frameWidth * 4);
        var pixels = new byte[checked(stride * frameHeight)];
        frame.CopyPixels(pixels, stride, 0);

        for (var y = 0; y < frameHeight; y++)
        {
            var targetY = metadata.Top + y;
            if (targetY < 0 || targetY >= canvasHeight)
            {
                continue;
            }

            for (var x = 0; x < frameWidth; x++)
            {
                var targetX = metadata.Left + x;
                if (targetX < 0 || targetX >= canvasWidth)
                {
                    continue;
                }

                var sourceOffset = y * stride + x * 4;
                var alpha = pixels[sourceOffset + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var targetOffset = (targetY * canvasWidth + targetX) * 4;
                BlendPixel(
                    canvas,
                    targetOffset,
                    pixels[sourceOffset + 0],
                    pixels[sourceOffset + 1],
                    pixels[sourceOffset + 2],
                    alpha);
            }
        }
    }

    private static bool TryBlendIndexedFrame(byte[] canvas, int canvasWidth, int canvasHeight, BitmapFrame source, GifFrameMetadata metadata)
    {
        var bitsPerPixel = source.Format.BitsPerPixel;
        if (source.Palette is null || bitsPerPixel is not (1 or 2 or 4 or 8))
        {
            return false;
        }

        var frameWidth = source.PixelWidth;
        var frameHeight = source.PixelHeight;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return true;
        }

        var stride = checked((frameWidth * bitsPerPixel + 7) / 8);
        var pixels = new byte[checked(stride * frameHeight)];
        source.CopyPixels(pixels, stride, 0);

        for (var y = 0; y < frameHeight; y++)
        {
            var targetY = metadata.Top + y;
            if (targetY < 0 || targetY >= canvasHeight)
            {
                continue;
            }

            for (var x = 0; x < frameWidth; x++)
            {
                var targetX = metadata.Left + x;
                if (targetX < 0 || targetX >= canvasWidth)
                {
                    continue;
                }

                var paletteIndex = ReadPaletteIndex(pixels, stride, bitsPerPixel, x, y);
                if (metadata.HasTransparency && paletteIndex == metadata.TransparentColorIndex)
                {
                    continue;
                }

                if (paletteIndex < 0 || paletteIndex >= source.Palette.Colors.Count)
                {
                    continue;
                }

                var color = source.Palette.Colors[paletteIndex];
                if (color.A == 0)
                {
                    continue;
                }

                var targetOffset = (targetY * canvasWidth + targetX) * 4;
                BlendPixel(canvas, targetOffset, color.B, color.G, color.R, color.A);
            }
        }

        return true;
    }

    private static int ReadPaletteIndex(byte[] pixels, int stride, int bitsPerPixel, int x, int y)
    {
        var rowOffset = y * stride;
        return bitsPerPixel switch
        {
            1 => (pixels[rowOffset + x / 8] >> (7 - x % 8)) & 0x1,
            2 => (pixels[rowOffset + x / 4] >> (6 - x % 4 * 2)) & 0x3,
            4 => (pixels[rowOffset + x / 2] >> (x % 2 == 0 ? 4 : 0)) & 0xF,
            8 => pixels[rowOffset + x],
            _ => 0,
        };
    }

    private static void BlendPixel(byte[] target, int targetOffset, byte blue, byte green, byte red, byte alpha)
    {
        if (alpha == 255 || target[targetOffset + 3] == 0)
        {
            target[targetOffset + 0] = blue;
            target[targetOffset + 1] = green;
            target[targetOffset + 2] = red;
            target[targetOffset + 3] = alpha;
            return;
        }

        var sourceAlpha = alpha / 255.0;
        var targetAlpha = target[targetOffset + 3] / 255.0;
        var outAlpha = sourceAlpha + targetAlpha * (1 - sourceAlpha);
        if (outAlpha <= 0)
        {
            target[targetOffset + 0] = 0;
            target[targetOffset + 1] = 0;
            target[targetOffset + 2] = 0;
            target[targetOffset + 3] = 0;
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            var sourceComponent = i switch
            {
                0 => blue,
                1 => green,
                _ => red,
            };
            var sourceColor = sourceComponent / 255.0;
            var targetColor = target[targetOffset + i] / 255.0;
            var outColor = (sourceColor * sourceAlpha + targetColor * targetAlpha * (1 - sourceAlpha)) / outAlpha;
            target[targetOffset + i] = (byte)Math.Round(outColor * 255);
        }

        target[targetOffset + 3] = (byte)Math.Round(outAlpha * 255);
    }

    private static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var targetY = top + y;
            if (targetY < 0 || targetY >= canvasHeight)
            {
                continue;
            }

            for (var x = 0; x < width; x++)
            {
                var targetX = left + x;
                if (targetX < 0 || targetX >= canvasWidth)
                {
                    continue;
                }

                var offset = (targetY * canvasWidth + targetX) * 4;
                canvas[offset + 0] = 0;
                canvas[offset + 1] = 0;
                canvas[offset + 2] = 0;
                canvas[offset + 3] = 0;
            }
        }
    }

    private static bool CanAnimatePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".apng", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFormatName(string path, BitmapDecoder decoder)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var codecName = decoder.CodecInfo?.FriendlyName;

        if (string.IsNullOrWhiteSpace(codecName))
        {
            return extension;
        }

        return $"{extension} / {codecName}";
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            3 => new RotateTransform(180),
            6 => new RotateTransform(90),
            8 => new RotateTransform(270),
            _ => null,
        };

        if (transform is null)
        {
            return source;
        }

        var transformed = new TransformedBitmap();
        transformed.BeginInit();
        transformed.Source = source;
        transformed.Transform = transform;
        transformed.EndInit();
        return transformed;
    }

    private static (int Width, int Height) GetOrientedDimensions(int width, int height, int orientation)
    {
        return orientation is 5 or 6 or 7 or 8
            ? (height, width)
            : (width, height);
    }

    private static TimeSpan ReadFrameDelay(BitmapFrame frame)
    {
        if (frame.Metadata is BitmapMetadata metadata)
        {
            foreach (var query in new[] { "/grctlext/Delay", "/grctlext/{ushort=4}" })
            {
                try
                {
                    if (!metadata.ContainsQuery(query))
                    {
                        continue;
                    }

                    var value = metadata.GetQuery(query);
                    var hundredths = value switch
                    {
                        ushort ushortValue => ushortValue,
                        short shortValue => Math.Max(0, (int)shortValue),
                        int intValue => Math.Max(0, intValue),
                        uint uintValue => uintValue > int.MaxValue ? int.MaxValue : (int)uintValue,
                        _ => 0,
                    };

                    if (hundredths <= 0)
                    {
                        break;
                    }

                    var delay = TimeSpan.FromMilliseconds(hundredths * 10);
                    return delay < MinimumAnimationDelay ? MinimumAnimationDelay : delay;
                }
                catch (NotSupportedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
        }

        return DefaultAnimationDelay;
    }

    private static int ReadExifOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata)
        {
            return 1;
        }

        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "/xmp/tiff:Orientation" })
        {
            try
            {
                if (!metadata.ContainsQuery(query))
                {
                    continue;
                }

                var value = metadata.GetQuery(query);
                if (value is ushort ushortValue)
                {
                    return ushortValue;
                }

                if (value is short shortValue)
                {
                    return shortValue;
                }

                if (value is int intValue)
                {
                    return intValue;
                }

                if (value is string stringValue && int.TryParse(stringValue, out var parsed))
                {
                    return parsed;
                }
            }
            catch (NotSupportedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return 1;
    }

    private readonly record struct GifCanvasSize(int Width, int Height);

    private readonly record struct GifFrameMetadata(
        int Left,
        int Top,
        int Disposal,
        bool HasTransparency,
        int TransparentColorIndex,
        TimeSpan Delay);

    private readonly record struct RadianceResolution(
        int Width,
        int Height,
        bool FlipX,
        bool FlipY);
}
