using Pixora.Models;
using Pixora.Services;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pixora;

public partial class ShortcutSettingsWindow : Window
{
    private readonly ShortcutSettings _source;
    private readonly ShortcutSettings _working;
    private readonly ViewerSettings _viewerSettings;
    private ShortcutAction? _capturingAction;
    private KeyboardShortcut? _shortcutBeingReplaced;
    private bool _windowPlacementSaved;
    private CancellationTokenSource? _thumbnailCacheInfoCts;
    private CancellationTokenSource? _thumbnailCacheMaintenanceCts;
    private List<ShortcutRow> _shortcutRows = [];
    private int _shortcutGridColumnCount = 1;
    private const double ShortcutGridItemGap = 8;
    private const double ShortcutGridMinimumTwoColumnItemWidth = 300;

    public ShortcutSettingsWindow(ShortcutSettings settings, ViewerSettings viewerSettings)
    {
        InitializeComponent();
        _source = settings;
        _working = settings.Clone();
        _viewerSettings = viewerSettings;
        ApplySavedWindowPlacement();
        SavedFileOpenBehaviorComboBox.SelectedValue = viewerSettings.SavedFileOpenBehavior.ToString();
        ConfirmDeleteToRecycleBinCheckBox.IsChecked = viewerSettings.ConfirmDeleteToRecycleBin;
        OpenLastFolderOnStartupCheckBox.IsChecked = viewerSettings.OpenLastFolderOnStartup;
        ReuseExistingWindowCheckBox.IsChecked = viewerSettings.ReuseExistingWindow;
        KeepViewStateWhenNavigatingCheckBox.IsChecked = viewerSettings.KeepViewStateWhenNavigating;
        WatchFolderChangesCheckBox.IsChecked = viewerSettings.WatchFolderChanges;
        ShowThumbnailSidebarCheckBox.IsChecked = viewerSettings.ShowThumbnailSidebar;
        UseDoubleThumbnailColumnsCheckBox.IsChecked = viewerSettings.UseDoubleThumbnailColumns;
        QuickSearchModeComboBox.SelectedValue = viewerSettings.QuickSearchMode.ToString();
        ShowQuickSearchOnStartupCheckBox.IsChecked = viewerSettings.ShowQuickSearchOnStartup;
        HideQuickSearchAfterJumpCheckBox.IsChecked = viewerSettings.HideQuickSearchAfterJump;
        RememberMainWindowPlacementCheckBox.IsChecked = viewerSettings.RememberMainWindowPlacement;
        StartMainWindowMaximizedCheckBox.IsChecked = viewerSettings.StartMainWindowMaximized;
        ShowAnimationControlsCheckBox.IsChecked = viewerSettings.ShowAnimationControls;
        ShowOperationNotificationsCheckBox.IsChecked = viewerSettings.ShowOperationNotifications;
        ShowZoomIndicatorCheckBox.IsChecked = viewerSettings.ShowZoomIndicator;
        ZoomIndicatorDisplayModeComboBox.SelectedValue = viewerSettings.ZoomIndicatorDisplayMode.ToString();
        LoadFullResolutionWhenIdleCheckBox.IsChecked = viewerSettings.LoadFullResolutionWhenIdle;
        SelectComboBoxValue(MainImageCacheComboBox, viewerSettings.MainImageCacheMegabytes, ViewerSettings.DefaultMainImageCacheMegabytes);
        SelectComboBoxValue(DisplayPreviewCacheComboBox, viewerSettings.DisplayPreviewCacheMegabytes, ViewerSettings.DefaultDisplayPreviewCacheMegabytes);
        CacheSizingModeComboBox.SelectedValue = viewerSettings.UseAutomaticCacheSizing ? "Automatic" : "Manual";
        UpdateCacheSizingModeUi();
        LowMemoryProtectionCheckBox.IsChecked = viewerSettings.EnableLowMemoryProtection;
        ThumbnailDiskCacheCheckBox.IsChecked = viewerSettings.UseThumbnailDiskCache;
        IncludePrivatePathsInDiagnosticsCheckBox.IsChecked = viewerSettings.IncludePrivatePathsInDiagnostics;
        SelectComboBoxValue(ThumbnailDiskCacheSizeComboBox, viewerSettings.ThumbnailDiskCacheMegabytes, ViewerSettings.DefaultThumbnailDiskCacheMegabytes);
        _ = RefreshThumbnailDiskCacheInfoAsync();
        AppVersionText.Text = $"{AppInfo.Name} {GetAppVersion()}";
        RefreshRows();
        UpdateButtons();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _thumbnailCacheInfoCts?.Cancel();
        _thumbnailCacheMaintenanceCts?.Cancel();
        if (_windowPlacementSaved)
        {
            return;
        }

        try
        {
            SaveWindowPlacement();
            _viewerSettings.Save();
            _windowPlacementSaved = true;
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("SaveShortcutSettingsWindowPlacement", "保存设置窗口位置失败。", ex);
        }
    }

    private void ApplySavedWindowPlacement()
    {
        if (_viewerSettings.ShortcutSettingsWindowWidth >= MinWidth)
        {
            Width = _viewerSettings.ShortcutSettingsWindowWidth;
        }

        if (_viewerSettings.ShortcutSettingsWindowHeight >= MinHeight)
        {
            Height = _viewerSettings.ShortcutSettingsWindowHeight;
        }

        var savedLeft = _viewerSettings.ShortcutSettingsWindowLeft;
        var savedTop = _viewerSettings.ShortcutSettingsWindowTop;
        if (savedLeft.HasValue
            && savedTop.HasValue
            && IsRectVisibleOnVirtualScreen(
                savedLeft.Value,
                savedTop.Value,
                Width,
                Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = savedLeft.Value;
            Top = savedTop.Value;
        }

        if (_viewerSettings.ShortcutSettingsWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        _viewerSettings.ShortcutSettingsWindowMaximized = WindowState == WindowState.Maximized;
        var bounds = RestoreBounds;
        if (bounds.Width >= MinWidth)
        {
            _viewerSettings.ShortcutSettingsWindowWidth = bounds.Width;
        }

        if (bounds.Height >= MinHeight)
        {
            _viewerSettings.ShortcutSettingsWindowHeight = bounds.Height;
        }

        if (IsRectVisibleOnVirtualScreen(bounds.Left, bounds.Top, bounds.Width, bounds.Height))
        {
            _viewerSettings.ShortcutSettingsWindowLeft = bounds.Left;
            _viewerSettings.ShortcutSettingsWindowTop = bounds.Top;
        }
    }

    private static bool IsRectVisibleOnVirtualScreen(double left, double top, double width, double height)
    {
        if (double.IsNaN(left)
            || double.IsNaN(top)
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        var right = left + width;
        var bottom = top + height;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        return right > screenLeft
            && left < screenRight
            && bottom > screenTop
            && top < screenBottom;
    }
    private void SettingsTab_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPage is null
            || ShortcutsPage is null
            || StatusText is null
            || ShortcutActionButtonsPanel is null)
        {
            return;
        }

        var showGeneral = GeneralTabButton.IsChecked == true;
        GeneralPage.Visibility = showGeneral ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = showGeneral ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = showGeneral ? Visibility.Collapsed : Visibility.Visible;
        ShortcutActionButtonsPanel.Visibility = showGeneral ? Visibility.Collapsed : Visibility.Visible;
        ApplySettingsSearchFilter();
    }

    private void CacheSizingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCacheSizingModeUi();
    }

    private void UpdateCacheSizingModeUi()
    {
        if (ManualCacheSettingsPanel is null
            || AutomaticCacheSummaryPanel is null
            || AutomaticCacheSummaryText is null)
        {
            return;
        }

        var automatic = IsAutomaticCacheSizingSelected();
        ManualCacheSettingsPanel.IsEnabled = !automatic;
        ManualCacheSettingsPanel.Opacity = automatic ? 0.48 : 1;
        AutomaticCacheSummaryPanel.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        if (!automatic)
        {
            return;
        }

        var memoryInfo = GC.GetGCMemoryInfo();
        var profile = MemoryCacheCoordinator.ResolvePerformanceProfile(
            ViewerSettings.AutomaticMainImageCacheCapMegabytes,
            ViewerSettings.AutomaticDisplayPreviewCacheCapMegabytes,
            useAutomaticSizing: true,
            memoryInfo.TotalAvailableMemoryBytes,
            Environment.ProcessorCount);
        AutomaticCacheSummaryText.Text =
            $"当前预算：原图 {FormatMegabytes(profile.CacheBudgets.MainImageMegabytes)} · 预览 {FormatMegabytes(profile.CacheBudgets.PreviewMegabytes)}\n"
            + $"预加载：前向 {profile.MainPreloadForwardRadius} / 反向 {profile.MainPreloadOppositeRadius} · 缩略图并发 {profile.ThumbnailLoadConcurrency}";
    }

    private bool IsAutomaticCacheSizingSelected()
    {
        return string.Equals(
            CacheSizingModeComboBox?.SelectedValue?.ToString(),
            "Automatic",
            StringComparison.Ordinal);
    }

    private void SettingsSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SettingsSearchPlaceholderText is null)
        {
            return;
        }

        SettingsSearchPlaceholderText.Visibility = string.IsNullOrEmpty(SettingsSearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplySettingsSearchFilter();
    }

    private void ApplySettingsSearchFilter()
    {
        if (FileBehaviorSettingsSection is null
            || InterfaceSettingsSection is null
            || PerformanceSettingsSection is null
            || DiagnosticsSettingsSection is null
            || FileAssociationsSettingsSection is null
            || ShortcutGrid is null)
        {
            return;
        }

        var query = SettingsSearchTextBox?.Text.Trim() ?? string.Empty;
        SetSettingsSectionVisibility(
            FileBehaviorSettingsSection,
            query,
            "文件 行为 保存 打开 删除 回收站 启动 目录 窗口 复用 缩放 监视 刷新");
        SetSettingsSectionVisibility(
            InterfaceSettingsSection,
            query,
            "界面 缩略图 单列 双列 搜索 序号 文件名 主窗口 最大化 统计 动图 操作提示 缩放 百分比 倍数 原图 安全预览");
        SetSettingsSectionVisibility(
            PerformanceSettingsSection,
            query,
            "性能 缓存 内存 原图 预览 缩略图 磁盘 清理 容量 自动 推荐 手动 预算 预加载 并发");
        SetSettingsSectionVisibility(
            DiagnosticsSettingsSection,
            query,
            "诊断 关于 版本 日志 路径 隐私 复制");
        SetSettingsSectionVisibility(
            FileAssociationsSettingsSection,
            query,
            "文件关联 默认应用 格式 注册 取消关联 高级");

        ApplyShortcutRows();
    }

    private static void SetSettingsSectionVisibility(FrameworkElement section, string query, string keywords)
    {
        section.Visibility = string.IsNullOrWhiteSpace(query)
            || keywords.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void RefreshRows()
    {
        var selectedAction = (ShortcutGrid.SelectedItem as ShortcutDisplayRow)?.Action;
        var selectedShortcut = (ShortcutGrid.SelectedItem as ShortcutDisplayRow)?.Shortcut;
        var rows = new List<ShortcutRow>();

        foreach (var item in ShortcutSettings.ActionInfos.OrderBy(static item => GetShortcutActionSortIndex(item.Action)))
        {
            var shortcuts = _working.GetShortcuts(item.Action);
            if (shortcuts.Count == 0)
            {
                rows.Add(new ShortcutRow(item.Action, item.Category, item.Name, null));
                continue;
            }

            rows.AddRange(shortcuts.Select(shortcut => new ShortcutRow(item.Action, item.Category, item.Name, shortcut)));
        }

        _shortcutRows = rows;
        ApplyShortcutRows(selectedAction, selectedShortcut);
        UpdateButtons();
    }

    private void ShortcutGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void ShortcutGrid_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateShortcutGridItemWidth();
    }

    private void ShortcutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateShortcutGridItemWidth();
    }

    private void UpdateShortcutGridItemWidth()
    {
        var availableWidth = ShortcutGrid.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var contentWidth = Math.Max(0, availableWidth - SystemParameters.VerticalScrollBarWidth - 2);
        var twoColumnItemWidth = Math.Floor((contentWidth - ShortcutGridItemGap * 2) / 2);
        var useTwoColumns = twoColumnItemWidth >= ShortcutGridMinimumTwoColumnItemWidth;
        var columnCount = useTwoColumns ? 2 : 1;
        var itemWidth = useTwoColumns
            ? twoColumnItemWidth
            : Math.Floor(contentWidth - ShortcutGridItemGap);

        if (itemWidth <= 0)
        {
            return;
        }

        if (ShortcutGrid.Tag is not double currentWidth || Math.Abs(currentWidth - itemWidth) >= 0.5)
        {
            ShortcutGrid.Tag = itemWidth;
        }

        if (_shortcutGridColumnCount == columnCount)
        {
            return;
        }

        var selectedAction = (ShortcutGrid.SelectedItem as ShortcutDisplayRow)?.Action;
        var selectedShortcut = (ShortcutGrid.SelectedItem as ShortcutDisplayRow)?.Shortcut;
        _shortcutGridColumnCount = columnCount;
        ApplyShortcutRows(selectedAction, selectedShortcut);
    }


    private void ApplyShortcutRows(ShortcutAction? selectedAction = null, KeyboardShortcut? selectedShortcut = null)
    {
        var query = SettingsSearchTextBox?.Text.Trim() ?? string.Empty;
        var sourceRows = string.IsNullOrWhiteSpace(query)
            ? _shortcutRows
            : _shortcutRows.Where(row =>
                row.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.ActionName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.ShortcutText.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var rows = ArrangeShortcutRowsForDisplay(sourceRows, _shortcutGridColumnCount);
        var displayRows = CreateShortcutDisplayRows(rows, _shortcutGridColumnCount);
        ShortcutGrid.ItemsSource = displayRows;
        ShortcutGrid.SelectedItem = displayRows.FirstOrDefault(row =>
            row.Action == selectedAction &&
            ShortcutsEqual(row.Shortcut, selectedShortcut)) ??
            displayRows.FirstOrDefault(row => row.Action == selectedAction) ??
            displayRows.FirstOrDefault();
    }


    private static IReadOnlyList<ShortcutDisplayRow> CreateShortcutDisplayRows(IReadOnlyList<ShortcutRow> rows, int columnCount)
    {
        var displayRows = new List<ShortcutDisplayRow>(rows.Count);
        var categoryByColumn = new string?[Math.Max(1, columnCount)];
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var columnIndex = index % categoryByColumn.Length;
            var categoryText = row.Category == categoryByColumn[columnIndex] ? string.Empty : row.Category;
            displayRows.Add(new ShortcutDisplayRow(row, categoryText));
            categoryByColumn[columnIndex] = row.Category;
        }

        return displayRows;
    }
    private static IReadOnlyList<ShortcutRow> ArrangeShortcutRowsForDisplay(IReadOnlyList<ShortcutRow> rows, int columnCount)
    {
        if (columnCount <= 1 || rows.Count <= columnCount)
        {
            return rows;
        }

        var arranged = new List<ShortcutRow>(rows.Count);
        var rowCount = (rows.Count + columnCount - 1) / columnCount;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var sourceIndex = columnIndex * rowCount + rowIndex;
                if (sourceIndex < rows.Count)
                {
                    arranged.Add(rows[sourceIndex]);
                }
            }
        }

        return arranged;
    }
    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutGrid.SelectedItem is not ShortcutDisplayRow row)
        {
            StatusText.Text = "请先选择一行。";
            return;
        }

        _capturingAction = row.Action;
        _shortcutBeingReplaced = row.Shortcut;
        StatusText.Text = row.Shortcut is null
            ? $"正在给“{row.ActionName}”设置快捷键。按新快捷键，Esc 取消。"
            : $"正在修改“{row.ActionName}”的 {row.ShortcutText}。按新快捷键，Esc 取消。";
        Focus();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutGrid.SelectedItem is not ShortcutDisplayRow row)
        {
            StatusText.Text = "请先选择一个功能。";
            return;
        }

        _capturingAction = row.Action;
        _shortcutBeingReplaced = null;
        StatusText.Text = $"正在给“{row.ActionName}”添加快捷键。按新快捷键，Esc 取消。";
        Focus();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutGrid.SelectedItem is not ShortcutDisplayRow row)
        {
            StatusText.Text = "请先选择一行。";
            return;
        }

        if (row.Shortcut is null)
        {
            StatusText.Text = "该功能当前没有可删除的快捷键。";
            return;
        }

        _capturingAction = null;
        _shortcutBeingReplaced = null;
        _working.RemoveShortcut(row.Action, row.Shortcut);
        RefreshRows();
        StatusText.Text = $"已删除“{row.ActionName}”的 {row.ShortcutText}，点击保存后生效。";
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingAction = null;
        _shortcutBeingReplaced = null;
        _working.ResetToDefaults();
        RefreshRows();
        StatusText.Text = "已恢复默认快捷键，点击保存后生效。";
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "确定清空所有快捷键吗？\n\n清空后需要点击保存才会生效，也可以点击“恢复默认”找回默认快捷键。",
            "清空所有快捷键",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _capturingAction = null;
        _shortcutBeingReplaced = null;
        _working.ClearAll();
        RefreshRows();
        StatusText.Text = "已清空所有快捷键，点击保存后生效。";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _source.ReplaceWith(_working);
        try
        {
            _source.Save();
            _viewerSettings.SavedFileOpenBehavior = GetSelectedSavedFileOpenBehavior();
            _viewerSettings.ConfirmDeleteToRecycleBin = ConfirmDeleteToRecycleBinCheckBox.IsChecked == true;
            _viewerSettings.OpenLastFolderOnStartup = OpenLastFolderOnStartupCheckBox.IsChecked == true;
            _viewerSettings.ReuseExistingWindow = ReuseExistingWindowCheckBox.IsChecked == true;
            _viewerSettings.KeepViewStateWhenNavigating = KeepViewStateWhenNavigatingCheckBox.IsChecked == true;
            _viewerSettings.WatchFolderChanges = WatchFolderChangesCheckBox.IsChecked == true;
            _viewerSettings.ShowThumbnailSidebar = ShowThumbnailSidebarCheckBox.IsChecked == true;
            _viewerSettings.UseDoubleThumbnailColumns = UseDoubleThumbnailColumnsCheckBox.IsChecked == true;
            _viewerSettings.QuickSearchMode = GetSelectedQuickSearchMode();
            _viewerSettings.ShowQuickSearchOnStartup = ShowQuickSearchOnStartupCheckBox.IsChecked == true;
            _viewerSettings.HideQuickSearchAfterJump = HideQuickSearchAfterJumpCheckBox.IsChecked == true;
            _viewerSettings.RememberMainWindowPlacement = RememberMainWindowPlacementCheckBox.IsChecked == true;
            _viewerSettings.StartMainWindowMaximized = StartMainWindowMaximizedCheckBox.IsChecked == true;
            _viewerSettings.ShowAnimationControls = ShowAnimationControlsCheckBox.IsChecked == true;
            _viewerSettings.ShowOperationNotifications = ShowOperationNotificationsCheckBox.IsChecked == true;
            _viewerSettings.ShowZoomIndicator = ShowZoomIndicatorCheckBox.IsChecked == true;
            _viewerSettings.ZoomIndicatorDisplayMode = GetSelectedZoomIndicatorDisplayMode();
            _viewerSettings.LoadFullResolutionWhenIdle = LoadFullResolutionWhenIdleCheckBox.IsChecked == true;
            _viewerSettings.MainImageCacheMegabytes = GetSelectedMegabytes(MainImageCacheComboBox, ViewerSettings.DefaultMainImageCacheMegabytes);
            _viewerSettings.DisplayPreviewCacheMegabytes = GetSelectedMegabytes(DisplayPreviewCacheComboBox, ViewerSettings.DefaultDisplayPreviewCacheMegabytes);
            _viewerSettings.UseAutomaticCacheSizing = IsAutomaticCacheSizingSelected();
            _viewerSettings.EnableLowMemoryProtection = LowMemoryProtectionCheckBox.IsChecked == true;
            _viewerSettings.UseThumbnailDiskCache = ThumbnailDiskCacheCheckBox.IsChecked == true;
            _viewerSettings.IncludePrivatePathsInDiagnostics = IncludePrivatePathsInDiagnosticsCheckBox.IsChecked == true;
            _viewerSettings.ThumbnailDiskCacheMegabytes = GetSelectedMegabytes(
                ThumbnailDiskCacheSizeComboBox,
                ViewerSettings.DefaultThumbnailDiskCacheMegabytes);
            SaveWindowPlacement();
            _viewerSettings.Save();
            _windowPlacementSaved = true;
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("SaveSettings", "保存设置失败。", ex);
            MessageBox.Show(this, $"保存设置失败：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private async void ClearThumbnailDiskCacheButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "确定清理本机缩略图缓存吗？\n\n不会删除原始图片或视频。之后再次浏览时会重新生成缩略图。",
            "清理缩略图缓存",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _thumbnailCacheMaintenanceCts?.Cancel();
        _thumbnailCacheMaintenanceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _thumbnailCacheMaintenanceCts = cts;
        ClearThumbnailDiskCacheButton.IsEnabled = false;
        ThumbnailDiskCachePathText.Text = "正在清理缩略图缓存…";
        try
        {
            var result = await new ThumbnailDiskCache().ClearAsync(cts.Token);
            await RefreshThumbnailDiskCacheInfoAsync();
            if (ShowOperationNotificationsCheckBox.IsChecked == true)
            {
                MessageBox.Show(
                    this,
                    $"已清理 {result.RemovedFileCount:N0} 个缓存文件，释放 {FormatFileSize(result.RemovedBytes)}。",
                    "缩略图缓存",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("ClearThumbnailDiskCache", "清理缩略图缓存失败。", ex);
            MessageBox.Show(this, $"清理缩略图缓存失败：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_thumbnailCacheMaintenanceCts, cts))
            {
                _thumbnailCacheMaintenanceCts = null;
            }

            cts.Dispose();
            ClearThumbnailDiskCacheButton.IsEnabled = true;
        }
    }

    private void OpenErrorLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ErrorLog.LogFolder);
            Process.Start(new ProcessStartInfo(ErrorLog.LogFolder)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("OpenErrorLogFolder", "打开错误日志位置失败。", ex);
            MessageBox.Show(this, $"打开错误日志位置失败：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mainWindow)
        {
            mainWindow.CopyDiagnosticsInfo();
            return;
        }

        MessageBox.Show(this, "请从主窗口打开设置后再复制诊断信息。", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RegisterFileAssociationsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                throw new FileNotFoundException($"找不到当前 {AppInfo.Name} 程序文件。", processPath);
            }

            FileAssociationService.Register(processPath);
            if (ShowOperationNotificationsCheckBox.IsChecked == true)
            {
                MessageBox.Show(
                    this,
                    $"已把常见图片格式加入 Windows“打开方式”的 {AppInfo.Name} 项。",
                    "文件关联",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("RegisterFileAssociations", "设置文件关联失败。", ex);
            MessageBox.Show(this, $"设置文件关联失败：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RegisterAndOpenDefaultAppsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                throw new FileNotFoundException($"找不到当前 {AppInfo.Name} 程序文件。", processPath);
            }

            FileAssociationService.Register(processPath);
            if (FileAssociationService.TrySetDefaultAssociationsSilently(out _))
            {
                return;
            }

            FileAssociationService.OpenDefaultAppsSettings();
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("RegisterAndOpenDefaultApps", "设置默认应用失败。", ex);
            MessageBox.Show(this, $"设置默认应用失败：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AdvancedDefaultAssociationsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                throw new FileNotFoundException($"找不到当前 {AppInfo.Name} 程序文件。", processPath);
            }

            FileAssociationService.Register(processPath);
            if (!FileAssociationService.TryGetExternalDefaultToolPath(out var toolPath))
            {
                MessageBox.Show(
                    this,
                    $"未找到 SetUserFTA.exe。\n\n请把 SetUserFTA.exe 放到：\n{toolPath}\n\n这是高级模式使用的外部工具，{AppInfo.Name} 不内置该工具；请只使用你信任的来源。",
                    "高级一键默认",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                $"高级模式会调用外部工具批量修改当前用户的默认图片应用关联。\n\n工具路径：\n{toolPath}\n\n这不是 Windows 公开推荐的默认应用设置方式，系统更新后可能失效。确定继续吗？",
                "高级一键默认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var result = await Task.Run(() => FileAssociationService.SetDefaultAssociationsWithExternalTool(toolPath));
            if (result.Failures.Count == 0)
            {
                MessageBox.Show(
                    this,
                    $"已完成：{result.Succeeded}/{result.Total} 个图片格式已设置为 {AppInfo.Name}。",
                    "高级一键默认",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var failures = string.Join("\n", result.Failures.Take(8).Select(item => $"{item.Extension}：{item.Message}"));
            if (result.Failures.Count > 8)
            {
                failures += $"\n... 还有 {result.Failures.Count - 8} 项失败";
            }

            MessageBox.Show(
                this,
                $"部分格式设置失败：{result.Succeeded}/{result.Total} 成功。\n\n{failures}",
                "高级一键默认",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("AdvancedDefaultAssociations", "高级默认应用设置失败。", ex);
            MessageBox.Show(this, $"高级默认应用设置失败：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UnregisterFileAssociationsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociationService.Unregister();
            if (ShowOperationNotificationsCheckBox.IsChecked == true)
            {
                MessageBox.Show(this, $"已取消 {AppInfo.Name} 的常见图片格式打开方式关联。", "文件关联", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("UnregisterFileAssociations", "取消文件关联失败。", ex);
            MessageBox.Show(this, $"取消文件关联失败：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static void SelectComboBoxValue(ComboBox comboBox, int value, int fallback)
    {
        comboBox.SelectedValue = value.ToString(CultureInfo.InvariantCulture);
        if (comboBox.SelectedIndex < 0)
        {
            comboBox.SelectedValue = fallback.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static int GetSelectedMegabytes(ComboBox comboBox, int fallback)
    {
        if (comboBox.SelectedValue is string selectedValue
            && int.TryParse(selectedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            return value;
        }

        return fallback;
    }

    private static string FormatMegabytes(int megabytes)
    {
        return megabytes >= 1024 && megabytes % 1024 == 0
            ? $"{megabytes / 1024} GB"
            : $"{megabytes} MB";
    }

    private async Task RefreshThumbnailDiskCacheInfoAsync()
    {
        _thumbnailCacheInfoCts?.Cancel();
        _thumbnailCacheInfoCts?.Dispose();
        var cts = new CancellationTokenSource();
        _thumbnailCacheInfoCts = cts;
        ThumbnailDiskCachePathText.Text = $"保存位置：{ThumbnailDiskCache.DefaultCacheFolder}\n正在统计当前占用…";
        try
        {
            var statistics = await new ThumbnailDiskCache().GetStatisticsAsync(cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(_thumbnailCacheInfoCts, cts))
            {
                ThumbnailDiskCachePathText.Text = $"保存位置：{ThumbnailDiskCache.DefaultCacheFolder}\n当前占用：{FormatFileSize(statistics.TotalBytes)}，{statistics.FileCount:N0} 项";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("RefreshThumbnailDiskCacheInfo", "统计缩略图缓存失败。", ex);
            ThumbnailDiskCachePathText.Text = $"保存位置：{ThumbnailDiskCache.DefaultCacheFolder}\n当前占用统计失败";
        }
        finally
        {
            if (ReferenceEquals(_thumbnailCacheInfoCts, cts))
            {
                _thumbnailCacheInfoCts = null;
            }

            cts.Dispose();
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} B";
        }

        var units = new[] { "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unitIndex = -1;
        do
        {
            value /= 1024;
            unitIndex++;
        }
        while (value >= 1024 && unitIndex < units.Length - 1);

        return $"{value:0.##} {units[unitIndex]}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingAction is null)
        {
            if (e.Key == Key.Delete && ShortcutGrid.IsKeyboardFocusWithin)
            {
                DeleteButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            _capturingAction = null;
            _shortcutBeingReplaced = null;
            StatusText.Text = "已取消修改。";
            e.Handled = true;
            return;
        }

        var shortcut = KeyboardShortcut.FromKeyEvent(e);
        if (!KeyboardShortcut.IsValidInput(shortcut))
        {
            StatusText.Text = "不能只使用 Ctrl、Shift、Alt 或 Win。";
            e.Handled = true;
            return;
        }

        var action = _capturingAction.Value;
        if (_working.FindConflict(action, shortcut) is { } conflict)
        {
            StatusText.Text = $"快捷键 {shortcut.ToDisplayText()} 已被“{ShortcutSettings.ActionNames[conflict]}”使用。";
            e.Handled = true;
            return;
        }

        if (IsDuplicateInCurrentAction(action, shortcut))
        {
            StatusText.Text = $"“{ShortcutSettings.ActionNames[action]}”已经有 {shortcut.ToDisplayText()}。";
            e.Handled = true;
            return;
        }

        _working.ReplaceShortcut(action, _shortcutBeingReplaced, shortcut);
        _capturingAction = null;
        _shortcutBeingReplaced = null;
        RefreshRows();
        StatusText.Text = "已修改，点击保存后生效。";
        e.Handled = true;
    }

    private void UpdateButtons()
    {
        var hasRow = ShortcutGrid.SelectedItem is ShortcutDisplayRow;
        var hasShortcut = ShortcutGrid.SelectedItem is ShortcutDisplayRow { Shortcut: not null };
        EditButton.IsEnabled = hasRow;
        AddButton.IsEnabled = hasRow;
        DeleteButton.IsEnabled = hasShortcut;
    }

    private bool IsDuplicateInCurrentAction(ShortcutAction action, KeyboardShortcut shortcut)
    {
        return _working.GetShortcuts(action).Any(existing =>
            existing.Matches(shortcut.Key, shortcut.Modifiers) &&
            !ShortcutsEqual(existing, _shortcutBeingReplaced));
    }

    private SavedFileOpenBehavior GetSelectedSavedFileOpenBehavior()
    {
        var selectedValue = SavedFileOpenBehaviorComboBox.SelectedValue as string;
        return Enum.TryParse<SavedFileOpenBehavior>(selectedValue, out var behavior)
            ? behavior
            : SavedFileOpenBehavior.None;
    }

    private QuickSearchMode GetSelectedQuickSearchMode()
    {
        var selectedValue = QuickSearchModeComboBox.SelectedValue as string;
        return Enum.TryParse<QuickSearchMode>(selectedValue, out var mode)
            ? mode
            : QuickSearchMode.Index;
    }

    private ZoomIndicatorDisplayMode GetSelectedZoomIndicatorDisplayMode()
    {
        var selectedValue = ZoomIndicatorDisplayModeComboBox.SelectedValue as string;
        return Enum.TryParse<ZoomIndicatorDisplayMode>(selectedValue, out var mode)
            ? mode
            : ZoomIndicatorDisplayMode.Percentage;
    }

    private static string GetAppVersion()
    {
        var assembly = typeof(ShortcutSettingsWindow).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static bool ShortcutsEqual(KeyboardShortcut? left, KeyboardShortcut? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Matches(right.Key, right.Modifiers);
    }


    private static int GetShortcutActionSortIndex(ShortcutAction action)
    {
        return action switch
        {
            ShortcutAction.PreviousImage => 10,
            ShortcutAction.NextImage => 11,
            ShortcutAction.CycleSortMode => 12,
            ShortcutAction.ToggleAnimationPlayback => 13,
            ShortcutAction.RestartAnimation => 14,
            ShortcutAction.RotateLeft => 15,
            ShortcutAction.RotateRight => 16,
            ShortcutAction.ToggleFitActual => 30,
            ShortcutAction.FitWindow => 31,
            ShortcutAction.ActualSize => 32,
            ShortcutAction.ZoomIn => 33,
            ShortcutAction.ZoomOut => 34,
            ShortcutAction.ToggleFullScreen => 35,
            ShortcutAction.ToggleInfo => 36,
            ShortcutAction.ToggleThumbnailSidebar => 37,
            ShortcutAction.ToggleThumbnailColumns => 38,
            ShortcutAction.ShowQuickSearch => 39,
            ShortcutAction.OpenImage => 50,
            ShortcutAction.OpenFolder => 51,
            ShortcutAction.OpenContainingFolder => 52,
            ShortcutAction.CopyFile => 53,
            ShortcutAction.CopyPath => 54,
            ShortcutAction.CopyName => 55,
            ShortcutAction.ToggleFavorite => 56,
            ShortcutAction.ToggleFavoritesView => 57,
            ShortcutAction.SaveVideoCover => 58,
            ShortcutAction.DeleteImage => 59,
            ShortcutAction.BatchDeleteCurrentFolder => 60,
            ShortcutAction.CropImage => 80,
            ShortcutAction.CircleCropImage => 81,
            ShortcutAction.CompressImage => 82,
            ShortcutAction.OpenBatchCompressTools => 83,
            ShortcutAction.SaveCrop => 100,
            ShortcutAction.ShowShortcutSettings => 120,
            ShortcutAction.CancelOrClose => 121,
            _ => 1000 + (int)action,
        };
    }
    private sealed record ShortcutDisplayRow(ShortcutRow Source, string CategoryText)
    {
        public ShortcutAction Action => Source.Action;

        public string ActionName => Source.ActionName;

        public KeyboardShortcut? Shortcut => Source.Shortcut;

        public string ShortcutText => Source.ShortcutText;
    }

    private sealed record ShortcutRow(ShortcutAction Action, string Category, string ActionName, KeyboardShortcut? Shortcut)
    {
        public string ShortcutText => Shortcut?.ToDisplayText() ?? "未设置";
    }
}
