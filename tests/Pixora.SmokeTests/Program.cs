using ImageMagick;
using Pixora.Services;
using Pixora.Controls;
using Pixora.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Diagnostics;
using System.Reflection;

namespace Pixora.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = FindRepositoryRoot();
        var imageFolder = Path.Combine(root, "test-images");
        var firstImage = Path.Combine(imageFolder, "1.png");
        var brokenImage = Path.Combine(imageFolder, "broken.jpg");
        var animatedGif = Path.Combine(imageFolder, "animated.gif");
        var hdrImage = Path.Combine(imageFolder, "sample.hdr");
        var avifImage = Path.Combine(imageFolder, "sample.avif");
        EnsureAnimatedGif(animatedGif);
        EnsureRadianceHdr(hdrImage);
        EnsureAvif(avifImage);

        var catalog = new ImageCatalog();
        catalog.LoadFromFolder(imageFolder);
        Assert(catalog.Count == 7, $"Expected 7 supported image paths, got {catalog.Count}.");
        Assert(Path.GetFileName(catalog.CurrentPath) == "1.png", "Natural sort should place 1.png first.");

        catalog.MoveNext();
        Assert(Path.GetFileName(catalog.CurrentPath) == "2.png", "Natural sort should place 2.png before 10.png.");

        catalog.MoveNext();
        Assert(Path.GetFileName(catalog.CurrentPath) == "10.png", "Natural sort should place 10.png after 2.png.");

        Assert(catalog.MoveTo(firstImage), "Catalog should move to an existing image path.");
        Assert(Path.GetFileName(catalog.CurrentPath) == "1.png", "MoveTo should select the requested image.");
        var forwardNeighbors = catalog.GetDirectionalNeighborPaths(direction: 1, forwardRadius: 2, oppositeRadius: 1);
        Assert(Path.GetFileName(forwardNeighbors[0]) == "2.png" && Path.GetFileName(forwardNeighbors[1]) == "10.png", "Directional preloading should prioritize the next images in navigation order.");
        AssertImageCatalogAddsSavedFileKeepingCurrent(root);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var document = ImageLoader.Load(firstImage, cts.Token);
        Assert(document.PixelWidth == 1 && document.PixelHeight == 1, "1.png should decode as a 1 x 1 image.");
        AssertAnimatedGifLoads(animatedGif);
        AssertImageCacheSkipsAnimatedDocuments(animatedGif);
        AssertImageCacheUsesMemoryBudget();
        AssertManyFrameGifLoads(root);
        AssertRadianceHdrLoads(hdrImage);
        AssertAvifLoads(avifImage);
        AssertLargeImageSafetyPreview(root);
        AssertTiffOverviewSelector();
        AssertWebImageExtensions();
        AssertMediaFormatRegistry();
        AssertFileAssociationMoveRepair();
        AssertMediaCatalogLoaderCancellation(imageFolder);
        AssertVideoMediaSupport(root);
        AssertCatalogSortModes(root);
        AssertCatalogMetadataCache(root);
        AssertCatalogUpdateHelpers(imageFolder);
        AssertCatalogIncrementalChanges(root);
        AssertFolderChangeMonitor(root);
        AssertFavoriteStore(root);
        AssertShortcutSettings();
        AssertQuickSearchMatcher();
        AssertQuickSearchInteractionState();
        AssertQuickSearchIndexPerformance();
        AssertLargeCatalogPerformance();
        AssertViewerSettings(root);
        AssertAtomicJsonPersistence(root);
        AssertMainWindowInputMethodDisabled(root);
        AssertMainWindowExperienceControls(root);
        AssertDpiAndAccessibility(root);
        AssertMainWindowInitializes();
        AssertSettingsWindowInitializes();
        AssertMemoryCacheCoordinator();
        AssertThumbnailDiskCache(root);
        AssertThumbnailImageLoader(firstImage, root);
        AssertRollingTextFile(root);
        AssertBatchCompressionSettings(root);
        AssertFitMathKeepsTallImagesInsideViewport();
        AssertFitMathDoesNotUpscaleSmallImagesByDefault();
        AssertCropMathClipsSelectionToImagePixels();
        AssertCircularCropMasksCorners();
        AssertImageCompressorSavesJpegAndPng(root);
        AssertBatchImageCompressor(root);
        AssertBitmapViewerRendersFullTallImage();

        var options = ParseArgs(args);

        var damagedFailed = false;
        try
        {
            ImageLoader.Load(brokenImage, CancellationToken.None);
        }
        catch
        {
            damagedFailed = true;
        }

        Assert(damagedFailed, "broken.jpg should fail decoding without escaping the caller.");

        if (options.MediaFolder is not null)
        {
            AssertMediaFolderStress(options.MediaFolder, options.SampleCount, options.VideoSampleCount);
        }

        foreach (var externalImage in options.ExternalImages)
        {
            AssertExternalImageRendersFull(externalImage);
        }

        foreach (var largeImage in options.LargeImages)
        {
            AssertExternalLargeImageSafetyPreview(largeImage);
        }

        Console.WriteLine("Smoke tests passed.");
        return 0;
    }

    private static TestOptions ParseArgs(string[] args)
    {
        var options = new TestOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.Equals("--media-folder", StringComparison.OrdinalIgnoreCase))
            {
                options.MediaFolder = RequireOptionValue(args, ref index, arg);
                continue;
            }

            if (arg.Equals("--sample-count", StringComparison.OrdinalIgnoreCase))
            {
                options.SampleCount = int.Parse(RequireOptionValue(args, ref index, arg));
                continue;
            }

            if (arg.Equals("--video-sample-count", StringComparison.OrdinalIgnoreCase))
            {
                options.VideoSampleCount = int.Parse(RequireOptionValue(args, ref index, arg));
                continue;
            }

            if (arg.Equals("--large-image", StringComparison.OrdinalIgnoreCase))
            {
                options.LargeImages.Add(RequireOptionValue(args, ref index, arg));
                continue;
            }

            options.ExternalImages.Add(arg);
        }

        return options;
    }

    private static void AssertImageCatalogAddsSavedFileKeepingCurrent(string root)
    {
        var folder = Path.Combine(root, "test-output", "catalog-saved-file");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        var currentPath = Path.Combine(folder, "1.png");
        var existingPath = Path.Combine(folder, "3.png");
        var savedPath = Path.Combine(folder, "2_压缩.jpg");
        File.Copy(Path.Combine(root, "test-images", "1.png"), currentPath);
        File.Copy(Path.Combine(root, "test-images", "2.png"), existingPath);

        var catalog = new ImageCatalog();
        catalog.LoadFromFolder(folder);
        Assert(catalog.MoveTo(currentPath), "Catalog should select the source image before saving a sibling file.");

        File.Copy(Path.Combine(root, "test-images", "2.png"), savedPath);
        Assert(catalog.AddOrUpdateExistingMediaPath(savedPath), "Catalog should add a supported saved image from the current folder.");
        Assert(catalog.Count == 3, "Catalog should show the saved image immediately.");
        Assert(string.Equals(catalog.CurrentPath, currentPath, StringComparison.OrdinalIgnoreCase), "Adding a saved image should keep the current image selected.");
    }

    private static string RequireOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static void AssertBitmapViewerRendersFullTallImage()
    {
        const int width = 80;
        const int height = 200;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var topHalf = y < height / 2;
                pixels[offset + 0] = topHalf ? (byte)0 : (byte)255;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = topHalf ? (byte)255 : (byte)0;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);

        bitmap.Freeze();

        var viewer = new BitmapViewer
        {
            Source = bitmap,
            ViewScale = 0.5,
            OffsetX = 10,
            OffsetY = 5,
        };

        viewer.Measure(new Size(100, 120));
        viewer.Arrange(new Rect(0, 0, 100, 120));
        viewer.UpdateLayout();

        var rendered = new RenderTargetBitmap(100, 120, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(viewer);

        AssertPixelHasColor(rendered, 20, 10, expectRed: true);
        AssertPixelHasColor(rendered, 20, 100, expectRed: false);
    }

    private static void AssertAnimatedGifLoads(string path)
    {
        var document = ImageLoader.Load(path, CancellationToken.None);
        Assert(document.IsAnimated, "animated.gif should be recognized as animated.");
        Assert(document.AnimationFrames.Count == 2, $"animated.gif should have 2 frames, got {document.AnimationFrames.Count}.");
        Assert(document.AnimationFrames.All(frame => frame.Delay >= TimeSpan.FromMilliseconds(20)), "Animated GIF delays should be clamped to a usable minimum.");
        Assert(document.PixelWidth == 1 && document.PixelHeight == 1, "animated.gif should decode as a 1 x 1 image.");
    }

    private static void AssertImageCacheSkipsAnimatedDocuments(string path)
    {
        var document = ImageLoader.Load(path, CancellationToken.None);
        var cache = ImageCache.FromMegabytes(16);
        cache.Add(document);
        Assert(!cache.TryGet(path, out _), "Animated image documents should not stay in the regular image cache.");
    }

    private static void AssertImageCacheUsesMemoryBudget()
    {
        var cache = new ImageCache(maxBytes: 128);
        var first = CreateCacheTestDocument("cache-first.png", 4, 4);
        var second = CreateCacheTestDocument("cache-second.png", 4, 4);
        var third = CreateCacheTestDocument("cache-third.png", 4, 4);

        cache.Add(first);
        cache.Add(second);
        Assert(cache.Count == 2, $"Cache should keep two 64-byte images inside a 128-byte budget, got {cache.Count}.");

        Assert(cache.TryGet(first.Path, out _), "Touching the first image should make it recently used.");
        cache.Add(third);

        Assert(cache.TryGet(first.Path, out _), "Recently used cached image should be retained.");
        Assert(!cache.TryGet(second.Path, out _), "Least recently used image should be evicted when memory budget is exceeded.");
        Assert(cache.TryGet(third.Path, out _), "Newly added image should be retained.");
        Assert(cache.TotalEstimatedBytes <= cache.MaxBytes, "Cache should trim estimated decoded bytes back to the budget.");
    }

    private static ImageDocument CreateCacheTestDocument(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();

        return new ImageDocument(path, bitmap, "TEST", pixels.Length, DateTime.Now);
    }

    private static void AssertManyFrameGifLoads(string root)
    {
        var path = Path.Combine(root, "test-output", "many-frame-animation.gif");
        EnsureManyFrameGif(path, frameCount: 600);

        var document = ImageLoader.Load(path, CancellationToken.None);
        Assert(document.IsAnimated, "GIFs above the old 500-frame cutoff should still animate when the pixel-frame budget is safe.");
        Assert(document.AnimationFrames.Count == 600, $"many-frame-animation.gif should have 600 frames, got {document.AnimationFrames.Count}.");
        Assert(document.AnimationFrames.All(frame => frame.Bitmap.PixelWidth == document.PixelWidth && frame.Bitmap.PixelHeight == document.PixelHeight), "Many-frame GIF frames should use the full logical canvas.");
    }

    private static void AssertRadianceHdrLoads(string path)
    {
        var document = ImageLoader.Load(path, CancellationToken.None);
        Assert(document.PixelWidth == 1 && document.PixelHeight == 1, "Radiance HDR should decode as a 1 x 1 image.");
        Assert(document.FormatName.Contains("HDR", StringComparison.OrdinalIgnoreCase), "Radiance HDR should be labeled as HDR.");
    }

    private static void AssertAvifLoads(string path)
    {
        var document = ImageLoader.Load(path, CancellationToken.None);
        Assert(document.PixelWidth == 1 && document.PixelHeight == 1, "AVIF should decode as a 1 x 1 image.");
        Assert(document.FormatName.Contains("MAGICK", StringComparison.OrdinalIgnoreCase)
            || document.FormatName.Contains("AVIF", StringComparison.OrdinalIgnoreCase), "AVIF should report a useful format name.");
    }

    private static void AssertLargeImageSafetyPreview(string root)
    {
        Assert(
            !LargeImagePolicy.RequiresSafetyPreview(15_000, 8_000),
            "Exactly 120 million pixels should remain eligible for full-resolution decoding.");
        Assert(
            LargeImagePolicy.RequiresSafetyPreview(15_001, 8_000),
            "Images above 120 million pixels should use the safety preview path.");

        var standardPreview = LargeImagePolicy.CalculatePreviewDecodeSize(
            16_000,
            8_000,
            requestedMaxWidth: 1_920,
            requestedMaxHeight: 1_080,
            LargeImagePolicy.DefaultPreviewMaximumSide);
        Assert(
            standardPreview == (4_096, 2_048),
            $"Standard oversized preview should preserve aspect ratio at 4096 px, got {standardPreview}.");

        var clampedStandardPreview = LargeImagePolicy.CalculatePreviewDecodeSize(
            16_000,
            8_000,
            requestedMaxWidth: 20_000,
            requestedMaxHeight: 20_000,
            LargeImagePolicy.DefaultPreviewMaximumSide);
        Assert(
            clampedStandardPreview == standardPreview,
            "An oversized preview request must not exceed the selected safety-preview tier.");

        var highPerformancePreview = LargeImagePolicy.CalculatePreviewDecodeSize(
            16_000,
            8_000,
            requestedMaxWidth: 1_920,
            requestedMaxHeight: 1_080,
            LargeImagePolicy.HighPerformancePreviewMaximumSide);
        Assert(
            highPerformancePreview == (8_192, 4_096),
            $"High-performance oversized preview should preserve aspect ratio at 8192 px, got {highPerformancePreview}.");
        Assert(
            LargeImagePolicy.ResolvePreviewMaximumSide(512) == LargeImagePolicy.DefaultPreviewMaximumSide
                && LargeImagePolicy.ResolvePreviewMaximumSide(1_024) == LargeImagePolicy.HighPerformancePreviewMaximumSide,
            "Oversized preview quality should follow the preview-cache performance tier.");

        var safetyLimitRejected = false;
        try
        {
            LargeImagePolicy.ValidateSafetyPreviewSource(50_001, 40_000);
        }
        catch (InvalidDataException)
        {
            safetyLimitRejected = true;
        }

        Assert(safetyLimitRejected, "Sources above two billion pixels should be rejected before decoding.");

        var unsupportedPath = Path.Combine(root, "test-output", "oversized-unsupported.bmp");
        EnsureOversizedRleBitmap(unsupportedPath, width: 12_001, height: 10_000);
        Assert(
            ImageLoader.RequiresSafetyPreview(unsupportedPath, CancellationToken.None),
            "Oversized WIC metadata should select the safety preview path without loading the pixels.");

        var unsupportedPreviewRejected = false;
        try
        {
            _ = ImageLoader.LoadPreviewDocument(
                unsupportedPath,
                maxWidth: 1_920,
                maxHeight: 1_080,
                CancellationToken.None,
                LargeImagePolicy.DefaultPreviewMaximumSide);
        }
        catch (InvalidDataException)
        {
            unsupportedPreviewRejected = true;
        }

        Assert(
            unsupportedPreviewRejected,
            "Oversized formats without native WIC decode scaling should be rejected before pixel decoding.");

        var path = Path.Combine(root, "test-output", "oversized-native-preview.png");
        EnsureOversizedGrayscalePng(path, width: 12_001, height: 10_000);
        Assert(
            ImageLoader.RequiresSafetyPreview(path, CancellationToken.None),
            "Oversized PNG metadata should select the native safety preview path.");

        var decodedPreview = ImageLoader.LoadPreviewDocument(
            path,
            maxWidth: 1_920,
            maxHeight: 1_080,
            CancellationToken.None,
            LargeImagePolicy.DefaultPreviewMaximumSide);
        Assert(decodedPreview.IsLargeImagePreview, "Native oversized WIC decoding should produce a safety preview document.");
        var decodedMaximumSide = Math.Max(decodedPreview.Bitmap.PixelWidth, decodedPreview.Bitmap.PixelHeight);
        Assert(
            decodedMaximumSide is >= 4_090 and <= 4_096,
            $"Standard oversized PNG previews should stay within and close to the 4096 px tier, got {decodedPreview.Bitmap.PixelWidth} x {decodedPreview.Bitmap.PixelHeight}.");
        Assert(
            decodedPreview.PixelWidth == 12_001 && decodedPreview.PixelHeight == 10_000,
            "Scaled oversized WIC previews should preserve their original dimensions as metadata.");

        var preview = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 0, 0, 0, 255 },
            4);
        preview.Freeze();

        var previewDocument = ImageLoader.CreatePreviewDocument(path, preview, CancellationToken.None);
        Assert(previewDocument.IsPreview, "Oversized images should remain marked as previews.");
        Assert(previewDocument.IsLargeImagePreview, "Oversized image metadata should be retained on the preview document.");
        Assert(
            previewDocument.PixelWidth == 12_001 && previewDocument.PixelHeight == 10_000,
            "Safety preview documents should retain the original image dimensions.");

        var fullResolutionRejected = false;
        try
        {
            ImageLoader.Load(path, CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            fullResolutionRejected = true;
        }

        Assert(fullResolutionRejected, "Oversized images should be rejected before full-resolution WIC decoding.");
    }

    private static void EnsureOversizedRleBitmap(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var fullRuns = width / byte.MaxValue;
        var remainder = width % byte.MaxValue;
        var encodedRowBytes = checked((fullRuns * 2) + (remainder > 0 ? 2 : 0) + 2);
        var encodedImageBytes = checked((encodedRowBytes * height) + 2);
        const int pixelOffset = 14 + 40 + 8;
        var fileSize = checked(pixelOffset + encodedImageBytes);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0x4D42);
        writer.Write(fileSize);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(pixelOffset);

        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)8);
        writer.Write(1);
        writer.Write(encodedImageBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(2);
        writer.Write(2);

        writer.Write(new byte[]
        {
            0, 0, 0, 0,
            255, 255, 255, 0,
        });

        for (var y = 0; y < height; y++)
        {
            for (var run = 0; run < fullRuns; run++)
            {
                writer.Write(byte.MaxValue);
                writer.Write((byte)1);
            }

            if (remainder > 0)
            {
                writer.Write((byte)remainder);
                writer.Write((byte)1);
            }

            writer.Write((byte)0);
            writer.Write((byte)0);
        }

        writer.Write((byte)0);
        writer.Write((byte)1);
    }

    private static void EnsureOversizedGrayscalePng(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)width));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), checked((uint)height));
        header[8] = 1;
        header[9] = 0;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WritePngChunk(writer, "IHDR", header);

        byte[] compressedPixels;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(
                compressed,
                System.IO.Compression.CompressionLevel.Fastest,
                leaveOpen: true))
            {
                var scanline = new byte[checked(((width + 7) / 8) + 1)];
                for (var y = 0; y < height; y++)
                {
                    zlib.Write(scanline);
                }
            }

            compressedPixels = compressed.ToArray();
        }

        WritePngChunk(writer, "IDAT", compressedPixels);
        WritePngChunk(writer, "IEND", []);
    }

    private static void WritePngChunk(BinaryWriter writer, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        WriteUInt32BigEndian(writer, checked((uint)data.Length));
        writer.Write(typeBytes);
        writer.Write(data);
        WriteUInt32BigEndian(writer, CalculatePngCrc(typeBytes, data));
    }

    private static uint CalculatePngCrc(byte[] type, byte[] data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type)
        {
            crc = UpdatePngCrc(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdatePngCrc(crc, value);
        }

        return ~crc;
    }

    private static uint UpdatePngCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0
                ? 0xEDB88320U ^ (crc >> 1)
                : crc >> 1;
        }

        return crc;
    }

    private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
    {
        writer.Write(new byte[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value,
        });
    }

    private static void AssertTiffOverviewSelector()
    {
        const int sourceWidth = 193_224;
        const int sourceHeight = 90_014;
        TiffOverviewCandidate[] candidates =
        [
            new(0, sourceWidth, sourceHeight),
            new(1, 1_024, 477),
            new(2, 48_306, 22_503),
            new(3, 12_076, 5_625),
            new(4, 3_019, 1_406),
            new(5, 4_096, 4_096),
        ];

        var standard = TiffOverviewSelector.SelectBest(
            sourceWidth,
            sourceHeight,
            candidates,
            LargeImagePolicy.DefaultPreviewMaximumSide);
        Assert(standard?.FrameIndex == 4, "Standard BigTIFF preview should choose the nearby low-memory pyramid level.");

        var highPerformance = TiffOverviewSelector.SelectBest(
            sourceWidth,
            sourceHeight,
            candidates,
            LargeImagePolicy.HighPerformancePreviewMaximumSide);
        Assert(highPerformance?.FrameIndex == 3, "High-performance BigTIFF preview should choose the sharper safe pyramid level.");

        var thumbnail = TiffOverviewSelector.SelectBest(
            sourceWidth,
            sourceHeight,
            candidates,
            targetMaximumSide: 240);
        Assert(thumbnail?.FrameIndex == 1, "BigTIFF thumbnails should use the embedded thumbnail instead of a large pyramid level.");
    }

    private static void AssertWebImageExtensions()
    {
        foreach (var extension in new[] { ".webp", ".avif", ".avifs", ".jfif", ".jpe", ".apng", ".ico", ".cur" })
        {
            Assert(ImageCatalog.IsSupportedStillImagePath("sample" + extension), $"{extension} should be accepted as a web image format.");
        }

        Assert(!ImageCatalog.IsSupportedStillImagePath("sample.svg"), "SVG should not be listed as bitmap-supported without an SVG renderer.");
        Assert(ImageCatalog.IsLikelyAnimatedImagePath("sample.gif"), "GIF should count as a likely animated image for lightweight stats.");
        Assert(ImageCatalog.IsLikelyAnimatedImagePath("sample.apng"), "APNG should count as a likely animated image for lightweight stats.");
        Assert(!ImageCatalog.IsLikelyAnimatedImagePath("sample.webp"), "Static WebP is common enough that lightweight stats should not count every WebP as animated.");
    }

    private static void AssertMediaFormatRegistry()
    {
        Assert(
            MediaFormatRegistry.SupportedStillImageExtensions.SequenceEqual(FileAssociationService.SupportedExtensions, StringComparer.OrdinalIgnoreCase),
            "File association extensions should use the central still-image format registry.");
        Assert(MediaFormatRegistry.SupportedVideoExtensions.Contains(".mp4"), "Central format registry should include MP4.");
        Assert(MediaFormatRegistry.IsSupportedMediaPath("sample.MP4"), "Central format registry should handle extension case-insensitively.");
        Assert(!MediaFormatRegistry.IsSupportedMediaPath("sample.svg"), "Unsupported formats should stay out of the central registry.");
    }

    private static void AssertFileAssociationMoveRepair()
    {
        var method = typeof(FileAssociationService).GetMethod(
            "NeedsRegistrationRepair",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (method is null)
        {
            throw new InvalidOperationException("File association move repair check should exist.");
        }

        const string currentExecutablePath = @"C:\Apps\Pixora\Pixora.exe";
        var currentCommand = $"\"{currentExecutablePath}\" \"%1\"";
        var staleCommand = "\"D:\\OldPixora\\Pixora.exe\" \"%1\"";

        var currentNeedsRepair = (bool)method.Invoke(null, [currentCommand, currentExecutablePath])!;
        var staleNeedsRepair = (bool)method.Invoke(null, [staleCommand, currentExecutablePath])!;

        Assert(!currentNeedsRepair, "The current Pixora command should not be rewritten on startup.");
        Assert(staleNeedsRepair, "A moved Pixora executable should refresh its stale command path.");
    }

    private static void AssertMediaCatalogLoaderCancellation(string imageFolder)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = false;
        try
        {
            MediaCatalogLoader.LoadFolderAsync(imageFolder, ImageSortMode.NameNatural, cts.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "Background catalog loading should honor a cancellation request before scanning a directory.");
    }

    private static void AssertVideoMediaSupport(string root)
    {
        var folder = Path.Combine(root, "test-output", "video-catalog");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        var videoPath = Path.Combine(folder, "sample.mp4");
        File.WriteAllBytes(videoPath, []);

        Assert(ImageCatalog.IsSupportedVideoPath(videoPath), "MP4 should be recognized as a supported video path.");
        Assert(ImageCatalog.IsSupportedMediaPath(videoPath), "Supported video paths should be accepted as media paths.");
        Assert(!ImageCatalog.IsSupportedImagePath(videoPath), "Video paths should not be treated as still image paths.");

        var catalog = new ImageCatalog();
        catalog.LoadFromFolder(folder);
        Assert(catalog.Count == 1, $"Expected 1 supported media path, got {catalog.Count}.");
        Assert(Path.GetFileName(catalog.CurrentPath) == "sample.mp4", "Catalog should include supported videos.");

        var document = VideoThumbnailLoader.LoadDocument(videoPath, CancellationToken.None);
        Assert(document.IsVideo, "Video thumbnail documents should be marked as video.");
        Assert(document.PixelWidth > 0 && document.PixelHeight > 0, "Video thumbnail documents should provide a visible bitmap.");
    }

    private static void AssertCatalogSortModes(string root)
    {
        var folder = Path.Combine(root, "test-output", "sort-catalog");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        var one = Path.Combine(folder, "1.jpg");
        var two = Path.Combine(folder, "2.jpg");
        var ten = Path.Combine(folder, "10.jpg");
        File.WriteAllBytes(one, new byte[10]);
        File.WriteAllBytes(two, new byte[30]);
        File.WriteAllBytes(ten, new byte[20]);
        File.SetLastWriteTimeUtc(one, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(two, new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(ten, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var catalog = new ImageCatalog();
        catalog.LoadFromFolder(folder);
        Assert(Path.GetFileName(catalog.Paths[0]) == "1.jpg", "Name sort should place 1.jpg first.");
        Assert(Path.GetFileName(catalog.Paths[1]) == "2.jpg", "Name sort should place 2.jpg before 10.jpg.");
        Assert(Path.GetFileName(catalog.Paths[2]) == "10.jpg", "Name sort should place 10.jpg after 2.jpg.");
        Assert(catalog.SourceFolder == folder, "Folder catalog should remember the source folder.");
        Assert(!catalog.IsSingleFileCatalog, "Full folder catalog should not be marked as a single-file placeholder.");

        catalog.LoadSingleFile(two);
        Assert(catalog.IsSingleFileCatalog, "Single-file catalog should be marked as a startup placeholder.");
        Assert(catalog.Count == 1, $"Single-file catalog should contain only the current file, got {catalog.Count}.");
        Assert(Path.GetFileName(catalog.CurrentPath) == "2.jpg", "Single-file catalog should point at the opened file.");
        Assert(catalog.SourceFolder == folder, "Single-file catalog should remember the source folder for async completion.");

        var completedCatalog = new ImageCatalog();
        completedCatalog.LoadFromFile(two);
        catalog.LoadFromCatalog(completedCatalog);
        Assert(!catalog.IsSingleFileCatalog, "Completed catalog should clear the single-file placeholder flag.");
        Assert(catalog.Count == 3, $"Completed catalog should contain the whole folder, got {catalog.Count}.");
        Assert(Path.GetFileName(catalog.CurrentPath) == "2.jpg", "Completed catalog should preserve the opened file as current.");

        catalog.SortMode = ImageSortMode.NameNaturalDescending;
        catalog.ResortKeepingCurrent();
        Assert(Path.GetFileName(catalog.Paths[0]) == "10.jpg", "Descending name sort should place 10.jpg first.");

        catalog.SortMode = ImageSortMode.LastWriteTimeNewest;
        catalog.ResortKeepingCurrent();
        Assert(Path.GetFileName(catalog.Paths[0]) == "2.jpg", "Newest sort should place the newest file first.");

        catalog.SortMode = ImageSortMode.LastWriteTimeOldest;
        catalog.ResortKeepingCurrent();
        Assert(Path.GetFileName(catalog.Paths[0]) == "1.jpg", "Oldest sort should place the oldest file first.");

        catalog.SortMode = ImageSortMode.FileSizeLargest;
        catalog.ResortKeepingCurrent();
        Assert(Path.GetFileName(catalog.Paths[0]) == "2.jpg", "Largest sort should place the biggest file first.");

        catalog.SortMode = ImageSortMode.FileSizeSmallest;
        catalog.ResortKeepingCurrent();
        Assert(Path.GetFileName(catalog.Paths[0]) == "1.jpg", "Smallest sort should place the smallest file first.");

        Assert(catalog.MoveTo(ten), "Catalog should move to 10.jpg before explicit list reload.");
        catalog.LoadFromPaths([one, two, ten], ten);
        Assert(catalog.SourceFolder is null, "Explicit path lists should not report a source folder.");
        Assert(Path.GetFileName(catalog.CurrentPath) == "10.jpg", "Explicit path list should keep the preferred current file.");

        catalog.LoadFromPaths([one, two], one);
        var neighbors = catalog.GetNeighborPaths(radius: 3);
        Assert(neighbors.Count == 1, $"Two-item catalogs should expose exactly one neighbor, got {neighbors.Count}.");
        Assert(Path.GetFileName(neighbors[0]) == "2.jpg", "Two-item catalog neighbor should be the other file.");
        Assert(catalog.MoveTo(two), "Catalog should move to 2.jpg.");
        neighbors = catalog.GetNeighborPaths(radius: 3);
        Assert(neighbors.Count == 1, $"Two-item catalogs should still expose exactly one neighbor after moving, got {neighbors.Count}.");
        Assert(Path.GetFileName(neighbors[0]) == "1.jpg", "Two-item catalog neighbor should wrap to the other file.");

        var indexField = typeof(ImageCatalog).GetField(
            "<Index>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert(indexField is not null, "Catalog index backing field should exist for stale-index regression coverage.");
        indexField!.SetValue(catalog, catalog.Count);
        Assert(catalog.GetNeighborPaths(radius: 3).Count == 0, "Catalog should not throw when its current index is stale.");
    }

    private static void AssertFavoriteStore(string root)
    {
        var storePath = Path.Combine(root, "test-output", "favorites-smoke.json");
        if (File.Exists(storePath))
        {
            File.Delete(storePath);
        }

        var backupPath = AtomicJsonFile.GetBackupPath(storePath);
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        var firstImage = Path.Combine(root, "test-images", "1.png");
        var missingImage = Path.Combine(root, "test-output", "missing-favorite.png");
        var store = FavoriteStore.Load(storePath);
        Assert(store.Count == 0, "New favorite store should start empty.");
        Assert(store.Add(firstImage), "Favorite store should add an image.");
        Assert(!store.Add(firstImage), "Favorite store should ignore duplicate paths.");
        Assert(store.Add(missingImage), "Favorite store can persist paths before pruning.");
        store.Save(storePath);

        var loaded = FavoriteStore.Load(storePath);
        Assert(loaded.IsFavorite(firstImage), "Favorite store should persist favorite paths.");
        Assert(loaded.RemoveMissingOrUnsupported(), "Favorite store should prune missing files.");
        Assert(loaded.GetExistingMediaPaths().Count == 1, "Favorite store should return existing media paths.");
        Assert(loaded.Remove(firstImage), "Favorite store should remove existing favorites.");
        loaded.Save(storePath);

        var empty = FavoriteStore.Load(storePath);
        Assert(empty.Count == 0, "Favorite store should persist removals.");
    }

    private static void AssertMediaFolderStress(string folder, int imageSampleCount, int videoSampleCount)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Media folder does not exist: {folder}");
        }

        var catalog = new ImageCatalog();
        var stopwatch = Stopwatch.StartNew();
        catalog.LoadFromFolder(folder);
        stopwatch.Stop();

        var expectedCount = Directory.EnumerateFiles(folder)
            .Count(ImageCatalog.IsSupportedMediaPath);
        Assert(catalog.Count == expectedCount, $"Media folder catalog count mismatch: {catalog.Count}/{expectedCount}.");
        Assert(catalog.Count > 0, "Media folder should contain at least one supported image or video.");
        Console.WriteLine($"Media folder catalog passed: {catalog.Count} items in {stopwatch.Elapsed.TotalSeconds:0.00}s");

        var imagePaths = catalog.Paths
            .Where(ImageCatalog.IsSupportedStillImagePath)
            .Take(Math.Max(0, imageSampleCount))
            .ToList();
        var loadedImages = 0;
        foreach (var path in imagePaths)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var document = ImageLoader.Load(path, cts.Token);
                Assert(document.PixelWidth > 0 && document.PixelHeight > 0, $"External image dimensions should be valid: {path}");
                loadedImages++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"External image failed: {Path.GetFileName(path)} => {ex.Message}");
            }
        }

        if (imageSampleCount > 0 && imagePaths.Count > 0)
        {
            Assert(loadedImages > 0, "At least one external image sample should load.");
        }

        Console.WriteLine($"Media folder image samples: {loadedImages}/{imagePaths.Count} loaded");

        var videoPaths = catalog.Paths
            .Where(ImageCatalog.IsSupportedVideoPath)
            .Take(Math.Max(0, videoSampleCount))
            .ToList();
        var loadedVideos = 0;
        foreach (var path in videoPaths)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var thumbnail = VideoThumbnailLoader.LoadThumbnail(path, 256, cts.Token);
                Assert(thumbnail.PixelWidth > 0 && thumbnail.PixelHeight > 0, $"Video thumbnail dimensions should be valid: {path}");
                loadedVideos++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"External video thumbnail failed: {Path.GetFileName(path)} => {ex.Message}");
            }
        }

        if (videoSampleCount > 0 && videoPaths.Count > 0)
        {
            Assert(loadedVideos > 0, "At least one external video thumbnail sample should load.");
        }

        Console.WriteLine($"Media folder video samples: {loadedVideos}/{videoPaths.Count} thumbnails loaded");
    }

    private static void AssertShortcutSettings()
    {
        var settings = ShortcutSettings.CreateDefault();
        Assert(settings.Matches(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.A, ModifierKeys.None)), "A should go to the previous image.");
        Assert(settings.Matches(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.OemQuestion, ModifierKeys.None)), "/ should go to the previous image.");
        Assert(settings.Matches(ShortcutAction.NextImage, new KeyboardShortcut(Key.D, ModifierKeys.None)), "D should go to the next image.");
        Assert(settings.Matches(ShortcutAction.CopyFile, new KeyboardShortcut(Key.C, ModifierKeys.Control)), "Ctrl+C should copy the current file.");
        Assert(settings.Matches(ShortcutAction.CopyPath, new KeyboardShortcut(Key.C, ModifierKeys.Control | ModifierKeys.Shift)), "Ctrl+Shift+C should copy the current path.");
        Assert(settings.Matches(ShortcutAction.ToggleFavorite, new KeyboardShortcut(Key.F, ModifierKeys.Control)), "Ctrl+F should toggle favorite.");
        Assert(settings.Matches(ShortcutAction.ToggleFavoritesView, new KeyboardShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift)), "Ctrl+Shift+F should toggle favorites view.");
        Assert(settings.Matches(ShortcutAction.RotateLeft, new KeyboardShortcut(Key.L, ModifierKeys.Control)), "Ctrl+L should rotate left.");
        Assert(settings.Matches(ShortcutAction.RotateRight, new KeyboardShortcut(Key.R, ModifierKeys.Control)), "Ctrl+R should rotate right.");
        Assert(settings.Matches(ShortcutAction.ToggleAnimationPlayback, new KeyboardShortcut(Key.P, ModifierKeys.None)), "P should pause or resume animated images.");
        Assert(settings.Matches(ShortcutAction.RestartAnimation, new KeyboardShortcut(Key.P, ModifierKeys.Shift)), "Shift+P should restart animated images.");
        Assert(settings.Matches(ShortcutAction.CycleSortMode, new KeyboardShortcut(Key.S, ModifierKeys.None)), "S should cycle sort mode.");
        Assert(settings.Matches(ShortcutAction.OpenBatchCompressTools, new KeyboardShortcut(Key.M, ModifierKeys.Control | ModifierKeys.Shift)), "Ctrl+Shift+M should open batch compression tools.");
        Assert(settings.GetShortcuts(ShortcutAction.BatchDeleteCurrentFolder).Count == 0, "Batch delete should not have a default shortcut.");
        Assert(settings.Matches(ShortcutAction.CropImage, new KeyboardShortcut(Key.C, ModifierKeys.None)), "C should start image cropping.");
        Assert(settings.Matches(ShortcutAction.CompressImage, new KeyboardShortcut(Key.M, ModifierKeys.Control)), "Ctrl+M should open image compression.");
        Assert(settings.Matches(ShortcutAction.ToggleFullScreen, new KeyboardShortcut(Key.Enter, ModifierKeys.None)), "Enter should toggle full screen.");
        Assert(settings.Matches(ShortcutAction.ToggleFullScreen, new KeyboardShortcut(Key.F11, ModifierKeys.None)), "F11 should toggle full screen.");
        Assert(settings.Matches(ShortcutAction.SaveCrop, new KeyboardShortcut(Key.Enter, ModifierKeys.None)), "Enter should save crop while cropping.");
        Assert(settings.Matches(ShortcutAction.ToggleThumbnailSidebar, new KeyboardShortcut(Key.B, ModifierKeys.Control)), "Ctrl+B should toggle thumbnail sidebar.");
        Assert(settings.Matches(ShortcutAction.ToggleThumbnailColumns, new KeyboardShortcut(Key.B, ModifierKeys.Control | ModifierKeys.Shift)), "Ctrl+Shift+B should toggle thumbnail columns.");
        Assert(settings.Matches(ShortcutAction.ShowQuickSearch, new KeyboardShortcut(Key.K, ModifierKeys.Control)), "Ctrl+K should open quick search.");
        Assert(settings.FindConflict(ShortcutAction.ToggleFullScreen, new KeyboardShortcut(Key.Enter, ModifierKeys.None)) is null, "Viewer and crop shortcuts should be allowed to share Enter.");

        var custom = settings.Clone();
        custom.SetSingleShortcut(ShortcutAction.NextImage, new KeyboardShortcut(Key.N, ModifierKeys.Control));
        Assert(custom.Matches(ShortcutAction.NextImage, new KeyboardShortcut(Key.N, ModifierKeys.Control)), "Custom shortcut should be applied.");
        Assert(!custom.Matches(ShortcutAction.NextImage, new KeyboardShortcut(Key.D, ModifierKeys.None)), "Replacing a shortcut should remove old bindings for that action.");

        custom = settings.Clone();
        Assert(custom.RemoveShortcut(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.A, ModifierKeys.None)), "Existing shortcut should be removable.");
        Assert(!custom.Matches(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.A, ModifierKeys.None)), "Removed shortcut should no longer match.");
        Assert(custom.Matches(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.Left, ModifierKeys.None)), "Removing one shortcut should keep other bindings for the action.");

        custom.AddShortcut(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.P, ModifierKeys.Control));
        Assert(custom.Matches(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.P, ModifierKeys.Control)), "Added shortcut should match.");

        custom.ReplaceShortcut(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.P, ModifierKeys.Control), new KeyboardShortcut(Key.B, ModifierKeys.Control));
        Assert(!custom.Matches(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.P, ModifierKeys.Control)), "Replaced shortcut should no longer match.");
        Assert(custom.Matches(ShortcutAction.PreviousImage, new KeyboardShortcut(Key.B, ModifierKeys.Control)), "Replacement shortcut should match.");

        custom.ClearAll();
        Assert(ShortcutSettings.ActionInfos.All(info => custom.GetShortcuts(info.Action).Count == 0), "ClearAll should remove every shortcut binding.");
    }

    private static void AssertQuickSearchMatcher()
    {
        Assert(QuickSearchMatcher.TryResolveOneBasedIndex("2", 3, out var index) && index == 1, "Quick search should resolve one-based positions.");
        Assert(!QuickSearchMatcher.TryResolveOneBasedIndex("0", 3, out _), "Quick search should reject positions below the catalog range.");
        Assert(!QuickSearchMatcher.TryResolveOneBasedIndex("4", 3, out _), "Quick search should reject positions above the catalog range.");

        var paths = new[]
        {
            Path.Combine("sample", "春天-001.jpg"),
            Path.Combine("sample", "Summer-002.PNG"),
        };
        Assert(QuickSearchMatcher.FindFileNameMatch(paths, "春天") == 0, "Quick search should match Chinese file names.");
        Assert(QuickSearchMatcher.FindFileNameMatch(paths, "summer") == 1, "Quick search should match file names without case sensitivity.");
        Assert(QuickSearchMatcher.FindFileNameMatch(paths, "missing") == -1, "Quick search should report missing file-name matches.");
        Assert(QuickSearchMatcher.MatchesFileName(paths[1], "002"), "Quick search should expose reusable file-name matching for live thumbnail filtering.");
        Assert(
            QuickSearchMatcher.CompactFileName("abcdefghijklmno123456789.png", 8, 8) == "abcdefgh…6789.png",
            "Quick search should preserve both the beginning and ending of long file names.");
    }

    private static void AssertQuickSearchInteractionState()
    {
        var state = new QuickSearchInteractionState();
        Assert(!state.IsTextEntryActive, "Quick search should start in viewer-shortcut mode.");
        Assert(!state.ShouldTextBoxHandleKey(Key.I, ModifierKeys.None), "Viewer-shortcut mode should not consume a configured letter as text.");
        Assert(state.ShouldSuppressUnboundKey(Key.Q, ModifierKeys.None), "Viewer-shortcut mode should suppress an unbound letter instead of typing it into quick search.");
        Assert(!state.ShouldSuppressUnboundKey(Key.F11, ModifierKeys.None), "Viewer-shortcut mode should leave non-text function keys available.");

        state.SetTextEntryActive(true);
        Assert(state.IsTextEntryActive, "Clicking quick search should activate text entry.");
        Assert(state.ShouldTextBoxHandleKey(Key.I, ModifierKeys.None), "Text-entry mode should accept letters that also have viewer shortcuts.");
        Assert(!state.ShouldSuppressUnboundKey(Key.Q, ModifierKeys.None), "Text-entry mode should not suppress letters intended for quick search.");
        Assert(state.ShouldTextBoxHandleKey(Key.C, ModifierKeys.Control), "Text-entry mode should preserve standard text-copy shortcuts.");
        Assert(!state.ShouldTextBoxHandleKey(Key.F11, ModifierKeys.None), "Text-entry mode should leave non-text function keys available to the viewer.");

        state.SetTextEntryActive(false);
        Assert(!state.ShouldTextBoxHandleKey(Key.I, ModifierKeys.None), "Leaving quick-search input should restore viewer-shortcut routing.");
    }

    private static void AssertQuickSearchIndexPerformance()
    {
        const int pathCount = 150_000;
        var paths = Enumerable.Range(0, pathCount)
            .Select(index => Path.Combine("sample", $"image_{index:D6}_sample.jpg"))
            .ToArray();
        var index = new QuickSearchIndex();
        index.Reset(paths);
        Assert(index.InitializedEntryCount == 0, "Quick-search index should defer file-name extraction until a name search runs.");

        var stopwatch = Stopwatch.StartNew();
        var broad = index.Search("image_149");
        var refined = index.Search("image_149999", broad);
        stopwatch.Stop();

        Assert(index.Count == pathCount, "Quick-search index should retain every catalog entry.");
        Assert(index.InitializedEntryCount == pathCount, "A broad search should initialize entries only when they are actually inspected.");
        Assert(broad.MatchingIndices.Count == 1000, "Broad indexed search should find the expected candidate range.");
        Assert(refined.FirstIndex == 149_999 && refined.MatchingIndices.Count == 1, "Refined indexed search should reuse candidates and find the final item.");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Indexed search over {pathCount:N0} paths should finish promptly; elapsed {stopwatch.Elapsed.TotalMilliseconds:N0} ms.");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = false;
        try
        {
            index.Search("sample", cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert(canceled, "Quick-search index should honor cancellation.");
    }

    private static void AssertLargeCatalogPerformance()
    {
        const int itemCount = 150_000;
        var createdItems = 0;
        var items = new LazyIndexedList<object>(
            itemCount,
            _ =>
            {
                createdItems++;
                return new object();
            });
        var rows = new VirtualizedRowCollection<object>(items, columns: 2);

        var stopwatch = Stopwatch.StartNew();
        Assert(rows.Count == itemCount / 2, "Virtualized thumbnail rows should expose the complete large catalog count.");
        _ = rows[0];
        _ = rows[rows.Count / 2];
        _ = rows[rows.Count - 1];
        stopwatch.Stop();

        Assert(rows.CreatedRowCount == 3, "Large catalogs should create only rows actually requested by the UI.");
        Assert(items.CreatedCount == 6 && createdItems == 6, "Two-column thumbnail rows should create only the six visible sample items.");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Virtualized access over {itemCount:N0} items should be immediate; elapsed {stopwatch.Elapsed.TotalMilliseconds:N0} ms.");

        var boundedItems = new LazyIndexedList<object>(itemCount, static _ => new object(), maxCachedItems: 128);
        var boundedRows = new VirtualizedRowCollection<object>(boundedItems, columns: 2, maxCachedRows: 64);
        for (var rowIndex = 0; rowIndex < boundedRows.Count; rowIndex++)
        {
            _ = boundedRows[rowIndex];
        }

        Assert(boundedRows.CreatedRowCount <= 64, "Scrolling an entire large catalog should keep only a bounded number of row models.");
        Assert(boundedItems.CreatedCount <= 128, "Scrolling an entire large catalog should keep only a bounded number of thumbnail item models.");

        var buffer = new ProgressUpdateBuffer<int>();
        stopwatch.Restart();
        for (var index = 0; index < 100_000; index++)
        {
            buffer.Report(index);
        }

        var updates = buffer.Drain();
        stopwatch.Stop();
        Assert(updates.Count == 100_000 && updates[^1] == 99_999, "Coalesced progress buffering should retain ordered background updates.");
        Assert(buffer.PendingCount == 0, "Draining progress updates should clear the pending queue.");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Progress buffering should handle 100,000 reports promptly; elapsed {stopwatch.Elapsed.TotalMilliseconds:N0} ms.");
    }

    private static void AssertViewerSettings(string root)
    {
        Assert(!new ViewerSettings().HideQuickSearchAfterJump, "Quick search should remain visible after a successful jump by default.");
        Assert(new ViewerSettings().ShowZoomIndicator, "Zoom percentage indicator should be enabled by default.");
        Assert(new ViewerSettings().ZoomIndicatorDisplayMode == ZoomIndicatorDisplayMode.Percentage, "Zoom indicator should default to percentage mode.");
        Assert(
            new ViewerSettings().MainImageCacheMegabytes == 768
            && new ViewerSettings().DisplayPreviewCacheMegabytes == 192
            && ViewerSettings.AutomaticMainImageCacheCapMegabytes == 8192
            && ViewerSettings.AutomaticDisplayPreviewCacheCapMegabytes == 2048,
            "Automatic cache ceilings should be independent from conservative manual defaults.");

        var outputFolder = Path.Combine(root, "test-output");
        Directory.CreateDirectory(outputFolder);
        var settingsPath = Path.Combine(outputFolder, "viewer-settings-smoke.json");

        var settings = new ViewerSettings
        {
            ShowThumbnailSidebar = false,
            UseDoubleThumbnailColumns = false,
            QuickSearchMode = QuickSearchMode.FileName,
            ShowQuickSearchOnStartup = true,
            HideQuickSearchAfterJump = true,
            QuickSearchOffsetX = 128.5,
            QuickSearchOffsetY = 96.25,
            SavedFileOpenBehavior = SavedFileOpenBehavior.NewWindow,
            ConfirmDeleteToRecycleBin = false,
            SortMode = ImageSortMode.FileSizeLargest,
            LastOpenedFolder = outputFolder,
            OpenLastFolderOnStartup = true,
            RememberMainWindowPlacement = true,
            StartMainWindowMaximized = true,
            ReuseExistingWindow = false,
            KeepViewStateWhenNavigating = true,
            WatchFolderChanges = false,
            MainWindowWidth = 1234,
            MainWindowHeight = 777,
            MainWindowLeft = 33,
            MainWindowTop = 44,
            MainWindowMaximized = true,
            ShowAnimationControls = false,
            ShowOperationNotifications = false,
            ShowZoomIndicator = false,
            ZoomIndicatorDisplayMode = ZoomIndicatorDisplayMode.Multiplier,
            ThumbnailDiskCacheMegabytes = 1024,
            IncludePrivatePathsInDiagnostics = true,
        };

        settings.Save(settingsPath);
        var loaded = ViewerSettings.Load(settingsPath);
        Assert(!loaded.ShowThumbnailSidebar, "Viewer settings should persist hidden thumbnail sidebar state.");
        Assert(!loaded.UseDoubleThumbnailColumns, "Viewer settings should persist thumbnail column preference.");
        Assert(loaded.QuickSearchMode == QuickSearchMode.FileName, "Viewer settings should persist quick-search mode.");
        Assert(loaded.ShowQuickSearchOnStartup, "Viewer settings should persist startup quick-search visibility.");
        Assert(loaded.HideQuickSearchAfterJump, "Viewer settings should persist whether quick search closes after a successful jump.");
        Assert(loaded.QuickSearchOffsetX == 128.5 && loaded.QuickSearchOffsetY == 96.25, "Viewer settings should persist the draggable quick-search position.");
        Assert(loaded.SavedFileOpenBehavior == SavedFileOpenBehavior.NewWindow, "Viewer settings should persist saved file open behavior.");
        Assert(!loaded.ConfirmDeleteToRecycleBin, "Viewer settings should persist delete confirmation preference.");
        Assert(loaded.SortMode == ImageSortMode.FileSizeLargest, "Viewer settings should persist image sort mode.");
        Assert(string.Equals(loaded.LastOpenedFolder, outputFolder, StringComparison.OrdinalIgnoreCase), "Viewer settings should persist the last opened folder.");
        Assert(loaded.OpenLastFolderOnStartup, "Viewer settings should persist last-folder startup behavior.");
        Assert(loaded.RememberMainWindowPlacement, "Viewer settings should persist main-window placement behavior.");
        Assert(loaded.StartMainWindowMaximized, "Viewer settings should persist forced maximized startup behavior.");
        Assert(!loaded.ReuseExistingWindow, "Viewer settings should persist single-instance behavior.");
        Assert(loaded.KeepViewStateWhenNavigating, "Viewer settings should persist navigation view behavior.");
        Assert(!loaded.WatchFolderChanges, "Viewer settings should persist folder watching behavior.");
        Assert(loaded.MainWindowWidth == 1234 && loaded.MainWindowHeight == 777, "Viewer settings should persist main-window dimensions.");
        Assert(loaded.MainWindowLeft == 33 && loaded.MainWindowTop == 44 && loaded.MainWindowMaximized, "Viewer settings should persist main-window placement and state.");

        Assert(!loaded.ShowAnimationControls, "Viewer settings should persist animation control visibility.");
        Assert(!loaded.ShowOperationNotifications, "Viewer settings should persist operation notification visibility.");
        Assert(!loaded.ShowZoomIndicator, "Viewer settings should persist zoom indicator visibility.");
        Assert(loaded.ZoomIndicatorDisplayMode == ZoomIndicatorDisplayMode.Multiplier, "Viewer settings should persist the zoom indicator display mode.");
        Assert(loaded.ThumbnailDiskCacheMegabytes == 1024, "Viewer settings should persist thumbnail disk cache capacity.");
        Assert(loaded.IncludePrivatePathsInDiagnostics, "Viewer settings should persist diagnostic privacy preference.");
        Assert(typeof(ViewerSettings).GetProperty("ShowDirectoryStats") is null, "Viewer settings should not retain the removed directory statistics option.");
        var settingsXaml = File.ReadAllText(Path.Combine(root, "src", "Pixora", "ShortcutSettingsWindow.xaml"));
        Assert(settingsXaml.Contains("x:Name=\"ShowZoomIndicatorCheckBox\"", StringComparison.Ordinal), "Interface settings should expose the zoom percentage preference.");
        Assert(settingsXaml.Contains("x:Name=\"ZoomIndicatorDisplayModeComboBox\"", StringComparison.Ordinal), "Interface settings should let users choose percentage or multiplier zoom text.");
        Assert(settingsXaml.Contains("适应窗口”的默认大小为 1.00×", StringComparison.Ordinal), "Interface settings should explain that multiplier mode is relative to the fitted view.");
        Assert(FileAssociationService.SupportedExtensions.Contains(".gif"), "File association extensions should include GIF.");
        Assert(FileAssociationService.SupportedExtensions.All(extension => ImageCatalog.IsSupportedStillImagePath("sample" + extension)), "File association extensions should be supported image extensions.");
    }

    private static void AssertAtomicJsonPersistence(string root)
    {
        var folder = Path.Combine(root, "test-output", "atomic-json");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        var settingsPath = Path.Combine(folder, "viewer-settings.json");
        new ViewerSettings
        {
            ShowQuickSearchOnStartup = false,
            QuickSearchOffsetX = 12,
        }.Save(settingsPath);
        new ViewerSettings
        {
            ShowQuickSearchOnStartup = true,
            QuickSearchOffsetX = 34,
        }.Save(settingsPath);

        Assert(File.Exists(settingsPath), "Atomic JSON save should keep the main settings file.");
        Assert(File.Exists(AtomicJsonFile.GetBackupPath(settingsPath)), "Replacing settings atomically should retain the previous valid file as a backup.");
        var current = ViewerSettings.Load(settingsPath);
        Assert(current.ShowQuickSearchOnStartup && current.QuickSearchOffsetX == 34, "Atomic JSON load should prefer the current valid file.");

        File.WriteAllText(settingsPath, "{ broken json");
        var recovered = ViewerSettings.Load(settingsPath);
        Assert(!recovered.ShowQuickSearchOnStartup && recovered.QuickSearchOffsetX == 12, "Corrupt settings should recover from the previous atomic backup.");
        Assert(!Directory.EnumerateFiles(folder, "*.tmp", SearchOption.TopDirectoryOnly).Any(), "Atomic JSON save should not leave temporary files behind.");

        foreach (var fileName in new[]
        {
            "ViewerSettings.cs",
            "ShortcutSettings.cs",
            "FavoriteStore.cs",
            "BatchCompressionSettings.cs",
        })
        {
            var source = File.ReadAllText(Path.Combine(root, "src", "Pixora", "Services", fileName));
            Assert(source.Contains("AtomicJsonFile.", StringComparison.Ordinal), $"{fileName} should use the shared atomic JSON persistence service.");
        }
    }

    private static void AssertMemoryCacheCoordinator()
    {
        var imageCache = ImageCache.FromMegabytes(1);
        var previewCache = BitmapSourceMemoryCache.FromMegabytes(1);
        var coordinator = new MemoryCacheCoordinator(imageCache, previewCache);

        coordinator.ApplyConfiguredBudgets(64, 32);
        var snapshot = coordinator.CaptureSnapshot(isProtectionEnabled: false);
        Assert(snapshot.MainImageBudgetBytes == 64L * 1024 * 1024, "Memory coordinator should apply the main image cache budget.");
        Assert(snapshot.PreviewBudgetBytes == 32L * 1024 * 1024, "Memory coordinator should apply the preview cache budget.");
        Assert(MemoryCacheCoordinator.IsUnderPressure(85, 100), "Memory coordinator should detect the early 85% system-memory pressure threshold.");
        Assert(!MemoryCacheCoordinator.IsUnderPressure(84, 100), "Memory coordinator should not trim below the pressure threshold.");
        Assert(MemoryCacheCoordinator.IsUnderPressure(0, 0, 600L * 1024 * 1024, 2L * 1024 * 1024 * 1024), "Memory coordinator should detect excessive process working-set pressure.");
        var lowMemoryBudgets = MemoryCacheCoordinator.ResolveBudgets(768, 192, true, 4L * 1024 * 1024 * 1024);
        Assert(lowMemoryBudgets == new MemoryCacheBudgets(256, 64), "Automatic cache sizing should reduce budgets on 4 GB systems.");
        var configuredBudgets = MemoryCacheCoordinator.ResolveBudgets(768, 192, false, 4L * 1024 * 1024 * 1024);
        Assert(configuredBudgets == new MemoryCacheBudgets(768, 192), "Manual cache sizing should keep configured budgets.");
        Assert(
            MemoryCacheCoordinator.ResolveBudgets(8192, 2048, true, 16L * 1024 * 1024 * 1024) == new MemoryCacheBudgets(2048, 512),
            "Automatic cache sizing should use the balanced 16 GB budget.");
        Assert(
            MemoryCacheCoordinator.ResolveBudgets(8192, 2048, true, 32L * 1024 * 1024 * 1024) == new MemoryCacheBudgets(4096, 1024),
            "Automatic cache sizing should use the high-performance 32 GB budget.");
        Assert(
            MemoryCacheCoordinator.ResolveBudgets(8192, 2048, true, 64L * 1024 * 1024 * 1024) == new MemoryCacheBudgets(8192, 2048),
            "Automatic cache sizing should use the maximum 64 GB budget.");

        var highEndProfile = MemoryCacheCoordinator.ResolvePerformanceProfile(
            8192,
            2048,
            true,
            64L * 1024 * 1024 * 1024,
            processorCount: 16);
        Assert(
            highEndProfile.MainPreloadForwardRadius == 8
            && highEndProfile.MainPreloadOppositeRadius == 2
            && highEndProfile.ThumbnailLoadConcurrency == 6,
            "A 64 GB / 16-thread system should use the high-end preload profile.");
        var cpuLimitedProfile = MemoryCacheCoordinator.ResolvePerformanceProfile(
            8192,
            2048,
            true,
            64L * 1024 * 1024 * 1024,
            processorCount: 4);
        Assert(cpuLimitedProfile.ThumbnailLoadConcurrency == 2, "Thumbnail concurrency should remain limited on low-core-count CPUs.");
    }

    private static void AssertSettingsWindowInitializes()
    {
        var viewerSettings = new ViewerSettings
        {
            ThumbnailDiskCacheMegabytes = 1024,
            QuickSearchMode = QuickSearchMode.FileName,
            ShowQuickSearchOnStartup = true,
            HideQuickSearchAfterJump = true,
        };
        var window = new ShortcutSettingsWindow(ShortcutSettings.Load(), viewerSettings);
        var comboBox = window.FindName("ThumbnailDiskCacheSizeComboBox") as ComboBox;
        var cachePathText = window.FindName("ThumbnailDiskCachePathText") as TextBlock;
        var generalPage = window.FindName("GeneralPage") as ScrollViewer;
        var quickSearchMode = window.FindName("QuickSearchModeComboBox") as ComboBox;
        var showQuickSearchOnStartup = window.FindName("ShowQuickSearchOnStartupCheckBox") as CheckBox;
        var hideQuickSearchAfterJump = window.FindName("HideQuickSearchAfterJumpCheckBox") as CheckBox;
        var settingsSearch = window.FindName("SettingsSearchTextBox") as TextBox;
        var interfaceSection = window.FindName("InterfaceSettingsSection") as FrameworkElement;
        var performanceSection = window.FindName("PerformanceSettingsSection") as FrameworkElement;
        var cacheSizingMode = window.FindName("CacheSizingModeComboBox") as ComboBox;
        var automaticCacheSummary = window.FindName("AutomaticCacheSummaryText") as TextBlock;
        var automaticCacheSummaryPanel = window.FindName("AutomaticCacheSummaryPanel") as FrameworkElement;
        var manualCacheSettings = window.FindName("ManualCacheSettingsPanel") as FrameworkElement;
        var mainImageCache = window.FindName("MainImageCacheComboBox") as ComboBox;
        var displayPreviewCache = window.FindName("DisplayPreviewCacheComboBox") as ComboBox;

        Assert(comboBox is not null, "Settings window should initialize the thumbnail disk cache capacity selector.");
        Assert(comboBox!.SelectedValue?.ToString() == "1024", "Settings window should select the persisted thumbnail disk cache capacity.");
        Assert(cachePathText?.Text.Contains("当前占用", StringComparison.Ordinal) == true, "Settings window should show thumbnail disk cache usage.");
        Assert(generalPage?.ClipToBounds == true, "Settings page should clip scrolling content inside its viewport.");
        Assert(quickSearchMode?.SelectedValue?.ToString() == "FileName", "Settings window should select the persisted quick-search mode.");
        Assert(showQuickSearchOnStartup?.IsChecked == true, "Settings window should select persisted startup quick-search visibility.");
        Assert(hideQuickSearchAfterJump?.IsChecked == true, "Settings window should select the persisted post-jump quick-search behavior.");
        Assert(settingsSearch is not null, "Settings window should expose an always-visible search input.");
        Assert(cacheSizingMode?.SelectedValue?.ToString() == "Automatic", "Settings should present automatic cache mode as the default explicit choice.");
        Assert(automaticCacheSummary?.Text.Contains("当前预算", StringComparison.Ordinal) == true, "Automatic cache mode should show the effective runtime budget.");
        Assert(automaticCacheSummaryPanel?.Visibility == Visibility.Visible, "Automatic cache mode should show its hardware summary panel.");
        Assert(manualCacheSettings?.IsEnabled == false, "Automatic cache mode should disable unrelated manual capacity selectors.");
        Assert(mainImageCache?.Items.Cast<ComboBoxItem>().Any(item => item.Tag?.ToString() == "8192") == true, "Settings should expose an 8 GB main-image cache cap for high-end systems.");
        Assert(displayPreviewCache?.Items.Cast<ComboBoxItem>().Any(item => item.Tag?.ToString() == "2048") == true, "Settings should expose a 2 GB preview cache cap for high-end systems.");
        cacheSizingMode!.SelectedValue = "Manual";
        Assert(manualCacheSettings!.IsEnabled, "Switching to manual cache mode should immediately enable capacity selectors.");
        Assert(automaticCacheSummaryPanel!.Visibility == Visibility.Collapsed, "Manual cache mode should hide the automatic hardware summary.");
        settingsSearch!.Text = "缓存";
        Assert(performanceSection?.Visibility == Visibility.Visible, "Settings search should keep matching performance and cache settings visible.");
        Assert(interfaceSection?.Visibility == Visibility.Collapsed, "Settings search should hide unrelated interface settings.");
        settingsSearch.Clear();
        Assert(interfaceSection?.Visibility == Visibility.Visible, "Clearing settings search should restore all settings sections.");
        Assert(TextOptions.GetTextRenderingMode(generalPage) == TextRenderingMode.Grayscale, "Settings page should use stable grayscale text rendering while scrolling.");
        Assert(TextOptions.GetTextHintingMode(generalPage) == TextHintingMode.Animated, "Settings page should use animated text hinting while scrolling.");
    }

    private static void AssertMainWindowInputMethodDisabled(string root)
    {
        var xamlPath = Path.Combine(root, "src", "Pixora", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);
        Assert(
            xaml.Contains("InputMethod.IsInputMethodEnabled=\"False\"", StringComparison.Ordinal),
            "Main window should disable IME composition so letter shortcuts are not treated as Chinese text input.");
    }

    private static void AssertMainWindowExperienceControls(string root)
    {
        var xamlPath = Path.Combine(root, "src", "Pixora", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);
        var codePath = Path.Combine(root, "src", "Pixora", "MainWindow.xaml.cs");
        var code = File.ReadAllText(codePath);
        Assert(xaml.Contains("x:Name=\"CancelFolderLoadButton\"", StringComparison.Ordinal), "Main window should expose scan cancellation.");
        Assert(xaml.Contains("x:Name=\"CatalogScanPanel\"", StringComparison.Ordinal), "Main window should expose background catalog progress.");
        Assert(xaml.Contains("x:Name=\"QuickSearchOverlay\"", StringComparison.Ordinal), "Main window should expose the hidden quick-search overlay.");
        Assert(xaml.Contains("x:Name=\"ZoomIndicator\"", StringComparison.Ordinal), "Main window should expose a transient zoom indicator.");
        Assert(xaml.Contains("x:Name=\"ZoomIndicatorText\"", StringComparison.Ordinal), "Zoom indicator should expose its current scale text.");
        Assert(xaml.Contains("Text=\"100%\"", StringComparison.Ordinal), "Zoom indicator should start with a percentage value.");
        Assert(code.Contains("ZoomIndicatorTimer_Tick", StringComparison.Ordinal), "Zoom indicator should fade after zooming stops.");
        Assert(code.Contains("FormatZoomScale", StringComparison.Ordinal), "Zoom indicator should format the current scale as a percentage.");
        Assert(code.Contains("打开用时", StringComparison.Ordinal), "Main window should distinguish real image load time from directory indexing time.");
        Assert(!xaml.Contains("x:Name=\"DirectoryStatsPanel\"", StringComparison.Ordinal), "Thumbnail sidebar should not contain the removed directory statistics panel.");
        Assert(!code.Contains("StartDirectoryStatsUpdate", StringComparison.Ordinal), "Main window should not run the removed directory statistics background task.");
        Assert(xaml.Contains("x:Name=\"QuickSearchTextBox\"", StringComparison.Ordinal), "Quick search should expose a dedicated text input.");
        Assert(xaml.Contains("PreviewMouseLeftButtonDown=\"QuickSearchTextBox_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal), "Clicking the quick-search input should explicitly enter text-entry mode.");
        Assert(xaml.Contains("PreviewMouseDown=\"Window_PreviewMouseDown\"", StringComparison.Ordinal), "Clicking outside quick-search input should explicitly restore viewer shortcut routing.");
        Assert(xaml.Contains("Margin=\"4,0,0,0\"", StringComparison.Ordinal), "Quick-search input should keep comfortable spacing from the Pixora icon.");
        Assert(!xaml.Contains("x:Name=\"QuickSearchPlaceholderText\"", StringComparison.Ordinal), "The persistent mode label should replace redundant quick-search placeholder text.");
        Assert(xaml.Contains("x:Name=\"PART_ContentHost\"", StringComparison.Ordinal), "Quick search should use a transparent dedicated text-box template instead of the global dark input chrome.");
        Assert(xaml.Contains("Assets/PixoraIcon.png", StringComparison.Ordinal), "Quick search should use the Pixora icon instead of a text mode marker.");
        Assert(xaml.Contains("<EventTrigger RoutedEvent=\"MouseEnter\">", StringComparison.Ordinal), "Quick search should darken smoothly when hovered.");
        Assert(xaml.Contains("x:Name=\"QuickSearchBackdropBrush\"", StringComparison.Ordinal), "Quick search should sample the live viewer surface for its backdrop.");
        Assert(xaml.Contains("x:Name=\"QuickSearchPositionTransform\"", StringComparison.Ordinal), "Quick search should expose an independent transform for its persisted drag position.");
        Assert(xaml.Contains("PreviewMouseLeftButtonDown=\"QuickSearchGlass_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal), "Quick search should support Ctrl-drag from the glass bar.");
        Assert(xaml.Contains("<BlurEffect Radius=\"18\"", StringComparison.Ordinal), "Quick search should blur its sampled backdrop instead of using a flat gray fill.");
        Assert(xaml.Contains("<BitmapCache RenderAtScale=\"1\"", StringComparison.Ordinal), "Quick-search blur should cache its rendered layer during hover and drag animations.");
        Assert(xaml.Contains("x:Name=\"QuickSearchModeButton\"", StringComparison.Ordinal), "Quick search should let the Pixora icon switch search modes.");
        Assert(xaml.Contains("x:Name=\"QuickSearchModeText\"", StringComparison.Ordinal), "Quick search should always show whether the current mode is index or file name.");
        Assert(code.Contains("QuickSearchModeText.Text = searchesByFileName", StringComparison.Ordinal), "Quick-search mode label should update together with the active mode.");
        Assert(!xaml.Contains("Text=\"&#xE70D;\"", StringComparison.Ordinal), "Quick-search mode icon should not show a separate drop-down arrow.");
        Assert(!xaml.Contains("x:Name=\"ModeButtonChrome\"", StringComparison.Ordinal), "Quick-search mode icon should blend into the glass bar without a separate gray selection box.");
        Assert(xaml.Contains("Click=\"QuickSearchGoButton_Click\"", StringComparison.Ordinal), "Quick search should expose a clickable go button.");
        Assert(xaml.Contains("Text=\"→\"", StringComparison.Ordinal), "Quick search should use a forward arrow for explicit navigation.");
        Assert(xaml.Contains("InputMethod.IsInputMethodEnabled=\"True\"", StringComparison.Ordinal), "Quick search should locally enable IME for Chinese file-name input.");
        Assert(xaml.Contains("Grid.ColumnSpan=\"2\"", StringComparison.Ordinal), "Quick search should float across the main window instead of living inside the thumbnail sidebar.");
        Assert(code.Contains("ShouldShowQuickSearchThumbnailResults", StringComparison.Ordinal), "Quick search should filter thumbnail results only when the sidebar is visible.");
        Assert(code.Contains("if (result.MatchingIndices.Count == 0)", StringComparison.Ordinal), "A zero-result filename search should keep the current thumbnail sidebar instead of clearing it.");
        Assert(code.IndexOf("if (result.MatchingIndices.Count == 0)", StringComparison.Ordinal)
            < code.IndexOf("_thumbnailSearchItems = new IndexedProjectionList", StringComparison.Ordinal),
            "Quick-search thumbnails should only replace the current sidebar after finding valid matches.");
        Assert(code.Contains("_thumbnailItemsNeedRefresh = true;", StringComparison.Ordinal), "Hidden thumbnail sidebar should defer creating thumbnail item models until it is shown again.");
        Assert(code.Contains("new LazyIndexedList<ThumbnailItem>", StringComparison.Ordinal), "Large thumbnail catalogs should instantiate item models on demand.");
        Assert(xaml.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", StringComparison.Ordinal), "Thumbnail containers should use recycling virtualization.");
        Assert(code.Contains("ShowQuickSearchOnStartupIfNeeded", StringComparison.Ordinal), "Main window should restore persisted quick-search visibility after startup loading.");
        Assert(code.Contains("private void ToggleQuickSearch()", StringComparison.Ordinal), "Quick-search shortcut should toggle an already visible overlay instead of appearing unresponsive.");
        Assert(code.Split("ToggleQuickSearch();", StringSplitOptions.None).Length >= 3, "Quick-search shortcut should use the same toggle path whether the overlay is visible or hidden.");
        Assert(code.Contains("private void HideQuickSearch(bool rememberForStartup = true)", StringComparison.Ordinal), "Explicitly hiding quick search should persist the hidden startup state.");
        Assert(code.Contains("SetQuickSearchStartupPreference(false);", StringComparison.Ordinal), "Closing quick search should disable startup visibility until it is opened again.");
        Assert(code.Contains("ResetPersistedQuickSearchPosition();", StringComparison.Ordinal), "Closing quick search with its configured shortcut should reset the next opening position.");
        Assert(code.Contains("(Keyboard.Modifiers & ModifierKeys.Control) == 0", StringComparison.Ordinal), "Dragging quick search should require holding Ctrl.");
        Assert(code.Contains("_viewerSettings.QuickSearchOffsetX = offset.X;", StringComparison.Ordinal), "Dragging quick search should update its persisted horizontal offset.");
        Assert(code.Contains("if (_viewerSettings.HideQuickSearchAfterJump)", StringComparison.Ordinal), "Quick search should only close after a successful jump when the user enables that setting.");
        Assert(code.Contains("HideQuickSearch(rememberForStartup: false);", StringComparison.Ordinal), "Optional post-jump hiding should not overwrite the startup preference.");
        var quickSearchModeHandlerStart = code.IndexOf("private async void QuickSearchModeButton_Click", StringComparison.Ordinal);
        var quickSearchGoHandlerStart = code.IndexOf("private async void QuickSearchGoButton_Click", StringComparison.Ordinal);
        var quickSearchModeHandler = code[quickSearchModeHandlerStart..quickSearchGoHandlerStart];
        Assert(!quickSearchModeHandler.Contains("QuickSearchTextBox.Clear();", StringComparison.Ordinal), "Switching quick-search modes should preserve the current query.");
        Assert(quickSearchModeHandler.Contains("RefreshQuickSearchAsync();", StringComparison.Ordinal), "Switching to file-name search should asynchronously reinterpret the preserved query.");
        Assert(code.Contains("_quickSearchIndex.Search(query, previousResult, cts.Token)", StringComparison.Ordinal), "File-name search should run through the extracted cancellable index.");
        Assert(code.Contains("_folderChangeMonitor.Start(folder);", StringComparison.Ordinal), "Main window should delegate directory monitoring to the extracted service.");
        Assert(!code.Contains("new FileSystemWatcher", StringComparison.Ordinal), "Main window should not own low-level file-system watcher plumbing.");
        Assert(code.Contains("_thumbnailScrollIdleTimer", StringComparison.Ordinal), "Thumbnail loading should debounce rapid scrolling before resuming buffered preload.");
        Assert(code.Contains("UpdateVisibleThumbnailRange(queueLoads: false, bufferRows: 1);", StringComparison.Ordinal), "Rapid scrolling should update the active range without continuously starting decodes.");
        Assert(code.Contains("CancellationTokenSource.CreateLinkedTokenSource(_thumbnailCts.Token)", StringComparison.Ordinal), "Thumbnail viewport loads should have a cancellable lifetime separate from the catalog.");
        Assert(code.Contains("viewportGeneration != _thumbnailViewportGeneration", StringComparison.Ordinal), "Canceled thumbnail viewport tasks should not update a newer viewport.");
        Assert(
            code.IndexOf("Matches(ShortcutAction.ShowQuickSearch", StringComparison.Ordinal)
            < code.IndexOf("if (_isCropMode)", StringComparison.Ordinal),
            "Quick-search shortcut should be dispatched before viewer and crop actions can intercept it.");
        Assert(code.Contains("搜索刚打开且尚未开始输入时", StringComparison.Ordinal), "Freshly opened quick search should continue to route configured viewer shortcuts.");
        Assert(code.Contains("_quickSearchInteractionState.ShouldTextBoxHandleKey", StringComparison.Ordinal), "Explicit quick-search text entry should use the extracted interaction state before shortcut routing.");
        Assert(xaml.Contains("CaretBrush=\"Transparent\"", StringComparison.Ordinal), "Quick search should hide the caret while viewer shortcuts are active.");
        Assert(code.Contains("QuickSearchTextBox.CaretBrush = active ? Brushes.White : Brushes.Transparent;", StringComparison.Ordinal), "Quick search should show a blinking caret only in text-entry mode.");
        Assert(code.Contains("_shortcutSettings.ReplaceWith(ShortcutSettings.Load())", StringComparison.Ordinal), "Main window should reload saved shortcut settings immediately after the settings dialog closes.");
        Assert(xaml.Contains("<UniformGrid Rows=\"1\"", StringComparison.Ordinal), "Thumbnail rows should distribute cells evenly across the fixed sidebar width.");
        Assert(xaml.Contains("Columns=\"{Binding DataContext.ThumbnailColumnCount", StringComparison.Ordinal), "Thumbnail rows should follow the active single or double column count.");
        Assert(xaml.Contains("Width=\"{Binding DataContext.ThumbnailItemWidth", StringComparison.Ordinal), "Thumbnail cells should retain the compact fixed size used by the stable layout.");
        Assert(xaml.Contains("Padding=\"{Binding DataContext.ThumbnailItemPadding", StringComparison.Ordinal), "Thumbnail cell padding should preserve image size without clipping the selection border.");
        Assert(xaml.Contains("Margin=\"{Binding DataContext.ThumbnailItemMargin", StringComparison.Ordinal), "Single-column thumbnail cells should avoid horizontal margins that clip the selection border.");
        Assert(xaml.Contains("HorizontalAlignment=\"Center\"", StringComparison.Ordinal), "Fixed thumbnail cells should remain centered inside evenly distributed columns.");
        Assert(xaml.Contains("Padding=\"8,4,4,4\"", StringComparison.Ordinal), "Thumbnail list should balance its left inset against the scrollbar gutter.");
        Assert(xaml.Contains("Margin=\"0,0,1.5,0\"", StringComparison.Ordinal), "Thumbnail rows should keep balanced outer spacing in double-column mode.");
    }

    private static void AssertDpiAndAccessibility(string root)
    {
        var project = File.ReadAllText(Path.Combine(root, "src", "Pixora", "Pixora.csproj"));
        var manifest = File.ReadAllText(Path.Combine(root, "src", "Pixora", "app.manifest"));
        var mainXaml = File.ReadAllText(Path.Combine(root, "src", "Pixora", "MainWindow.xaml"));
        var settingsXaml = File.ReadAllText(Path.Combine(root, "src", "Pixora", "ShortcutSettingsWindow.xaml"));

        Assert(project.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", StringComparison.Ordinal), "Pixora should build with its explicit Windows application manifest.");
        Assert(manifest.Contains("PerMonitorV2,PerMonitor", StringComparison.Ordinal), "Pixora should opt into per-monitor V2 DPI awareness.");
        Assert(manifest.Contains("requestedExecutionLevel level=\"asInvoker\"", StringComparison.Ordinal), "Pixora should run with normal user privileges.");
        Assert(manifest.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", StringComparison.OrdinalIgnoreCase), "Pixora manifest should declare Windows 10 and later compatibility.");
        Assert(mainXaml.Contains("KeyboardNavigation.TabNavigation=\"Cycle\"", StringComparison.Ordinal), "Quick search should keep Tab navigation inside its compact control group.");
        Assert(mainXaml.Contains("AutomationProperties.Name=\"快速搜索输入\"", StringComparison.Ordinal), "Quick-search input should expose an accessible name.");
        Assert(mainXaml.Contains("AutomationProperties.Name=\"执行快速搜索\"", StringComparison.Ordinal), "Quick-search go button should expose an accessible name.");
        Assert(mainXaml.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "Dynamic viewer status should be announced without interrupting the user.");
        Assert(mainXaml.Contains("AutomationProperties.Name=\"当前目录缩略图\"", StringComparison.Ordinal), "Thumbnail list should expose an accessible name.");
        Assert(settingsXaml.Contains("AutomationProperties.Name=\"清理缩略图磁盘缓存\"", StringComparison.Ordinal), "Cache maintenance button should expose an accessible name.");
    }

    private static void AssertMainWindowInitializes()
    {
        var window = new MainWindow();
        var scanPanel = window.FindName("CatalogScanPanel") as FrameworkElement;
        var quickSearchOverlay = window.FindName("QuickSearchOverlay") as FrameworkElement;

        Assert(scanPanel is not null && scanPanel.Visibility == Visibility.Collapsed, "Background scan progress should start hidden.");
        Assert(quickSearchOverlay is not null && quickSearchOverlay.Visibility == Visibility.Collapsed, "Quick search should stay hidden until its shortcut is pressed.");

        var formatZoomScale = typeof(MainWindow).GetMethod(
            "FormatZoomScale",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FormatZoomScale was not found.");
        Assert(
            string.Equals(
                formatZoomScale.Invoke(null, [0.15, ZoomIndicatorDisplayMode.Percentage, 0.15]) as string,
                "15%",
                StringComparison.Ordinal),
            "Percentage zoom mode should show the absolute image scale.");
        Assert(
            string.Equals(
                formatZoomScale.Invoke(null, [0.15, ZoomIndicatorDisplayMode.Multiplier, 0.15]) as string,
                "1.00×",
                StringComparison.Ordinal),
            "Multiplier zoom mode should show fitted view as 1.00×.");
        Assert(
            string.Equals(
                formatZoomScale.Invoke(null, [0.1725, ZoomIndicatorDisplayMode.Multiplier, 0.15]) as string,
                "1.15×",
                StringComparison.Ordinal),
            "Multiplier zoom mode should show magnification relative to fitted view.");

        var shortcutSettings = (ShortcutSettings)(typeof(MainWindow).GetField(
            "_shortcutSettings",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException("Main-window shortcut settings field was not found."));
        shortcutSettings.SetSingleShortcut(
            ShortcutAction.PreviousImage,
            new KeyboardShortcut(Key.A, ModifierKeys.None));
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            foreach (var shortcut in shortcutSettings.GetShortcuts(action).ToArray())
            {
                if (shortcut.Matches(Key.Q, ModifierKeys.None))
                {
                    shortcutSettings.RemoveShortcut(action, shortcut);
                }
            }
        }

        var showQuickSearch = typeof(MainWindow).GetMethod(
            "ShowQuickSearch",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ShowQuickSearch was not found.");
        var windowKeyDown = typeof(MainWindow).GetMethod(
            "Window_KeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Window_KeyDown was not found.");
        showQuickSearch.Invoke(window, [false]);

        using var inputSource = new HwndSource(new HwndSourceParameters("Pixora smoke keyboard input"));
        var boundKey = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.A)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        windowKeyDown.Invoke(window, [window, boundKey]);
        Assert(boundKey.Handled, "A configured viewer shortcut should still be handled while quick search is visible.");

        var unboundKey = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Q)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        windowKeyDown.Invoke(window, [window, unboundKey]);
        Assert(unboundKey.Handled, "An unbound letter should be ignored while quick search remains in viewer-shortcut mode.");

        shortcutSettings.SetSingleShortcut(
            ShortcutAction.ToggleInfo,
            new KeyboardShortcut(Key.I, ModifierKeys.None));
        var setQuickSearchTextEntryActive = typeof(MainWindow).GetMethod(
            "SetQuickSearchTextEntryActive",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SetQuickSearchTextEntryActive was not found.");
        setQuickSearchTextEntryActive.Invoke(window, [true]);
        var focusedInputKey = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.I)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        windowKeyDown.Invoke(window, [window, focusedInputKey]);
        Assert(!focusedInputKey.Handled, "A letter shortcut should remain available for text after the user explicitly enters the quick-search input.");

        var deactivateQuickSearchTextEntry = typeof(MainWindow).GetMethod(
            "DeactivateQuickSearchTextEntry",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DeactivateQuickSearchTextEntry was not found.");
        deactivateQuickSearchTextEntry.Invoke(window, null);
        var restoredShortcutKey = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.I)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        windowKeyDown.Invoke(window, [window, restoredShortcutKey]);
        Assert(restoredShortcutKey.Handled, "Clicking outside quick-search text entry should restore the conflicting letter shortcut.");
    }

    private static void AssertCatalogUpdateHelpers(string imageFolder)
    {
        var reports = new List<int>();
        var catalog = new ImageCatalog();
        catalog.LoadFromFolder(imageFolder, CancellationToken.None, new ImmediateProgress<int>(reports.Add));
        Assert(reports.Count > 0 && reports[^1] == catalog.Count, "Catalog scan progress should report the final supported-media count.");

        var tenIndex = Array.FindIndex(
            catalog.Paths.ToArray(),
            path => string.Equals(Path.GetFileName(path), "10.png", StringComparison.OrdinalIgnoreCase));
        Assert(tenIndex >= 0, "Catalog search should find a matching file name.");
        Assert(catalog.MoveToIndex(tenIndex) && Path.GetFileName(catalog.CurrentPath) == "10.png", "Catalog should move to a zero-based index.");
        var removedPath = catalog.CurrentPath!;
        Assert(catalog.RemovePath(removedPath), "Catalog should remove an externally deleted path.");
        Assert(!catalog.Paths.Contains(removedPath, StringComparer.OrdinalIgnoreCase), "Removed catalog path should no longer be present.");
    }

    private static void AssertCatalogIncrementalChanges(string root)
    {
        var folder = Path.Combine(root, "test-output", "catalog-incremental");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "1.png");
        var second = Path.Combine(folder, "2.png");
        var renamed = Path.Combine(folder, "3.png");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);

        var catalog = new ImageCatalog();
        catalog.LoadFromFolder(folder);
        Assert(catalog.MoveTo(second), "Incremental catalog test should select the file that will be renamed.");
        File.Move(second, renamed);
        var renameResult = catalog.ApplyPathChanges([second], [renamed], renamed);
        Assert(renameResult.AddedCount == 1 && renameResult.RemovedCount == 1, "Incremental rename should remove the old path and add the new path once.");
        Assert(string.Equals(catalog.CurrentPath, renamed, StringComparison.OrdinalIgnoreCase), "Incremental rename should keep the renamed current item selected.");

        File.WriteAllBytes(renamed, [1, 2, 3]);
        var updateResult = catalog.ApplyPathChanges([], [renamed]);
        Assert(updateResult.UpdatedCount == 1 && !updateResult.CurrentPathChanged, "Incremental content changes should retain the current path.");

        File.Delete(first);
        var deleteResult = catalog.ApplyPathChanges([first], []);
        Assert(deleteResult.RemovedCount == 1 && catalog.Count == 1, "Incremental delete should remove only the affected catalog item.");
    }

    private static void AssertCatalogMetadataCache(string root)
    {
        var folder = Path.Combine(root, "test-output", "catalog-metadata-cache");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        var small = Path.Combine(folder, "small.png");
        var medium = Path.Combine(folder, "medium.png");
        var large = Path.Combine(folder, "large.png");
        File.WriteAllBytes(small, new byte[10]);
        File.WriteAllBytes(medium, new byte[20]);
        File.WriteAllBytes(large, new byte[30]);

        var catalog = new ImageCatalog
        {
            SortMode = ImageSortMode.FileSizeLargest,
        };
        catalog.LoadFromFolder(folder);
        Assert(catalog.CachedMetadataCount == 3, "File-size sorting should cache one metadata snapshot per catalog item.");
        Assert(Path.GetFileName(catalog.Paths[0]) == "large.png", "Metadata-backed sorting should order the largest file first.");

        catalog.ResortKeepingCurrent();
        Assert(catalog.CachedMetadataCount == 3, "Repeated sorting should reuse cached file metadata.");

        File.WriteAllBytes(small, new byte[40]);
        var update = catalog.ApplyPathChanges([], [small]);
        Assert(update.UpdatedCount == 1, "Catalog metadata test should observe the changed file.");
        Assert(Path.GetFileName(catalog.Paths[0]) == "small.png", "A changed file should invalidate its cached metadata before re-sorting.");
        Assert(catalog.CachedMetadataCount == 3, "Metadata invalidation should refresh only the changed entry.");
    }

    private static void AssertFolderChangeMonitor(string root)
    {
        var folder = Path.Combine(root, "test-output", "folder-change-monitor");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        using var received = new ManualResetEventSlim();
        FolderChangeBatch? observedBatch = null;
        using var monitor = new FolderChangeMonitor(TimeSpan.FromMilliseconds(80));
        monitor.BatchReady += (_, batch) =>
        {
            observedBatch = batch;
            received.Set();
        };
        monitor.Start(folder);
        var createdPath = Path.Combine(folder, "created.png");
        File.WriteAllBytes(createdPath, [1, 2, 3]);

        Assert(received.Wait(TimeSpan.FromSeconds(5)), "Folder change monitor should emit a debounced batch for a created file.");
        Assert(observedBatch is not null
            && observedBatch.Changes.Any(change => string.Equals(change.Path, createdPath, StringComparison.OrdinalIgnoreCase)),
            "Folder change monitor batch should contain the affected path.");
    }

    private static void AssertThumbnailDiskCache(string root)
    {
        var folder = Path.Combine(root, "test-output", "thumbnail-disk-cache");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        var sourceFolder = Path.Combine(root, "test-output", "thumbnail-disk-cache-sources");
        Directory.CreateDirectory(sourceFolder);
        var firstSource = Path.Combine(sourceFolder, "first.png");
        var secondSource = Path.Combine(sourceFolder, "second.png");
        File.WriteAllBytes(firstSource, [1]);
        File.WriteAllBytes(secondSource, [2]);

        var cache = new ThumbnailDiskCache(folder, maxBytes: 1024 * 1024);
        var bitmap = CreateCacheTestDocument("thumbnail-cache-test.png", 2, 2).Bitmap;
        cache.Save(firstSource, 64, 64, bitmap);
        cache.Save(secondSource, 64, 64, bitmap);
        var statistics = cache.GetStatistics();
        Assert(statistics.FileCount == 2, $"Thumbnail disk cache should save two entries, got {statistics.FileCount}.");
        var asyncStatistics = cache.GetStatisticsAsync().GetAwaiter().GetResult();
        Assert(asyncStatistics == statistics, "Asynchronous thumbnail cache statistics should match the synchronous result.");
        Assert(cache.TryLoad(firstSource, 64, 64, out var loaded) && loaded is not null, "Thumbnail disk cache should load a saved entry.");

        var trimmed = cache.TrimToBytes(1);
        Assert(trimmed.RemainingBytes <= 1, "Thumbnail disk cache trimming should enforce its byte budget.");
        Assert(cache.GetStatistics().FileCount == 0, "Thumbnail disk cache should remove entries that do not fit the byte budget.");

        cache.Save(firstSource, 64, 64, bitmap);
        var cleared = cache.ClearAsync().GetAwaiter().GetResult();
        Assert(cleared.RemovedFileCount == 1, "Thumbnail disk cache clear should remove saved entries.");
        Assert(cache.GetStatistics().FileCount == 0, "Thumbnail disk cache should be empty after clear.");

        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();
        var canceled = false;
        try
        {
            cache.GetStatisticsAsync(canceledCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert(canceled, "Asynchronous thumbnail cache maintenance should honor cancellation.");
    }

    private static void AssertRollingTextFile(string root)
    {
        var folder = Path.Combine(root, "test-output", "rolling-log");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        var log = new RollingTextFile(Path.Combine(folder, "error.log"), maximumBytes: 40);
        log.Append("first entry that fills the active log\n");
        log.Append("second entry\n");
        Assert(File.Exists(log.PreviousPath), "Rolling log should preserve the previous log when its capacity is exceeded.");
        Assert(log.ReadRecent(6) == "entry\n", "Rolling log should return the requested tail of the active log.");

        log.Clear();
        Assert(log.ReadRecent(100).Length == 0, "Clearing a rolling log should clear the active log.");
        Assert(!File.Exists(log.PreviousPath), "Clearing a rolling log should remove the previous log backup.");
    }

    private static void AssertThumbnailImageLoader(string imagePath, string root)
    {
        var cacheFolder = Path.Combine(root, "test-output", "thumbnail-image-loader-cache");
        if (Directory.Exists(cacheFolder))
        {
            Directory.Delete(cacheFolder, recursive: true);
        }

        var loader = new ThumbnailImageLoader(new ThumbnailDiskCache(cacheFolder));
        var thumbnail = loader.Load(imagePath, 64, 64, useDiskCache: true, CancellationToken.None);
        Assert(thumbnail.PixelWidth <= 64 && thumbnail.PixelHeight <= 64, "Thumbnail loader should constrain image dimensions to the requested bounds.");
        var cachedThumbnail = loader.Load(imagePath, 64, 64, useDiskCache: true, CancellationToken.None);
        Assert(cachedThumbnail.PixelWidth == thumbnail.PixelWidth && cachedThumbnail.PixelHeight == thumbnail.PixelHeight, "Thumbnail loader should reuse compatible disk cache entries.");
    }

    private static void AssertBatchCompressionSettings(string root)
    {
        var outputFolder = Path.Combine(root, "test-output");
        Directory.CreateDirectory(outputFolder);
        var settingsPath = Path.Combine(outputFolder, "batch-compression-settings-smoke.json");

        var settings = new BatchCompressionSettings
        {
            LastInputPath = Path.Combine(root, "test-images"),
            LastOutputFolder = Path.Combine(outputFolder, "batch-settings-output"),
            Format = ImageCompressionFormat.Png,
            JpegQuality = 91,
            MaxWidth = 1280,
            MaxHeight = 720,
            IncludeSubfolders = true,
            OverwriteExisting = true,
            BatchCompressWindowWidth = 1120,
            BatchCompressWindowHeight = 760,
            BatchCompressWindowLeft = 120,
            BatchCompressWindowTop = 80,
            BatchCompressWindowMaximized = true,
        };

        settings.Save(settingsPath);
        var loaded = BatchCompressionSettings.Load(settingsPath);
        Assert(string.Equals(loaded.LastInputPath, settings.LastInputPath, StringComparison.OrdinalIgnoreCase), "Batch compression settings should persist the last input path.");
        Assert(string.Equals(loaded.LastOutputFolder, settings.LastOutputFolder, StringComparison.OrdinalIgnoreCase), "Batch compression settings should persist the last output folder.");
        Assert(loaded.Format == ImageCompressionFormat.Png, "Batch compression settings should persist the format.");
        Assert(loaded.JpegQuality == 91, "Batch compression settings should persist JPEG quality.");
        Assert(loaded.MaxWidth == 1280, "Batch compression settings should persist max width.");
        Assert(loaded.MaxHeight == 720, "Batch compression settings should persist max height.");
        Assert(loaded.IncludeSubfolders, "Batch compression settings should persist recursive scan preference.");
        Assert(loaded.OverwriteExisting, "Batch compression settings should persist overwrite preference.");
        Assert(loaded.BatchCompressWindowWidth == 1120, "Batch compression settings should persist window width.");
        Assert(loaded.BatchCompressWindowHeight == 760, "Batch compression settings should persist window height.");
        Assert(loaded.BatchCompressWindowLeft == 120 && loaded.BatchCompressWindowTop == 80, "Batch compression settings should persist window position.");
        Assert(loaded.BatchCompressWindowMaximized, "Batch compression settings should persist maximized state.");
    }

    private static void EnsureAnimatedGif(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] bytes =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
            0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
            0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
            0x21, 0xFF, 0x0B,
            0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30,
            0x03, 0x01, 0x00, 0x00, 0x00,
            0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,
            0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0x02, 0x02, 0x44, 0x01, 0x00,
            0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,
            0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0x02, 0x02, 0x4C, 0x01, 0x00,
            0x3B,
        ];

        File.WriteAllBytes(path, bytes);
    }

    private static void EnsureManyFrameGif(string path, int frameCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var images = new MagickImageCollection();
        for (var index = 0; index < frameCount; index++)
        {
            var color = index % 2 == 0 ? MagickColors.Black : MagickColors.White;
            var frame = new MagickImage(color, 1, 1)
            {
                AnimationDelay = 1,
                AnimationTicksPerSecond = 100,
            };
            images.Add(frame);
        }

        images.Write(path, MagickFormat.Gif);
    }

    private static void EnsureRadianceHdr(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] header = System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 1\n");
        byte[] pixel = [128, 96, 64, 129];
        File.WriteAllBytes(path, [.. header, .. pixel]);
    }

    private static void EnsureAvif(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(MagickColors.CornflowerBlue, 1, 1);
        image.Write(path, MagickFormat.Avif);
    }

    private static void AssertCropMathClipsSelectionToImagePixels()
    {
        var transform = new ImageTransform(2.0, 10, 20);
        var exact = ImageViewportMath.CalculateCropPixelRect(new Rect(30, 40, 80, 60), 100, 80, transform);
        Assert(exact is { X: 10, Y: 10, Width: 40, Height: 30 }, $"Unexpected exact crop rect: {exact}.");

        var clipped = ImageViewportMath.CalculateCropPixelRect(new Rect(-20, -10, 260, 220), 100, 80, transform);
        Assert(clipped is { X: 0, Y: 0, Width: 100, Height: 80 }, $"Unexpected clipped crop rect: {clipped}.");

        var outside = ImageViewportMath.CalculateCropPixelRect(new Rect(-120, -80, 40, 30), 100, 80, transform);
        Assert(outside is null, $"Selection outside image should not produce a crop rect: {outside}.");
    }

    private static void AssertCircularCropMasksCorners()
    {
        const int width = 32;
        const int height = 32;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        source.Freeze();

        var method = typeof(MainWindow).GetMethod(
            "CreateCircularCropBitmap",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (method is null)
        {
            throw new InvalidOperationException("Circular crop helper should exist.");
        }

        var cropped = (BitmapSource)method.Invoke(null, [source])!;
        Assert(cropped.PixelWidth == width && cropped.PixelHeight == height, "Circular crop should keep square dimensions.");
        AssertPixelAlpha(cropped, 0, 0, expectOpaque: false);
        AssertPixelAlpha(cropped, width / 2, height / 2, expectOpaque: true);
    }

    private static void AssertImageCompressorSavesJpegAndPng(string root)
    {
        const int width = 96;
        const int height = 96;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset + 0] = (byte)(x * 255 / (width - 1));
                pixels[offset + 1] = (byte)(y * 255 / (height - 1));
                pixels[offset + 2] = (byte)((x + y) * 255 / (width + height - 2));
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();

        var outputFolder = Path.Combine(root, "test-output");
        Directory.CreateDirectory(outputFolder);
        var jpegPath = Path.Combine(outputFolder, "compress-smoke.jpg");
        var pngPath = Path.Combine(outputFolder, "compress-smoke.png");
        var resizedPath = Path.Combine(outputFolder, "compress-smoke-resized.jpg");

        var jpeg = ImageCompressor.Save(bitmap, new ImageCompressionOptions(ImageCompressionFormat.Jpeg, 72, jpegPath));
        var png = ImageCompressor.Save(bitmap, new ImageCompressionOptions(ImageCompressionFormat.Png, 100, pngPath));
        var resized = ImageCompressor.Save(bitmap, new ImageCompressionOptions(ImageCompressionFormat.Jpeg, 75, resizedPath, 48, 32));
        var resizedEstimate = ImageCompressor.Estimate(bitmap, new ImageCompressionOptions(ImageCompressionFormat.Jpeg, 75, resizedPath, 48, 32));

        Assert(jpeg.OutputBytes > 0 && File.Exists(jpegPath), "JPEG compression should create a file.");
        Assert(png.OutputBytes > 0 && File.Exists(pngPath), "PNG compression should create a file.");
        Assert(resized.OutputBytes > 0 && File.Exists(resizedPath), "Resized compression should create a file.");

        var jpegDocument = ImageLoader.Load(jpegPath, CancellationToken.None);
        var pngDocument = ImageLoader.Load(pngPath, CancellationToken.None);
        var resizedDocument = ImageLoader.Load(resizedPath, CancellationToken.None);
        Assert(jpegDocument.PixelWidth == width && jpegDocument.PixelHeight == height, "Compressed JPEG should keep dimensions.");
        Assert(pngDocument.PixelWidth == width && pngDocument.PixelHeight == height, "Compressed PNG should keep dimensions.");
        Assert(resizedDocument.PixelWidth == 48 && resizedDocument.PixelHeight == 32, "Resized compression should write requested dimensions.");
        Assert(resizedEstimate.PixelWidth == 48 && resizedEstimate.PixelHeight == 32, "Compression estimate should report resized dimensions.");
        Assert(ImageCompressor.EnsureExtension(Path.Combine(outputFolder, "sample"), ImageCompressionFormat.Jpeg).EndsWith(".jpg", StringComparison.OrdinalIgnoreCase), "JPEG default extension should be applied.");
        Assert(ImageCompressor.EnsureExtension(Path.Combine(outputFolder, "sample"), ImageCompressionFormat.Png).EndsWith(".png", StringComparison.OrdinalIgnoreCase), "PNG default extension should be applied.");
        Assert(
            string.Equals(
                ImageCompressor.GetDefaultOutputPath(Path.Combine(outputFolder, "sample.png"), ImageCompressionFormat.Jpeg),
                Path.Combine(outputFolder, "sample_压缩.jpg"),
                StringComparison.OrdinalIgnoreCase),
            "Compression default output path should stay beside the source image.");
    }

    private static void AssertBatchImageCompressor(string root)
    {
        var resize = BatchImageCompressor.CalculateResizeDimensions(4000, 2000, 1920, 1920);
        Assert(resize is { Width: 1920, Height: 960 }, $"Unexpected batch resize dimensions: {resize}.");
        Assert(BatchImageCompressor.CalculateResizeDimensions(800, 600, 1920, 1920) is null, "Batch resize should not upscale smaller images.");

        var inputFolder = Path.Combine(root, "test-output", "batch-compress-input");
        var outputFolder = Path.Combine(root, "test-output", "batch-compress-output");
        if (Directory.Exists(inputFolder))
        {
            Directory.Delete(inputFolder, recursive: true);
        }

        if (Directory.Exists(outputFolder))
        {
            Directory.Delete(outputFolder, recursive: true);
        }

        Directory.CreateDirectory(inputFolder);
        File.Copy(Path.Combine(root, "test-images", "1.png"), Path.Combine(inputFolder, "1.png"));
        File.Copy(Path.Combine(root, "test-images", "2.png"), Path.Combine(inputFolder, "2.png"));
        File.Copy(Path.Combine(root, "test-images", "animated.gif"), Path.Combine(inputFolder, "animated.gif"));
        File.Copy(Path.Combine(root, "test-images", "broken.jpg"), Path.Combine(inputFolder, "broken.jpg"));

        var options = new BatchCompressionOptions(
            inputFolder,
            outputFolder,
            ImageCompressionFormat.Jpeg,
            75,
            0,
            0,
            IncludeSubfolders: false,
            OverwriteExisting: false);

        var progressMessages = new List<string>();
        var progress = new ImmediateProgress<BatchCompressionProgress>(item => progressMessages.Add(item.Message));

        var sameFolderOptions = options with { OutputFolder = inputFolder };
        Assert(BatchImageCompressor.IsOutputFolderInsideInputFolder(sameFolderOptions), "Batch compression should warn when output folder equals input folder.");
        Assert(BatchImageCompressor.FindInputFiles(sameFolderOptions).Count == 4, "Using the input folder as output should not skip every source file.");

        var preflight = BatchImageCompressor.PreflightAsync(options, progress, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(preflight.Total == 4, $"Batch preflight should see 4 input files, got {preflight.Total}.");
        Assert(preflight.Compressible == 2, $"Batch preflight should report 2 compressible files, got {preflight.Compressible}.");
        Assert(preflight.Animated == 1, $"Batch preflight should report 1 animated image, got {preflight.Animated}.");
        Assert(preflight.Failed == 1, $"Batch preflight should report 1 failed image, got {preflight.Failed}.");
        Assert(progressMessages.Any(message => message.Contains("预扫描", StringComparison.OrdinalIgnoreCase)), "Preflight should report progress to the user.");

        progressMessages.Clear();
        var result = BatchImageCompressor.CompressAsync(options, progress, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(result.Total == 4, $"Batch compression should see 4 input files, got {result.Total}.");
        Assert(result.Saved == 2, $"Batch compression should save 2 files, got {result.Saved}.");
        Assert(result.Skipped == 1, $"Batch compression should report 1 skipped animation, got {result.Skipped}.");
        Assert(result.Failed == 1, $"Batch compression should report 1 failed image, got {result.Failed}.");
        Assert(result.SkippedItems.Count == 1, $"Batch compression should keep skipped details, got {result.SkippedItems.Count}.");
        Assert(result.FailedItems.Count == 1, $"Batch compression should keep failure details, got {result.FailedItems.Count}.");
        Assert(result.SkippedItems[0].Reason.Contains("动图", StringComparison.OrdinalIgnoreCase), "Skipped animation should explain why it was not compressed.");
        Assert(!string.IsNullOrWhiteSpace(result.FailedItems[0].Reason), "Failed compression should keep a readable failure reason.");
        Assert(progressMessages.Any(message => message.Contains("跳过", StringComparison.OrdinalIgnoreCase)), "Progress should tell the user when a file is skipped.");
        Assert(progressMessages.Any(message => message.Contains("失败", StringComparison.OrdinalIgnoreCase)), "Progress should tell the user when a file fails.");
        Assert(File.Exists(Path.Combine(outputFolder, "1_压缩.jpg")), "Batch compression should write the first output file.");
        Assert(File.Exists(Path.Combine(outputFolder, "2_压缩.jpg")), "Batch compression should write the second output file.");
    }

    private static void AssertFitMathKeepsTallImagesInsideViewport()
    {
        AssertFitInsideViewport(2863, 4441, 1100, 736);
        AssertFitInsideViewport(2039, 2894, 1100, 736);
        AssertFitInsideViewport(4441, 2863, 1100, 736);
    }

    private static void AssertFitMathDoesNotUpscaleSmallImagesByDefault()
    {
        var transform = ImageViewportMath.CalculateFitTransform(128, 96, 1100, 736, 0.02, 1.0);
        var target = ImageViewportMath.CalculateTargetRect(128, 96, transform);

        Assert(Math.Abs(transform.Scale - 1.0) < 0.001, $"Small images should stay at original scale by default, got {transform.Scale}.");
        Assert(Math.Abs(target.Width - 128) < 0.001, $"Small image width should stay original, got {target.Width}.");
        Assert(Math.Abs(target.Height - 96) < 0.001, $"Small image height should stay original, got {target.Height}.");
    }

    private static void AssertFitInsideViewport(int imageWidth, int imageHeight, double viewportWidth, double viewportHeight)
    {
        var transform = ImageViewportMath.CalculateFitTransform(imageWidth, imageHeight, viewportWidth, viewportHeight, 0.02, 1.0);
        var target = ImageViewportMath.CalculateTargetRect(imageWidth, imageHeight, transform);

        Assert(target.Width <= viewportWidth + 0.001, $"Fit width exceeds viewport: {target.Width} > {viewportWidth}.");
        Assert(target.Height <= viewportHeight + 0.001, $"Fit height exceeds viewport: {target.Height} > {viewportHeight}.");
        Assert(target.Left >= -0.001 || target.Width >= viewportWidth - 0.001, $"Unexpected negative left offset: {target.Left}.");
        Assert(target.Top >= -0.001 || target.Height >= viewportHeight - 0.001, $"Unexpected negative top offset: {target.Top}.");
    }

    private static void AssertPixelHasColor(BitmapSource bitmap, int x, int y, bool expectRed)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);

        var blue = pixel[0];
        var red = pixel[2];

        if (expectRed)
        {
            Assert(red > 200 && blue < 50, $"Expected red pixel at {x},{y}.");
        }
        else
        {
            Assert(blue > 200 && red < 50, $"Expected blue pixel at {x},{y}.");
        }
    }

    private static void AssertPixelAlpha(BitmapSource bitmap, int x, int y, bool expectOpaque)
    {
        var converted = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);

        if (expectOpaque)
        {
            Assert(pixel[3] > 200, $"Expected opaque pixel at {x},{y}.");
        }
        else
        {
            Assert(pixel[3] < 20, $"Expected transparent pixel at {x},{y}.");
        }
    }

    private static void AssertExternalImageRendersFull(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("External smoke image does not exist.", path);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var previewStopwatch = Stopwatch.StartNew();
        var preview = ImageLoader.LoadPreview(path, 1920, 1080, cts.Token);
        previewStopwatch.Stop();
        Assert(preview.PixelWidth > 0 && preview.PixelHeight > 0, "External image preview should decode.");

        var loadStopwatch = Stopwatch.StartNew();
        var document = ImageLoader.Load(path, cts.Token);
        loadStopwatch.Stop();
        var scale = Math.Min(600.0 / document.PixelWidth, 900.0 / document.PixelHeight);
        var targetWidth = Math.Max(1, (int)Math.Ceiling(document.PixelWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(document.PixelHeight * scale));

        if (document.IsAnimated)
        {
            Assert(document.AnimationFrames.All(frame =>
                frame.Bitmap.PixelWidth == document.PixelWidth &&
                frame.Bitmap.PixelHeight == document.PixelHeight), "Animated frames should be composited to the full logical canvas.");
            Console.WriteLine($"External animation passed: {document.FileName} => {document.PixelWidth} x {document.PixelHeight}, {document.AnimationFrames.Count} frames; full in {loadStopwatch.ElapsedMilliseconds} ms");
            return;
        }

        var viewer = new BitmapViewer
        {
            Source = document.Bitmap,
            ViewScale = scale,
            OffsetX = 0,
            OffsetY = 0,
        };

        viewer.Measure(new Size(targetWidth, targetHeight));
        viewer.Arrange(new Rect(0, 0, targetWidth, targetHeight));
        viewer.UpdateLayout();

        var rendered = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(viewer);

        var stride = targetWidth * 4;
        var pixels = new byte[stride * targetHeight];
        rendered.CopyPixels(pixels, stride, 0);

        var minX = targetWidth;
        var minY = targetHeight;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < targetHeight; y++)
        {
            var row = y * stride;
            for (var x = 0; x < targetWidth; x++)
            {
                if (pixels[row + x * 4 + 3] == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        var drawnWidth = maxX - minX + 1;
        var drawnHeight = maxY - minY + 1;
        if (BitmapHasTransparency(document.Bitmap))
        {
            Assert(drawnWidth > 0 && drawnHeight > 0, "Transparent external image should render some visible content.");
            Console.WriteLine(
                $"External transparent render passed: {document.FileName} => {targetWidth} x {targetHeight}, visible {drawnWidth} x {drawnHeight}; " +
                $"preview {preview.PixelWidth} x {preview.PixelHeight} in {previewStopwatch.ElapsedMilliseconds} ms; " +
                $"full {document.PixelWidth} x {document.PixelHeight} in {loadStopwatch.ElapsedMilliseconds} ms");
            return;
        }

        Assert(drawnWidth >= targetWidth * 0.95, $"External image render width is cropped: {drawnWidth}/{targetWidth}.");
        Assert(drawnHeight >= targetHeight * 0.95, $"External image render height is cropped: {drawnHeight}/{targetHeight}.");
        Console.WriteLine(
            $"External render passed: {document.FileName} => {targetWidth} x {targetHeight}; " +
            $"preview {preview.PixelWidth} x {preview.PixelHeight} in {previewStopwatch.ElapsedMilliseconds} ms; " +
            $"full {document.PixelWidth} x {document.PixelHeight} in {loadStopwatch.ElapsedMilliseconds} ms");
    }

    private static void AssertExternalLargeImageSafetyPreview(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("External large-image smoke sample does not exist.", path);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Assert(
            ImageLoader.RequiresSafetyPreview(path, cts.Token),
            "External BigTIFF should be detected as oversized even when its extension is incorrect.");

        var stopwatch = Stopwatch.StartNew();
        var document = ImageLoader.LoadPreviewDocument(
            path,
            maxWidth: 1_920,
            maxHeight: 1_080,
            cts.Token,
            LargeImagePolicy.DefaultPreviewMaximumSide);
        stopwatch.Stop();

        Assert(document.IsLargeImagePreview, "External large image should remain marked as a safety preview.");
        Assert(
            LargeImagePolicy.GetPixelCount(document.PixelWidth, document.PixelHeight)
                > LargeImagePolicy.FullResolutionPixelLimit,
            "External large-image metadata should retain the original oversized dimensions.");
        Assert(
            Math.Max(document.Bitmap.PixelWidth, document.Bitmap.PixelHeight)
                <= LargeImagePolicy.DefaultPreviewMaximumSide,
            "External large-image bitmap should stay inside the standard safety-preview tier.");

        var cachedDocument = ImageLoader.CreatePreviewDocument(path, document.Bitmap, cts.Token);
        Assert(cachedDocument.IsLargeImagePreview, "Cached BigTIFF previews should retain the safety-preview state.");
        Assert(
            cachedDocument.FormatName.Contains("金字塔安全预览", StringComparison.Ordinal),
            "Cached BigTIFF previews should retain their pyramid-preview format label.");

        var fullResolutionRejected = false;
        try
        {
            _ = ImageLoader.Load(path, cts.Token);
        }
        catch (InvalidDataException)
        {
            fullResolutionRejected = true;
        }

        Assert(fullResolutionRejected, "External BigTIFF should be rejected before full-resolution decoding.");

        Console.WriteLine(
            $"External large-image preview passed: {document.FileName} => "
            + $"source {document.PixelWidth} x {document.PixelHeight}, "
            + $"preview {document.Bitmap.PixelWidth} x {document.Bitmap.PixelHeight}, "
            + $"{document.FormatName}; {stopwatch.ElapsedMilliseconds} ms");
    }

    private static bool BitmapHasTransparency(BitmapSource bitmap)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 255)
            {
                return true;
            }
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pixora.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Pixora.sln.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TestOptions
    {
        public string? MediaFolder { get; set; }

        public int SampleCount { get; set; } = 20;

        public int VideoSampleCount { get; set; } = 8;

        public List<string> ExternalImages { get; } = [];

        public List<string> LargeImages { get; } = [];
    }

    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
        }
    }
}
