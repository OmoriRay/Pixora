using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pixora.Services;

namespace Pixora.SmokeTests;

internal static class OrientationRegressionTests
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(220, 30, 40), Color.FromRgb(30, 210, 50), Color.FromRgb(40, 50, 220),
        Color.FromRgb(220, 190, 30), Color.FromRgb(200, 40, 190), Color.FromRgb(30, 200, 210),
    ];

    // Expected row-major positions for the original 3 x 2 grid: ABC / DEF.
    private static readonly int[][] Expected =
    [
        [0, 1, 2, 3, 4, 5], [2, 1, 0, 5, 4, 3],
        [5, 4, 3, 2, 1, 0], [3, 4, 5, 0, 1, 2],
        [0, 3, 1, 4, 2, 5], [3, 0, 4, 1, 5, 2],
        [5, 2, 4, 1, 3, 0], [2, 5, 1, 4, 0, 3],
    ];

    public static void Run(string root)
    {
        var folder = Path.Combine(root, "test-output", "exif-orientation");
        Directory.CreateDirectory(folder);
        var loader = new ThumbnailImageLoader(new ThumbnailDiskCache(Path.Combine(folder, "cache")));
        var apply = typeof(ImageLoader).Assembly.GetType("Pixora.Services.ExifOrientation")!
            .GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!;

        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var width = orientation >= 5 ? 2 : 3;
            var height = orientation >= 5 ? 3 : 2;
            var exact = (BitmapSource)apply.Invoke(null, [CreateGrid(1), orientation])!;
            Check(exact.PixelWidth == width && exact.PixelHeight == height,
                $"EXIF {orientation}: transformed pixel dimensions are incorrect.");
            AssertGrid(exact, orientation, tolerance: 0);

            foreach (var format in new[] { "jpg", "tiff" })
            {
                var path = Path.Combine(folder, $"orientation-{orientation}.{format}");
                var metadata = new BitmapMetadata(format);
                metadata.SetQuery(format == "jpg" ? "/app1/ifd/{ushort=274}" : "/ifd/{ushort=274}", (ushort)orientation);
                BitmapEncoder encoder = format == "jpg"
                    ? new JpegBitmapEncoder { QualityLevel = 100 }
                    : new TiffBitmapEncoder { Compression = TiffCompressOption.None };
                encoder.Frames.Add(BitmapFrame.Create(CreateGrid(40), null, metadata, null));
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                var full = ImageLoader.Load(path, CancellationToken.None);
                Check(!full.FormatName.Contains("Magick.NET", StringComparison.Ordinal),
                    $"EXIF {orientation}: fixture must exercise the WIC decoder.");
                Check(full.PixelWidth == width * 40 && full.PixelHeight == height * 40,
                    $"EXIF {orientation}: full image metadata dimensions are incorrect.");
                Check(full.Bitmap.PixelWidth == full.PixelWidth && full.Bitmap.PixelHeight == full.PixelHeight,
                    $"EXIF {orientation}: full image bitmap dimensions disagree with metadata.");
                AssertGrid(full.Bitmap, orientation, tolerance: 12);

                foreach (var (maxWidth, maxHeight) in new[] { (45, 90), (90, 45) })
                {
                    var preview = ImageLoader.LoadPreviewDocument(path, maxWidth, maxHeight, CancellationToken.None);
                    Check(preview.PixelWidth == full.PixelWidth && preview.PixelHeight == full.PixelHeight,
                        $"EXIF {orientation}: preview original dimensions disagree with the full image.");
                    AssertBounds(preview.Bitmap, orientation, maxWidth, maxHeight);
                    AssertGrid(preview.Bitmap, orientation, tolerance: 12);

                    var thumbnail = loader.Load(path, maxWidth, maxHeight, false, CancellationToken.None);
                    AssertBounds(thumbnail, orientation, maxWidth, maxHeight);
                    AssertGrid(thumbnail, orientation, tolerance: 12);
                    Check(thumbnail.IsFrozen && preview.Bitmap.IsFrozen,
                        $"EXIF {orientation}: decoded previews must be frozen for cross-thread use.");
                }
            }
        }
    }

    private static BitmapSource CreateGrid(int cellSize)
    {
        var width = 3 * cellSize;
        var height = 2 * cellSize;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = Colors[y / cellSize * 3 + x / cellSize];
                var offset = (y * width + x) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static void AssertBounds(BitmapSource bitmap, int orientation, int maxWidth, int maxHeight)
    {
        Check(bitmap.PixelWidth <= maxWidth && bitmap.PixelHeight <= maxHeight,
            $"EXIF {orientation}: {bitmap.PixelWidth} x {bitmap.PixelHeight} exceeds {maxWidth} x {maxHeight} bounds.");
        var ratio = orientation >= 5 ? 2.0 / 3 : 3.0 / 2;
        Check(Math.Abs(bitmap.PixelWidth - bitmap.PixelHeight * ratio) <= 2,
            $"EXIF {orientation}: preview aspect ratio is incorrect.");
    }

    private static void AssertGrid(BitmapSource bitmap, int orientation, int tolerance)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var columns = orientation >= 5 ? 2 : 3;
        var rows = orientation >= 5 ? 3 : 2;
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var x = (2 * column + 1) * converted.PixelWidth / (2 * columns);
                var y = (2 * row + 1) * converted.PixelHeight / (2 * rows);
                var offset = (y * converted.PixelWidth + x) * 4;
                var expected = Colors[Expected[orientation - 1][row * columns + column]];
                Check(Math.Abs(pixels[offset] - expected.B) <= tolerance
                    && Math.Abs(pixels[offset + 1] - expected.G) <= tolerance
                    && Math.Abs(pixels[offset + 2] - expected.R) <= tolerance,
                    $"EXIF {orientation}: pixel at grid position ({column}, {row}) is incorrect.");
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
