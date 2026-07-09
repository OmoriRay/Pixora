namespace Pixora.Services;

public static class MediaCatalogLoader
{
    public static Task<ImageCatalog> LoadFolderAsync(
        string folder,
        ImageSortMode sortMode,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var catalog = new ImageCatalog
            {
                SortMode = sortMode,
            };
            catalog.LoadFromFolder(folder, cancellationToken);
            return catalog;
        }, cancellationToken);
    }

    public static Task<ImageCatalog> LoadFromFileAsync(
        string path,
        ImageSortMode sortMode,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var catalog = new ImageCatalog
            {
                SortMode = sortMode,
            };
            catalog.LoadFromFile(path, cancellationToken);
            return catalog;
        }, cancellationToken);
    }
}
