namespace Pixora.Services;

public readonly record struct TiffOverviewCandidate(int FrameIndex, int Width, int Height)
{
    public long PixelCount => LargeImagePolicy.GetPixelCount(Width, Height);

    public int MaximumSide => Math.Max(Width, Height);
}

public static class TiffOverviewSelector
{
    public const long MinimumDecodePixelLimit = 4_000_000;

    private const double MaximumAspectRatioDifference = 0.08;

    public static TiffOverviewCandidate? SelectBest(
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<TiffOverviewCandidate> candidates,
        int targetMaximumSide)
    {
        LargeImagePolicy.ValidateSourceDimensions(sourceWidth, sourceHeight);
        targetMaximumSide = Math.Max(1, targetMaximumSide);
        var sourceAspectRatio = sourceWidth / (double)sourceHeight;
        var decodePixelLimit = GetDecodePixelLimit(targetMaximumSide);

        TiffOverviewCandidate? best = null;
        var bestScore = double.MaxValue;
        foreach (var candidate in candidates)
        {
            if (candidate.FrameIndex <= 0
                || candidate.Width <= 0
                || candidate.Height <= 0
                || candidate.PixelCount <= 0
                || candidate.PixelCount > decodePixelLimit
                || candidate.Width > sourceWidth
                || candidate.Height > sourceHeight
                || (candidate.Width == sourceWidth && candidate.Height == sourceHeight))
            {
                continue;
            }

            var aspectRatio = candidate.Width / (double)candidate.Height;
            var aspectRatioDifference = Math.Abs(aspectRatio - sourceAspectRatio) / sourceAspectRatio;
            if (aspectRatioDifference > MaximumAspectRatioDifference)
            {
                continue;
            }

            var score = Math.Abs(Math.Log(candidate.MaximumSide / (double)targetMaximumSide));
            if (score < bestScore
                || (Math.Abs(score - bestScore) < 0.000001
                    && (!best.HasValue || candidate.MaximumSide > best.Value.MaximumSide)))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    public static long GetDecodePixelLimit(int targetMaximumSide)
    {
        var side = Math.Max(1L, targetMaximumSide);
        var targetBudget = Math.Min(
            LargeImagePolicy.FullResolutionPixelLimit,
            checked(side * side * 2));
        return Math.Max(MinimumDecodePixelLimit, targetBudget);
    }
}
