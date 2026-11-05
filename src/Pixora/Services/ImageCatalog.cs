using System.IO;

namespace Pixora.Services;

public sealed record ImageCatalogMutationResult(
    int AddedCount,
    int RemovedCount,
    int UpdatedCount,
    string? PreviousCurrentPath,
    string? CurrentPath)
{
    public bool Changed => AddedCount > 0 || RemovedCount > 0 || UpdatedCount > 0;

    public bool CurrentPathChanged => !string.Equals(PreviousCurrentPath, CurrentPath, StringComparison.OrdinalIgnoreCase);
}

public sealed class ImageCatalog
{
    private readonly WindowsLogicalStringComparer _windowsLogicalStringComparer = new();
    private readonly Dictionary<string, FileMetadata> _fileMetadata = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _files = [];

    public int Count => _files.Count;

    public int CachedMetadataCount => _fileMetadata.Count;

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
        LoadFromFile(path, cancellationToken, progress: null);
    }

    public void LoadFromFile(string path, CancellationToken cancellationToken, IProgress<int>? progress)
    {
        _fileMetadata.Clear();
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

        var files = SortPaths(EnumerateSupportedMediaFiles(directory, cancellationToken, progress));
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
        _fileMetadata.Clear();
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
        LoadFromFolder(folder, cancellationToken, progress: null);
    }

    public void LoadFromFolder(string folder, CancellationToken cancellationToken, IProgress<int>? progress)
    {
        _fileMetadata.Clear();
        var fullFolder = Path.GetFullPath(folder);
        _files = SortPaths(EnumerateSupportedMediaFiles(fullFolder, cancellationToken, progress));
        cancellationToken.ThrowIfCancellationRequested();

        Index = _files.Count > 0 ? 0 : -1;
        SourceFolder = fullFolder;
        IsSingleFileCatalog = false;
    }

    public void LoadFromPaths(IEnumerable<string> paths, string? preferredPath = null, bool requireExistingFiles = true)
    {
        _fileMetadata.Clear();
        var distinctPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if ((!requireExistingFiles || File.Exists(fullPath)) && IsSupportedMediaPath(fullPath))
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
        _fileMetadata.Clear();
        foreach (var pair in source._fileMetadata)
        {
            _fileMetadata[pair.Key] = pair.Value;
        }
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

    public bool MoveToIndex(int index)
    {
        if (index < 0 || index >= _files.Count)
        {
            return false;
        }

        Index = index;
        return true;
    }

    public bool RemovePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var removedIndex = _files.FindIndex(item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
        if (removedIndex < 0)
        {
            return false;
        }

        var currentPath = CurrentPath;
        _files.RemoveAt(removedIndex);
        _fileMetadata.Remove(fullPath);
        if (_files.Count == 0)
        {
            Index = -1;
            return true;
        }

        if (currentPath is not null && !string.Equals(currentPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            var currentIndex = _files.FindIndex(item => string.Equals(item, currentPath, StringComparison.OrdinalIgnoreCase));
            if (currentIndex >= 0)
            {
                Index = currentIndex;
                return true;
            }
        }

        Index = Math.Min(removedIndex, _files.Count - 1);
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
        _fileMetadata.Remove(fullPath);
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

    public ImageCatalogMutationResult ApplyPathChanges(
        IEnumerable<string> removedPaths,
        IEnumerable<string> addedOrUpdatedPaths,
        string? preferredCurrentPath = null)
    {
        ArgumentNullException.ThrowIfNull(removedPaths);
        ArgumentNullException.ThrowIfNull(addedOrUpdatedPaths);

        var previousCurrentPath = CurrentPath;
        var previousIndex = Index;
        var removed = NormalizePathSet(removedPaths);
        var removedCount = _files.RemoveAll(path => removed.Contains(path));
        foreach (var removedPath in removed)
        {
            _fileMetadata.Remove(removedPath);
        }
        var existing = new HashSet<string>(_files, StringComparer.OrdinalIgnoreCase);
        var addedCount = 0;
        var updatedCount = 0;

        foreach (var path in addedOrUpdatedPaths)
        {
            if (!TryNormalizeExistingMediaPath(path, out var fullPath))
            {
                continue;
            }

            _fileMetadata.Remove(fullPath);

            if (existing.Add(fullPath))
            {
                _files.Add(fullPath);
                addedCount++;
            }
            else
            {
                updatedCount++;
            }
        }

        if (addedCount > 0
            || removedCount > 0
            || (updatedCount > 0 && SortMode is ImageSortMode.LastWriteTimeNewest
                or ImageSortMode.LastWriteTimeOldest
                or ImageSortMode.FileSizeLargest
                or ImageSortMode.FileSizeSmallest))
        {
            _files = SortPaths(_files);
        }

        if (_files.Count == 0)
        {
            Index = -1;
        }
        else if (TryFindPath(preferredCurrentPath, out var preferredIndex))
        {
            Index = preferredIndex;
        }
        else if (TryFindPath(previousCurrentPath, out var currentIndex))
        {
            Index = currentIndex;
        }
        else
        {
            Index = Math.Clamp(previousIndex, 0, _files.Count - 1);
        }

        IsSingleFileCatalog = false;
        return new ImageCatalogMutationResult(
            addedCount,
            removedCount,
            updatedCount,
            previousCurrentPath,
            CurrentPath);
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

    public IReadOnlyList<string> GetDirectionalNeighborPaths(
        int direction,
        int forwardRadius = 3,
        int oppositeRadius = 1)
    {
        if (_files.Count <= 1 || Index < 0 || Index >= _files.Count)
        {
            return [];
        }

        if (direction == 0)
        {
            return GetNeighborPaths(forwardRadius);
        }

        var normalizedDirection = Math.Sign(direction);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var distance = 1; distance <= Math.Max(0, forwardRadius); distance++)
        {
            var path = _files[WrapIndex(Index + (distance * normalizedDirection), _files.Count)];
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }

        for (var distance = 1; distance <= Math.Max(0, oppositeRadius); distance++)
        {
            var path = _files[WrapIndex(Index - (distance * normalizedDirection), _files.Count)];
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    public string? RemoveCurrent()
    {
        if (Index < 0 || Index >= _files.Count)
        {
            return null;
        }

        var removed = _files[Index];
        _files.RemoveAt(Index);
        _fileMetadata.Remove(removed);

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

    private bool TryFindPath(string? path, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        index = _files.FindIndex(item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
        return index >= 0;
    }

    private static HashSet<string> NormalizePathSet(IEnumerable<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                result.Add(Path.GetFullPath(path));
            }
            catch
            {
            }
        }

        return result;
    }

    private static bool TryNormalizeExistingMediaPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) && IsSupportedMediaPath(fullPath);
        }
        catch
        {
            return false;
        }
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
                .OrderByDescending(path => GetFileMetadata(path).LastWriteTimeUtc)
                .ThenBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ImageSortMode.LastWriteTimeOldest => paths
                .OrderBy(path => GetFileMetadata(path).LastWriteTimeUtc)
                .ThenBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ImageSortMode.FileSizeLargest => paths
                .OrderByDescending(path => GetFileMetadata(path).Length)
                .ThenBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ImageSortMode.FileSizeSmallest => paths
                .OrderBy(path => GetFileMetadata(path).Length)
                .ThenBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => paths
                .OrderBy(GetSortName, _windowsLogicalStringComparer)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private FileMetadata GetFileMetadata(string path)
    {
        if (_fileMetadata.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var info = new FileInfo(path);
            var metadata = new FileMetadata(info.Length, info.LastWriteTimeUtc);
            _fileMetadata[path] = metadata;
            return metadata;
        }
        catch
        {
            var metadata = new FileMetadata(0, DateTime.MinValue);
            _fileMetadata[path] = metadata;
            return metadata;
        }
    }

    private readonly record struct FileMetadata(long Length, DateTime LastWriteTimeUtc);

    private static IEnumerable<string> EnumerateSupportedMediaFiles(
        string folder,
        CancellationToken cancellationToken,
        IProgress<int>? progress)
    {
        var supportedCount = 0;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupportedMediaPath(path))
            {
                supportedCount++;
                if (supportedCount == 1 || supportedCount % 250 == 0)
                {
                    progress?.Report(supportedCount);
                }

                yield return path;
            }
        }

        progress?.Report(supportedCount);
    }
}
