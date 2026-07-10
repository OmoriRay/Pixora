using ImageMagick;
using Pixora.Services;
using Pixora.Controls;
using Pixora.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Diagnostics;

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
        AssertWebImageExtensions();
        AssertMediaFormatRegistry();
        AssertFileAssociationMoveRepair();
        AssertMediaCatalogLoaderCancellation(imageFolder);
        AssertVideoMediaSupport(root);
        AssertCatalogSortModes(root);
        AssertFavoriteStore(root);
        AssertShortcutSettings();
        AssertViewerSettings(root);
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

    private static void AssertViewerSettings(string root)
    {
        var outputFolder = Path.Combine(root, "test-output");
        Directory.CreateDirectory(outputFolder);
        var settingsPath = Path.Combine(outputFolder, "viewer-settings-smoke.json");

        var settings = new ViewerSettings
        {
            ShowThumbnailSidebar = false,
            UseDoubleThumbnailColumns = false,
            SavedFileOpenBehavior = SavedFileOpenBehavior.NewWindow,
            ConfirmDeleteToRecycleBin = false,
            SortMode = ImageSortMode.FileSizeLargest,
            LastOpenedFolder = outputFolder,
            OpenLastFolderOnStartup = true,
            ShowDirectoryStats = true,
            ShowAnimationControls = false,
            ShowOperationNotifications = false,
            ThumbnailDiskCacheMegabytes = 1024,
        };

        settings.Save(settingsPath);
        var loaded = ViewerSettings.Load(settingsPath);
        Assert(!loaded.ShowThumbnailSidebar, "Viewer settings should persist hidden thumbnail sidebar state.");
        Assert(!loaded.UseDoubleThumbnailColumns, "Viewer settings should persist thumbnail column preference.");
        Assert(loaded.SavedFileOpenBehavior == SavedFileOpenBehavior.NewWindow, "Viewer settings should persist saved file open behavior.");
        Assert(!loaded.ConfirmDeleteToRecycleBin, "Viewer settings should persist delete confirmation preference.");
        Assert(loaded.SortMode == ImageSortMode.FileSizeLargest, "Viewer settings should persist image sort mode.");
        Assert(string.Equals(loaded.LastOpenedFolder, outputFolder, StringComparison.OrdinalIgnoreCase), "Viewer settings should persist the last opened folder.");
        Assert(loaded.OpenLastFolderOnStartup, "Viewer settings should persist last-folder startup behavior.");
        Assert(loaded.ShowDirectoryStats, "Viewer settings should persist directory stats visibility.");
        Assert(!loaded.ShowAnimationControls, "Viewer settings should persist animation control visibility.");
        Assert(!loaded.ShowOperationNotifications, "Viewer settings should persist operation notification visibility.");
        Assert(loaded.ThumbnailDiskCacheMegabytes == 1024, "Viewer settings should persist thumbnail disk cache capacity.");
        Assert(FileAssociationService.SupportedExtensions.Contains(".gif"), "File association extensions should include GIF.");
        Assert(FileAssociationService.SupportedExtensions.All(extension => ImageCatalog.IsSupportedStillImagePath("sample" + extension)), "File association extensions should be supported image extensions.");
    }

    private static void AssertMemoryCacheCoordinator()
    {
        var imageCache = ImageCache.FromMegabytes(1);
        var previewCache = BitmapSourceMemoryCache.FromMegabytes(1);
        var coordinator = new MemoryCacheCoordinator(imageCache, previewCache);

        coordinator.ApplyConfiguredBudgets(2, 3);
        var snapshot = coordinator.CaptureSnapshot(isProtectionEnabled: false);
        Assert(snapshot.MainImageBudgetBytes == 2L * 1024 * 1024, "Memory coordinator should apply the main image cache budget.");
        Assert(snapshot.PreviewBudgetBytes == 3L * 1024 * 1024, "Memory coordinator should apply the preview cache budget.");
        Assert(MemoryCacheCoordinator.IsUnderPressure(90, 100), "Memory coordinator should detect the 90% memory pressure threshold.");
        Assert(!MemoryCacheCoordinator.IsUnderPressure(89, 100), "Memory coordinator should not trim below the pressure threshold.");
    }

    private static void AssertSettingsWindowInitializes()
    {
        var viewerSettings = new ViewerSettings
        {
            ThumbnailDiskCacheMegabytes = 1024,
        };
        var window = new ShortcutSettingsWindow(ShortcutSettings.Load(), viewerSettings);
        var comboBox = window.FindName("ThumbnailDiskCacheSizeComboBox") as ComboBox;
        var cachePathText = window.FindName("ThumbnailDiskCachePathText") as TextBlock;
        var generalPage = window.FindName("GeneralPage") as ScrollViewer;

        Assert(comboBox is not null, "Settings window should initialize the thumbnail disk cache capacity selector.");
        Assert(comboBox!.SelectedValue?.ToString() == "1024", "Settings window should select the persisted thumbnail disk cache capacity.");
        Assert(cachePathText?.Text.Contains("当前占用", StringComparison.Ordinal) == true, "Settings window should show thumbnail disk cache usage.");
        Assert(generalPage?.ClipToBounds == true, "Settings page should clip scrolling content inside its viewport.");
        Assert(TextOptions.GetTextRenderingMode(generalPage) == TextRenderingMode.Grayscale, "Settings page should use stable grayscale text rendering while scrolling.");
        Assert(TextOptions.GetTextHintingMode(generalPage) == TextHintingMode.Animated, "Settings page should use animated text hinting while scrolling.");
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
        Assert(cache.TryLoad(firstSource, 64, 64, out var loaded) && loaded is not null, "Thumbnail disk cache should load a saved entry.");

        var trimmed = cache.TrimToBytes(1);
        Assert(trimmed.RemainingBytes <= 1, "Thumbnail disk cache trimming should enforce its byte budget.");
        Assert(cache.GetStatistics().FileCount == 0, "Thumbnail disk cache should remove entries that do not fit the byte budget.");

        cache.Save(firstSource, 64, 64, bitmap);
        var cleared = cache.Clear();
        Assert(cleared.RemovedFileCount == 1, "Thumbnail disk cache clear should remove saved entries.");
        Assert(cache.GetStatistics().FileCount == 0, "Thumbnail disk cache should be empty after clear.");
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
    }

    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
        }
    }
}
