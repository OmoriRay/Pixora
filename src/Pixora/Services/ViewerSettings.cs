using System.IO;

namespace Pixora.Services;

public enum SavedFileOpenBehavior
{
    None,
    NewWindow,
    CurrentWindow,
}

public enum QuickSearchMode
{
    Index,
    FileName,
}

public enum ZoomIndicatorDisplayMode
{
    Percentage,
    Multiplier,
}

public enum AppTheme
{
    Dark,
    Light,
}

public sealed class ViewerSettings
{
    public const int DefaultMainImageCacheMegabytes = 768;

    public const int DefaultDisplayPreviewCacheMegabytes = 192;

    public const int AutomaticMainImageCacheCapMegabytes = 8192;

    public const int AutomaticDisplayPreviewCacheCapMegabytes = 2048;

    public const int DefaultThumbnailDiskCacheMegabytes = 512;

    public bool ShowThumbnailSidebar { get; set; } = true;

    public bool UseDoubleThumbnailColumns { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public QuickSearchMode QuickSearchMode { get; set; } = global::Pixora.Services.QuickSearchMode.Index;

    public bool ShowQuickSearchOnStartup { get; set; }

    public bool HideQuickSearchAfterJump { get; set; }

    public double QuickSearchOffsetX { get; set; }

    public double QuickSearchOffsetY { get; set; }

    public SavedFileOpenBehavior SavedFileOpenBehavior { get; set; } = SavedFileOpenBehavior.None;

    public bool ConfirmDeleteToRecycleBin { get; set; } = true;

    public ImageSortMode SortMode { get; set; } = ImageSortMode.NameNatural;

    public string? LastOpenedFolder { get; set; }

    public bool OpenLastFolderOnStartup { get; set; }

    public bool RememberMainWindowPlacement { get; set; } = true;

    public bool StartMainWindowMaximized { get; set; }

    public bool ReuseExistingWindow { get; set; } = true;

    public bool KeepViewStateWhenNavigating { get; set; }

    public bool WatchFolderChanges { get; set; } = true;

    public double MainWindowWidth { get; set; }

    public double MainWindowHeight { get; set; }

    public double? MainWindowLeft { get; set; }

    public double? MainWindowTop { get; set; }

    public bool MainWindowMaximized { get; set; }

    public bool ShowAnimationControls { get; set; } = true;

    public bool ShowOperationNotifications { get; set; } = true;

    public bool ShowZoomIndicator { get; set; } = true;

    public ZoomIndicatorDisplayMode ZoomIndicatorDisplayMode { get; set; } = ZoomIndicatorDisplayMode.Percentage;

    public bool LoadFullResolutionWhenIdle { get; set; }

    public int MainImageCacheMegabytes { get; set; } = DefaultMainImageCacheMegabytes;

    public int DisplayPreviewCacheMegabytes { get; set; } = DefaultDisplayPreviewCacheMegabytes;

    public bool UseAutomaticCacheSizing { get; set; } = true;

    public bool EnableLowMemoryProtection { get; set; } = true;

    public bool UseThumbnailDiskCache { get; set; }

    public int ThumbnailDiskCacheMegabytes { get; set; } = DefaultThumbnailDiskCacheMegabytes;

    public bool IncludePrivatePathsInDiagnostics { get; set; }

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
        try
        {
            var settings = AtomicJsonFile.Load<ViewerSettings>(path) ?? new ViewerSettings();
            settings.Normalize();
            return settings;
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
        Normalize();
        AtomicJsonFile.Save(path, this);
    }

    private void Normalize()
    {
        if (!Enum.IsDefined<global::Pixora.Services.AppTheme>(Theme))
        {
            Theme = AppTheme.Dark;
        }

        if (!Enum.IsDefined<global::Pixora.Services.QuickSearchMode>(QuickSearchMode))
        {
            QuickSearchMode = global::Pixora.Services.QuickSearchMode.Index;
        }

        if (!Enum.IsDefined<global::Pixora.Services.ZoomIndicatorDisplayMode>(ZoomIndicatorDisplayMode))
        {
            ZoomIndicatorDisplayMode = global::Pixora.Services.ZoomIndicatorDisplayMode.Percentage;
        }

        QuickSearchOffsetX = NormalizeOffset(QuickSearchOffsetX);
        QuickSearchOffsetY = NormalizeOffset(QuickSearchOffsetY);

        MainWindowWidth = NormalizeWindowDimension(MainWindowWidth, 640, 10_000);
        MainWindowHeight = NormalizeWindowDimension(MainWindowHeight, 420, 10_000);
    }

    private static double NormalizeOffset(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, -10_000, 10_000)
            : 0;
    }

    private static double NormalizeWindowDimension(double value, double minimum, double maximum)
    {
        return double.IsFinite(value) && value >= minimum
            ? Math.Min(value, maximum)
            : 0;
    }

    private static string SettingsPath =>
        Path.Combine(
            AppInfo.LocalDataFolder,
            "viewer-settings.json");
}
