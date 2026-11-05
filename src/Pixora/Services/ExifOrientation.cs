using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pixora.Services;

internal static class ExifOrientation
{
    public static BitmapSource Apply(BitmapSource source, int orientation)
    {
        // TransformedBitmap normalizes the transformed bounds, including mirrored axes.
        Matrix? matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => null,
        };

        return matrix.HasValue
            ? new TransformedBitmap(source, new MatrixTransform(matrix.Value))
            : source;
    }

    public static (int Width, int Height) GetDimensions(int width, int height, int orientation)
    {
        return orientation is 5 or 6 or 7 or 8 ? (height, width) : (width, height);
    }

    public static int Read(BitmapFrame frame)
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

                var orientation = metadata.GetQuery(query) switch
                {
                    ushort value => value,
                    short value => value,
                    int value => value,
                    uint value when value <= int.MaxValue => (int)value,
                    string value when int.TryParse(value, out var parsed) => parsed,
                    _ => 0,
                };
                if (orientation is >= 1 and <= 8)
                {
                    return orientation;
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
}
