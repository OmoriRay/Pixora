using System.IO;

namespace Pixora.Services;

public sealed class ImageCatalog
{
    private readonly WindowsLogicalStringComparer _windowsLogicalStringComparer = new();
    private List<string> _files = [];

    public int Count => _files.Count;

    public IReadOnlyList<string> Paths => _files;

    public ImageSortMode SortMode { get; set; } = ImageSortMode.NameNatural;

    public string? SourceFolder { get; private set; }

    public bool IsSingleFileCatalog { get; private set; }

    public int Index { get; private set; } = -1;

    public string? CurrentPath => Index >= 0 && Index < _files.Count ? _files[Index] : null;

    public static bool IsSupportedImagePath(string path)
    {
        return IsSupportedStillImagePath(path);
    }

    public static bool IsSupportedStillImagePath(string path)
    {
        return MediaFormatRegistry.IsSupportedStillImagePath(path);
    }

    public static bool IsSupportedVideoPath(string path)
    {
        return MediaFormatRegistry.IsSupportedVideoPath(path);
    }

    public static bool IsLikelyAnimatedImagePath(string path)
    {
        return MediaFormatRegistry.IsLikelyAnimatedImagePath(path);
    }

    public static bool IsSupportedMediaPath(string path)
    {
        return MediaFormatRegistry.IsSupportedMediaPath(path);
    }

    public void LoadFromFile(string path)
    {
        LoadFromFile(path, CancellationToken.None);
    }

    public void LoadFromFile(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            _files = [fullPath];
            Index = 0;
            SourceFolder = null;
            IsSingleFileCatalog = false;
            return;
        }

        var files = SortPaths(EnumerateSupportedMediaFiles(directory, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        var index = files.FindIndex(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            files.Add(fullPath);
            files = SortPaths(files);
            index = files.FindIndex(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
        }

        _files = files;
        Index = index;
        SourceFolder = directory;
        IsSingleFileCatalog = false;
    }

    public void LoadSingleFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _files = [fullPath];
        Index = 0;
        SourceFolder = Path.GetDirectoryName(fullPath);
        IsSingleFileCatalog = true;
    }

    public void LoadFromFolder(string folder)
    {
        LoadFromFolder(folder, CancellationToken.None);
    }

    public void LoadFromFolder(string folder, CancellationToken cancellationToken)
    {
        var fullFolder = Path.GetFullPath(folder);
        _files = SortPaths(EnumerateSupportedMediaFiles(fullFolder, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        Index = _files.Count > 0 ? 0 : -1;
        SourceFolder = fullFolder;
        IsSingleFileCatalog = false;
    }

    public void LoadFromPaths(IEnumerable<string> paths, string? preferredPath = null)
    {
        var distinctPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && IsSupportedMediaPath(fullPath))
            {
                distinctPaths.Add(fullPath);
            }
        }

        _files = SortPaths(distinctPaths);
        SourceFolder = null;
        IsSingleFileCatalog = false;

        if (_files.Count == 0)
        {
            Index = -1;
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var fullPreferredPath = Path.GetFullPath(preferredPath);
            var preferredIndex = _files.FindIndex(path => string.Equals(path, fullPreferredPath, StringComparison.OrdinalIgnoreCase));
            if (preferredIndex >= 0)
            {
                Index = preferredIndex;
                return;
            }
        }

        Index = 0;
    }

    public void LoadFromCatalog(ImageCatalog source)
    {
        ArgumentNullException.ThrowIfNull(source);

        SortMode = source.SortMode;
        _files = source._files.ToList();
        Index = source.Index;
        SourceFolder = source.SourceFolder;
        IsSingleFileCatalog = source.IsSingleFileCatalog;
    }

    public void ResortKeepingCurrent()
    {
        if (_files.Count == 0)
        {
            Index = -1;
            return;
        }

        var currentPath = CurrentPath;
        _files = SortPaths(_files);
        if (currentPath is null)
        {
            Index = _files.Count > 0 ? 0 : -1;
            return;
        }

        var index = _files.FindIndex(path => string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase));
        Index = index >= 0 ? index : (_files.Count > 0 ? 0 : -1);
    }

    public bool MoveNext()
    {
        if (_files.Count == 0)
        {
            return false;
        }

        Index = (Index + 1) % _files.Count;
        return true;
    }

    public bool MovePrevious()
    {
        if (_files.Count == 0)
        {
            return false;
        }

        Index = (Index - 1 + _files.Count) % _files.Count;
        return true;
    }

    public bool MoveTo(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var index = _files.FindIndex(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        Index = index;
        return true;
    }

    public bool AddOrUpdateExistingMediaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !IsSupportedMediaPath(fullPath))
        {
            return false;
        }

        var currentPath = CurrentPath;
        if (!_files.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            _files.Add(fullPath);
        }

        _files = SortPaths(_files);
        if (currentPath is null)
        {
            Index = _files.FindIndex(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        var currentIndex = _files.FindIndex(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
        Index = currentIndex >= 0 ? currentIndex : (_files.Count > 0 ? 0 : -1);
        return true;
    }

    public IReadOnlyList<string> GetNeighborPaths(int radius)
    {
        if (_files.Count <= 1 || Index < 0 || Index >= _files.Count)
        {
            return [];
        }

        var result = new List<string>();
        var maxDistance = Math.Min(Math.Max(0, radius), _files.Count - 1);
        for (var distance = 1; distance <= maxDistance; distance++)
        {
            result.Add(_files[WrapIndex(Index + distance, _files.Count)]);
            result.Add(_files[WrapIndex(Index - distance, _files.Count)]);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? RemoveCurrent()
    {
        if (Index < 0 || Index >= _files.Count)
        {
            return null;
        }

        var removed = _files[Index];
        _files.RemoveAt(Index);

        if (_files.Count == 0)
        {
            Index = -1;
        }
        else if (Index >= _files.Count)
        {
            Index = _files.Count - 1;
        }

        return removed;
    }

    private static string GetSortName(string path)
    {
        return Path.GetFileName(path) ?? path;
    }

    private static int WrapIndex(int index, int count)
    {
        return ((index % count) + count) % count;
    }

    private List<string> SortPaths(IEnumerable<string> paths)
    {
        return SortMode switch
        {
            ImageSortMode.NameNaturalDescending => paths
                .OrderByDescending(GetSortName, _windowsLogicalStringComparer)
                .ThenByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ImageSortMode.LastWriteTimeNewest => paths
                .OrderByDescending(GetLastWriteTimeUtc)
                .ThenBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ImageSortMode.LastWriteTimeOldest => paths
                .OrderBy(GetLastWriteTimeUtc)
                .ThenBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ImageSortMode.FileSizeLargest => paths
                .OrderByDescending(GetFileSize)
                .ThenBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ImageSortMode.FileSizeSmallest => paths
                .OrderBy(GetFileSize)
                .ThenBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => paths
                .OrderBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static DateTime GetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static long GetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<string> EnumerateSupportedMediaFiles(string folder, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupportedMediaPath(path))
            {
                yield return path;
            }
        }
    }
}
