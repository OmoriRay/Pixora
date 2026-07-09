using System.IO;
using System.Text.Json;

namespace Pixora.Services;

public enum SavedFileOpenBehavior
{
    None,
    NewWindow,
    CurrentWindow,
}

public sealed class ViewerSettings
{
    public const int DefaultMainImageCacheMegabytes = 768;

    public const int DefaultDisplayPreviewCacheMegabytes = 192;

    public const int DefaultThumbnailDiskCacheMegabytes = 512;

    public bool ShowThumbnailSidebar { get; set; } = true;

    public bool UseDoubleThumbnailColumns { get; set; } = true;

    public SavedFileOpenBehavior SavedFileOpenBehavior { get; set; } = SavedFileOpenBehavior.None;

    public bool ConfirmDeleteToRecycleBin { get; set; } = true;

    public ImageSortMode SortMode { get; set; } = ImageSortMode.NameNatural;

    public string? LastOpenedFolder { get; set; }

    public bool OpenLastFolderOnStartup { get; set; }

    public bool ShowDirectoryStats { get; set; }

    public bool ShowAnimationControls { get; set; } = true;

    public bool ShowOperationNotifications { get; set; } = true;

    public bool LoadFullResolutionWhenIdle { get; set; }

    public int MainImageCacheMegabytes { get; set; } = DefaultMainImageCacheMegabytes;

    public int DisplayPreviewCacheMegabytes { get; set; } = DefaultDisplayPreviewCacheMegabytes;

    public bool EnableLowMemoryProtection { get; set; } = true;

    public bool UseThumbnailDiskCache { get; set; }

    public int ThumbnailDiskCacheMegabytes { get; set; } = DefaultThumbnailDiskCacheMegabytes;

    public double ShortcutSettingsWindowWidth { get; set; }

    public double ShortcutSettingsWindowHeight { get; set; }

    public double? ShortcutSettingsWindowLeft { get; set; }

    public double? ShortcutSettingsWindowTop { get; set; }

    public bool ShortcutSettingsWindowMaximized { get; set; }

    public static ViewerSettings Load()
    {
        return Load(SettingsPath);
    }

    public static ViewerSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ViewerSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ViewerSettings>(json) ?? new ViewerSettings();
        }
        catch
        {
            return new ViewerSettings();
        }
    }

    public void Save()
    {
        Save(SettingsPath);
    }

    public void Save(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string SettingsPath =>
        Path.Combine(
            AppInfo.LocalDataFolder,
            "viewer-settings.json");
}
