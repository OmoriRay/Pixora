using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Pixora.Models;
using Pixora.Services;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Pixora;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const double MinScale = 0.02;
    private const double MaxScale = 32.0;
    private const long MaxCropPixelCount = 12_000_000;
    private const double MinCropSelectionSize = 2.0;
    private const double CropHandleSize = 12.0;
    private const double CropHandleHitRadius = 10.0;
    private const int ShellSelectItemFlags = 0x1 | 0x4 | 0x8 | 0x10;
    private const int ThumbnailPreloadRadius = 48;
    private const int ThumbnailVisibleBufferRows = 4;
    private const int MaxThumbnailCacheItems = 512;
    private const int MainImageCacheMegabytes = ViewerSettings.DefaultMainImageCacheMegabytes;
    private const int DisplayPreviewCacheMegabytes = ViewerSettings.DefaultDisplayPreviewCacheMegabytes;
    private const int ThumbnailDecodeWidth = 168;
    private const int ThumbnailDecodeHeight = 104;
    private const int SystemParametersInfoSetDesktopWallpaper = 0x0014;
    private const int SystemParametersInfoUpdateIniFile = 0x01;
    private const int SystemParametersInfoSendChange = 0x02;
    private const int SystemParametersInfoWallpaperFlags = SystemParametersInfoUpdateIniFile | SystemParametersInfoSendChange;
    private const double DisplayPreviewScale = 1.35;
    private const int DisplayPreviewMinimumWidth = 1280;
    private const int DisplayPreviewMinimumHeight = 720;
    private const int DisplayPreviewMaximumSide = 2560;
    private const double QuickSearchEdgePadding = 8;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly TimeSpan FullScreenTransitionFadeInDuration = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan FullScreenTransitionHoldDuration = TimeSpan.FromMilliseconds(85);
    private static readonly TimeSpan FullScreenTransitionFadeOutDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan InitialQualityRestoreDelay = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan InteractiveQualityRestoreDelay = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan IdleFullResolutionLoadDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan ThumbnailScrollIdleDelay = TimeSpan.FromMilliseconds(180);
    private static readonly ImageSortMode[] SortModeCycle =
    [
        ImageSortMode.NameNatural,
        ImageSortMode.NameNaturalDescending,
        ImageSortMode.LastWriteTimeNewest,
        ImageSortMode.LastWriteTimeOldest,
        ImageSortMode.FileSizeLargest,
        ImageSortMode.FileSizeSmallest,
    ];

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(int uiAction, int uiParam, string pvParam, int fWinIni);

    private readonly ImageCatalog _catalog = new();
    private readonly ImageCache _cache = ImageCache.FromMegabytes(MainImageCacheMegabytes);
    private readonly BitmapSourceMemoryCache _displayPreviewCache = BitmapSourceMemoryCache.FromMegabytes(DisplayPreviewCacheMegabytes);
    private readonly MemoryCacheCoordinator _memoryCacheCoordinator;
    private readonly ShortcutSettings _shortcutSettings = ShortcutSettings.Load();
    private readonly ViewerSettings _viewerSettings = ViewerSettings.Load();
    private readonly FavoriteStore _favorites = FavoriteStore.Load();
    private readonly ThumbnailDiskCache _thumbnailDiskCache = new();
    private readonly ThumbnailImageLoader _thumbnailImageLoader;
    private readonly QuickSearchIndex _quickSearchIndex = new();
    private readonly QuickSearchInteractionState _quickSearchInteractionState = new();
    private readonly FolderChangeMonitor _folderChangeMonitor = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _boundaryToastTimer;
    private readonly DispatcherTimer _zoomIndicatorTimer;
    private readonly DispatcherTimer _qualityRestoreTimer;
    private readonly DispatcherTimer _idleFullResolutionTimer;
    private readonly DispatcherTimer _quickSearchDebounceTimer;
    private readonly DispatcherTimer _thumbnailScrollIdleTimer;
    private VirtualizedRowCollection<ThumbnailItem> _thumbnailRows = VirtualizedRowCollection<ThumbnailItem>.Empty;
    private readonly Dictionary<string, BitmapSource> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThumbnailItem> _thumbnailItemByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _thumbnailCacheOrder = new();
    private readonly HashSet<string> _queuedThumbnailPaths = new(StringComparer.OrdinalIgnoreCase);
    private SemaphoreSlim _thumbnailLoadSemaphore = new(2, 2);
    private readonly object _thumbnailQueueLock = new();
    private LazyIndexedList<ThumbnailItem> _thumbnailItems = LazyIndexedList<ThumbnailItem>.Empty;
    private IndexedProjectionList<ThumbnailItem>? _thumbnailSearchItems;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _thumbnailViewportCts;
    private CancellationTokenSource? _preloadCts;
    private CancellationTokenSource? _idleFullResolutionCts;
    private CancellationTokenSource? _catalogCompletionCts;
    private CancellationTokenSource? _folderLoadCts;
    private CancellationTokenSource? _folderWatcherRefreshCts;
    private CancellationTokenSource? _quickSearchCts;
    private ScrollViewer? _thumbnailScrollViewer;
    private ImageDocument? _currentDocument;
    private bool _showInfo = true;
    private bool _showThumbnailSidebar;
    private bool _useDoubleThumbnailColumns = true;
    private bool _isFitMode = true;
    private bool _fitAllowsUpscale;
    private bool _isDragging;
    private bool _isCropMode;
    private bool _isCropDragging;
    private bool _isSavingCrop;
    private bool _isClosing;
    private bool _isFullScreen;
    private bool _isFullScreenTransitioning;
    private bool _isFavoritesView;
    private bool _isAnimationPaused;
    private bool _isPreviewDisplay;
    private bool _isQuickSearchVisible;
    private bool _isQuickSearchDragging;
    private bool _isThumbnailScrollActive;
    private bool _thumbnailRowsNeedRefresh;
    private bool _thumbnailItemsNeedRefresh;
    private QuickSearchMode _activeQuickSearchMode;
    private int _animationFrameIndex;
    private int _thumbnailGeneration;
    private int _thumbnailViewportGeneration;
    private int _quickSearchIndexGeneration = -1;
    private int _navigationDirection;
    private int _mainPreloadForwardRadius = 3;
    private int _mainPreloadOppositeRadius = 1;
    private int _thumbnailLoadConcurrency = 2;
    private int _largeImagePreviewMaximumSide = LargeImagePolicy.DefaultPreviewMaximumSide;
    private int _catalogCompletionGeneration;
    private int _folderLoadGeneration;
    private int _scheduledFitGeneration;
    private int _zoomIndicatorGeneration;
    private int _thumbnailVisibleStartIndex;
    private int _thumbnailVisibleEndIndex = -1;
    private int? _quickSearchTargetIndex;
    private QuickSearchIndexResult? _quickSearchResult;
    private int _rotationDegrees;
    private double _scale = 1.0;
    private TimeSpan _lastImageLoadDuration;
    private Point _dragStartPoint;
    private Point _dragStartOffset;
    private Point _quickSearchDragStartPoint;
    private Point _quickSearchDragStartOffset;
    private Point _cropStartPoint;
    private Rect _cropStartRect = Rect.Empty;
    private Rect _cropSelectionRect = Rect.Empty;
    private CropDragMode _cropDragMode = CropDragMode.None;
    private CropShape _cropShape = CropShape.Rectangle;
    private WindowState _restoreWindowState;
    private WindowStyle _restoreWindowStyle;
    private ResizeMode _restoreResizeMode;
    private bool _restoreTopmost;
    private double _restoreLeft;
    private double _restoreTop;
    private double _restoreWidth;
    private double _restoreHeight;
    private string _lastInlineErrorMessage = string.Empty;
    private string? _previewDisplayPath;
    private BitmapSource? _previewDisplayBitmap;
    private ThumbnailItem? _selectedThumbnailItem;
    private readonly string? _startupPath;

    public double ThumbnailImageHeight => _useDoubleThumbnailColumns ? 64 : 104;

    public double ThumbnailItemWidth => _useDoubleThumbnailColumns ? 77 : 162;

    public Thickness ThumbnailItemPadding => _useDoubleThumbnailColumns
        ? new Thickness(3.25, 4, 3.25, 4)
        : new Thickness(4);

    public Thickness ThumbnailItemMargin => _useDoubleThumbnailColumns
        ? new Thickness(3)
        : new Thickness(0, 3, 0, 3);

    public int ThumbnailColumnCount => _useDoubleThumbnailColumns ? 2 : 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [Flags]
    private enum CropDragMode
    {
        None = 0,
        Create = 1,
        Move = 2,
        Left = 4,
        Top = 8,
        Right = 16,
        Bottom = 32,
    }

    private enum CropShape
    {
        Rectangle,
        Circle,
    }

    public MainWindow(string? startupPath = null)
    {
        InitializeComponent();
        _startupPath = startupPath;
        _memoryCacheCoordinator = new MemoryCacheCoordinator(_cache, _displayPreviewCache);
        _thumbnailImageLoader = new ThumbnailImageLoader(_thumbnailDiskCache);
        _folderChangeMonitor.BatchReady += FolderChangeMonitor_BatchReady;
        _catalog.SortMode = _viewerSettings.SortMode;
        _showThumbnailSidebar = _viewerSettings.ShowThumbnailSidebar;
        _useDoubleThumbnailColumns = _viewerSettings.UseDoubleThumbnailColumns;
        ApplyRuntimeViewerSettings();
        ThumbnailList.ItemsSource = _thumbnailRows;
        ThumbnailList.DataContext = this;
        ThumbnailList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ThumbnailList_ScrollChanged));
        ThumbnailList.Loaded += (_, _) => QueueVisibleThumbnails();
        UpdateThumbnailSidebarVisibility();

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render);
        _animationTimer.Tick += AnimationTimer_Tick;
        _qualityRestoreTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = InteractiveQualityRestoreDelay,
        };
        _qualityRestoreTimer.Tick += QualityRestoreTimer_Tick;
        _boundaryToastTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(2.4),
        };
        _boundaryToastTimer.Tick += BoundaryToastTimer_Tick;
        _zoomIndicatorTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(1600),
        };
        _zoomIndicatorTimer.Tick += ZoomIndicatorTimer_Tick;
        _idleFullResolutionTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = IdleFullResolutionLoadDelay,
        };
        _idleFullResolutionTimer.Tick += IdleFullResolutionTimer_Tick;
        _quickSearchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _quickSearchDebounceTimer.Tick += QuickSearchDebounceTimer_Tick;
        _thumbnailScrollIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ThumbnailScrollIdleDelay,
        };
        _thumbnailScrollIdleTimer.Tick += ThumbnailScrollIdleTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySavedMainWindowPlacement();
        Focus();
        UpdateInfoPanel();
        UpdateCommands();

        var startupPath = _startupPath;

        if (startupPath is not null)
        {
            await OpenPathAsync(startupPath);
            ShowQuickSearchOnStartupIfNeeded();
            return;
        }

        if (_viewerSettings.OpenLastFolderOnStartup
            && TryGetRememberedFolder(out var rememberedFolder))
        {
            await OpenFolderAsync(rememberedFolder);
        }

        ShowQuickSearchOnStartupIfNeeded();
    }

    private void ShowQuickSearchOnStartupIfNeeded()
    {
        if (!_viewerSettings.ShowQuickSearchOnStartup)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(() => ShowQuickSearch(rememberForStartup: false)),
            DispatcherPriority.Loaded);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveMainWindowPlacement();
        _isClosing = true;
        StopAnimation();
        _qualityRestoreTimer.Stop();
        _quickSearchDebounceTimer.Stop();
        _thumbnailScrollIdleTimer.Stop();
        _quickSearchCts?.Cancel();
        _quickSearchCts?.Dispose();
        _quickSearchCts = null;
        _boundaryToastTimer.Stop();
        _zoomIndicatorTimer.Stop();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        CancelCatalogCompletion();
        CancelFolderLoad();
        StopFolderWatcher();
        _folderChangeMonitor.BatchReady -= FolderChangeMonitor_BatchReady;
        _folderChangeMonitor.Dispose();
        CancelIdleFullResolutionLoad();
        CancelPreload();
    }

    private void ApplySavedMainWindowPlacement()
    {
        if (_viewerSettings.RememberMainWindowPlacement
            && _viewerSettings.MainWindowWidth >= MinWidth
            && _viewerSettings.MainWindowHeight >= MinHeight)
        {
            Width = _viewerSettings.MainWindowWidth;
            Height = _viewerSettings.MainWindowHeight;

            if (_viewerSettings.MainWindowLeft is { } left
                && _viewerSettings.MainWindowTop is { } top)
            {
                var savedBounds = new Rect(left, top, Width, Height);
                var virtualScreen = new Rect(
                    SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth,
                    SystemParameters.VirtualScreenHeight);
                if (savedBounds.IntersectsWith(virtualScreen))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = left;
                    Top = top;
                }
            }
        }

        if (_viewerSettings.StartMainWindowMaximized
            || (_viewerSettings.RememberMainWindowPlacement && _viewerSettings.MainWindowMaximized))
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveMainWindowPlacement()
    {
        if (!_viewerSettings.RememberMainWindowPlacement || _isFullScreen)
        {
            return;
        }

        try
        {
            var bounds = RestoreBounds;
            if (double.IsFinite(bounds.Width) && bounds.Width >= MinWidth)
            {
                _viewerSettings.MainWindowWidth = bounds.Width;
            }

            if (double.IsFinite(bounds.Height) && bounds.Height >= MinHeight)
            {
                _viewerSettings.MainWindowHeight = bounds.Height;
            }

            if (double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
            {
                _viewerSettings.MainWindowLeft = bounds.Left;
                _viewerSettings.MainWindowTop = bounds.Top;
            }

            _viewerSettings.MainWindowMaximized = WindowState == WindowState.Maximized;
            _viewerSettings.Save();
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("SaveMainWindowPlacement", "保存主窗口位置失败。", ex);
        }
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                if (!ImageCatalog.IsSupportedMediaPath(fullPath))
                {
                    ShowUserError($"暂不支持该文件格式：{Path.GetExtension(fullPath)}");
                    return;
                }

                _isFavoritesView = false;
                await OpenSingleFileAsync(fullPath, rememberFolder: true);
                return;
            }

            if (Directory.Exists(fullPath))
            {
                await OpenFolderAsync(fullPath);
                return;
            }

            ShowInlineError($"路径不存在或已被移动：\n{fullPath}");
        }
        catch (Exception ex)
        {
            ShowUserError($"无法打开路径：\n{path}\n\n{FriendlyException(ex)}", ex);
        }
    }

    private async Task OpenFolderAsync(string folder)
    {
        CancellationTokenSource? cts = null;
        var generation = 0;
        try
        {
            var fullFolder = Path.GetFullPath(folder);
            StopFolderWatcher();
            CancelCatalogCompletion();
            CancelFolderLoad();
            _isFavoritesView = false;
            cts = new CancellationTokenSource();
            generation = ++_folderLoadGeneration;
            _folderLoadCts = cts;
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingText.Text = "正在扫描目录...";
            CancelFolderLoadButton.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;

            var stopwatch = Stopwatch.StartNew();
            var progress = new Progress<int>(count =>
            {
                if (!cts.IsCancellationRequested && generation == _folderLoadGeneration)
                {
                    LoadingText.Text = $"正在扫描目录：已发现 {count:N0} 项";
                }
            });
            var loadedCatalog = await MediaCatalogLoader.LoadFolderAsync(
                fullFolder,
                _catalog.SortMode,
                cts.Token,
                progress);
            if (cts.IsCancellationRequested
                || generation != _folderLoadGeneration
                || !ReferenceEquals(_folderLoadCts, cts))
            {
                return;
            }

            _catalog.LoadFromCatalog(loadedCatalog);
            RefreshThumbnailItems();
            if (_catalog.Count == 0)
            {
                ClearImage();
                ShowInlineError($"文件夹中没有支持的图片或视频：\n{fullFolder}");
                return;
            }

            EnterDefaultFitMode();
            await LoadCurrentAsync();
            RememberOpenedFolder(fullFolder);
            StartFolderWatcher(fullFolder);
            ShowBoundaryToast($"目录扫描完成：{_catalog.Count:N0} 项，{FormatElapsedDuration(stopwatch.Elapsed)}");
        }
        catch (OperationCanceledException)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (generation != 0 && generation != _folderLoadGeneration)
            {
                return;
            }

            ShowUserError($"无法打开目录：\n{folder}\n\n{FriendlyException(ex)}", ex);
        }
        finally
        {
            if (cts is not null && ReferenceEquals(_folderLoadCts, cts))
            {
                _folderLoadCts = null;
            }

            cts?.Dispose();
            CancelFolderLoadButton.Visibility = Visibility.Collapsed;
        }
    }

    internal async Task OpenExternalPathAsync(string path)
    {
        await OpenPathAsync(path);
    }

    internal void ActivateFromExternalRequest()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async Task OpenSingleFileAsync(string fullPath, bool rememberFolder)
    {
        CancelFolderLoad();
        CancelCatalogCompletion();
        _catalog.LoadSingleFile(fullPath);
        RefreshThumbnailItems();
        EnterDefaultFitMode();
        await LoadCurrentAsync();
        if (rememberFolder)
        {
            RememberOpenedFolder(Path.GetDirectoryName(fullPath));
        }

        StartCatalogCompletion(fullPath);
        StartFolderWatcher(Path.GetDirectoryName(fullPath));
    }

    private void StartCatalogCompletion(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        CancelCatalogCompletion();
        var cts = new CancellationTokenSource();
        var generation = ++_catalogCompletionGeneration;
        var sortMode = _catalog.SortMode;
        _catalogCompletionCts = cts;
        CatalogScanPanel.Visibility = Visibility.Visible;
        CatalogScanText.Text = "正在补齐目录...";
        _ = CompleteCatalogFromFolderAsync(fullPath, sortMode, generation, cts);
    }

    private async Task CompleteCatalogFromFolderAsync(
        string fullPath,
        ImageSortMode sortMode,
        int generation,
        CancellationTokenSource cts)
    {
        try
        {
            var token = cts.Token;
            var stopwatch = Stopwatch.StartNew();
            var progress = new Progress<int>(count =>
            {
                if (!cts.IsCancellationRequested && generation == _catalogCompletionGeneration)
                {
                    CatalogScanText.Text = $"正在补齐目录：{count:N0} 项";
                }
            });
            var completedCatalog = await MediaCatalogLoader.LoadFromFileAsync(
                fullPath,
                sortMode,
                token,
                progress);

            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested
                    || generation != _catalogCompletionGeneration
                    || !ReferenceEquals(_catalogCompletionCts, cts)
                    || _isFavoritesView
                    || !_catalog.IsSingleFileCatalog
                    || !string.Equals(_catalog.CurrentPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _catalog.LoadFromCatalog(completedCatalog);
                RefreshThumbnailItems();
                UpdateInfoPanel();
                UpdateWindowTitle(_catalog.CurrentPath);
                UpdateCommands();
                PreloadAroundCurrent();
                StartFolderWatcher(completedCatalog.SourceFolder);
                if (stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250) || completedCatalog.Count >= 1_000)
                {
                    ShowBoundaryToast($"目录索引已补齐：{completedCatalog.Count:N0} 项，{FormatElapsedDuration(stopwatch.Elapsed)}");
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Directory completion is only an optimization; the current file stays open if it fails.
        }
        finally
        {
            if (ReferenceEquals(_catalogCompletionCts, cts))
            {
                _catalogCompletionCts = null;
                CatalogScanPanel.Visibility = Visibility.Collapsed;
            }

            cts.Dispose();
        }
    }

    private void CancelCatalogCompletion()
    {
        _catalogCompletionGeneration++;
        var cts = _catalogCompletionCts;
        _catalogCompletionCts = null;
        try
        {
            cts?.Cancel();
            CatalogScanPanel.Visibility = Visibility.Collapsed;
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelFolderLoad()
    {
        _folderLoadGeneration++;
        var cts = _folderLoadCts;
        _folderLoadCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StartFolderWatcher(string? folder)
    {
        StopFolderWatcher();
        if (!_viewerSettings.WatchFolderChanges
            || _isFavoritesView
            || string.IsNullOrWhiteSpace(folder)
            || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            _folderChangeMonitor.Start(folder);
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("StartFolderWatcher", "启动目录监视失败。", ex);
            StopFolderWatcher();
        }
    }

    private void StopFolderWatcher()
    {
        _folderWatcherRefreshCts?.Cancel();
        _folderWatcherRefreshCts?.Dispose();
        _folderWatcherRefreshCts = null;
        _folderChangeMonitor.Stop();
    }

    private void FolderChangeMonitor_BatchReady(object? sender, FolderChangeBatch batch)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(async () =>
        {
            if (_isClosing || !_viewerSettings.WatchFolderChanges)
            {
                return;
            }

            try
            {
                await ApplyFolderChangeBatchAsync(batch);
            }
            catch (Exception ex)
            {
                ErrorLog.WriteException("ApplyFolderChangeBatch", "增量更新目录失败，准备回退完整刷新。", ex);
                await RefreshCatalogFromFolderWatcherAsync();
            }
        }, DispatcherPriority.Background);
    }

    private async Task ApplyFolderChangeBatchAsync(FolderChangeBatch batch)
    {
        if (batch.RequiresFullRefresh || batch.Changes.Count > 512)
        {
            await RefreshCatalogFromFolderWatcherAsync();
            return;
        }

        var folder = _catalog.SourceFolder;
        if (_isFavoritesView || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        var currentPath = _catalog.CurrentPath;
        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedOrUpdatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cacheInvalidationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? preferredCurrentPath = null;

        foreach (var change in batch.Changes)
        {
            switch (change.Kind)
            {
                case FolderChangeKind.Created:
                    addedOrUpdatedPaths.Add(change.Path);
                    cacheInvalidationPaths.Add(change.Path);
                    break;
                case FolderChangeKind.Deleted:
                    removedPaths.Add(change.Path);
                    cacheInvalidationPaths.Add(change.Path);
                    break;
                case FolderChangeKind.Changed:
                    addedOrUpdatedPaths.Add(change.Path);
                    cacheInvalidationPaths.Add(change.Path);
                    break;
                case FolderChangeKind.Renamed:
                    if (!string.IsNullOrWhiteSpace(change.OldPath))
                    {
                        removedPaths.Add(change.OldPath);
                        cacheInvalidationPaths.Add(change.OldPath);
                        if (string.Equals(currentPath, change.OldPath, StringComparison.OrdinalIgnoreCase))
                        {
                            preferredCurrentPath = change.Path;
                        }
                    }

                    addedOrUpdatedPaths.Add(change.Path);
                    cacheInvalidationPaths.Add(change.Path);
                    break;
            }
        }

        var mutation = _catalog.ApplyPathChanges(removedPaths, addedOrUpdatedPaths, preferredCurrentPath);
        if (!mutation.Changed)
        {
            return;
        }

        foreach (var path in cacheInvalidationPaths)
        {
            RemoveCachedMedia(path);
        }

        var currentNeedsReload = mutation.CurrentPath is not null
            && addedOrUpdatedPaths.Contains(mutation.CurrentPath);
        RefreshThumbnailItems();
        UpdateInfoPanel();
        UpdateWindowTitle(_catalog.CurrentPath);
        UpdateCommands();

        if (_catalog.Count == 0)
        {
            ClearImage();
        }
        else if (mutation.CurrentPathChanged || currentNeedsReload)
        {
            PrepareForNavigation();
            await LoadCurrentAsync();
        }

        ShowBoundaryToast((mutation.AddedCount, mutation.RemovedCount, mutation.UpdatedCount) switch
        {
            (> 0, 0, 0) => $"目录已更新：新增 {mutation.AddedCount:N0} 项",
            (0, > 0, 0) => $"目录已更新：减少 {mutation.RemovedCount:N0} 项",
            _ => "目录内容已更新",
        });
    }

    private async Task RefreshCatalogFromFolderWatcherAsync()
    {
        var folder = _catalog.SourceFolder;
        if (_isFavoritesView || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        _folderWatcherRefreshCts?.Cancel();
        _folderWatcherRefreshCts?.Dispose();
        var cts = new CancellationTokenSource();
        _folderWatcherRefreshCts = cts;
        var currentPath = _catalog.CurrentPath;
        var previousIndex = _catalog.Index;
        var previousCount = _catalog.Count;
        var currentFileChanged = currentPath is not null
            && _currentDocument is not null
            && File.Exists(currentPath)
            && (File.GetLastWriteTime(currentPath) != _currentDocument.LastWriteTime
                || new FileInfo(currentPath).Length != _currentDocument.FileSize);

        try
        {
            var refreshed = await MediaCatalogLoader.LoadFolderAsync(folder, _catalog.SortMode, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_folderWatcherRefreshCts, cts))
            {
                return;
            }

            var keptCurrent = currentPath is not null && refreshed.MoveTo(currentPath);
            if (!keptCurrent && refreshed.Count > 0)
            {
                refreshed.MoveToIndex(Math.Clamp(previousIndex, 0, refreshed.Count - 1));
            }

            _catalog.LoadFromCatalog(refreshed);
            _thumbnailCache.Clear();
            _thumbnailCacheOrder.Clear();
            RefreshThumbnailItems();
            UpdateInfoPanel();
            UpdateWindowTitle(_catalog.CurrentPath);
            UpdateCommands();

            if (currentFileChanged && keptCurrent && currentPath is not null)
            {
                RemoveCachedMedia(currentPath);
                await LoadCurrentAsync();
            }
            else if (!keptCurrent && _catalog.Count > 0)
            {
                PrepareForNavigation();
                await LoadCurrentAsync();
            }
            else if (_catalog.Count == 0)
            {
                ClearImage();
            }

            var delta = _catalog.Count - previousCount;
            ShowBoundaryToast(delta switch
            {
                > 0 => $"目录已更新：新增 {delta:N0} 项",
                < 0 => $"目录已更新：减少 {-delta:N0} 项",
                _ => "目录内容已刷新",
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("RefreshFolderWatcher", "自动刷新目录失败。", ex);
        }
        finally
        {
            if (ReferenceEquals(_folderWatcherRefreshCts, cts))
            {
                _folderWatcherRefreshCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task LoadCurrentAsync()
    {
        var path = _catalog.CurrentPath;
        if (path is null)
        {
            ClearImage();
            return;
        }

        ExitCropMode();
        CancelIdleFullResolutionLoad();
        CancelPreload();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        _currentDocument = null;
        _rotationDegrees = 0;
        ClearDisplayPreviewState();
        StopAnimation();
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "正在打开...";
        CancelFolderLoadButton.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        UpdateInfoPanel();
        UpdateWindowTitle(path);
        UpdateCommands();
        _lastImageLoadDuration = TimeSpan.Zero;
        var loadStopwatch = Stopwatch.StartNew();

        try
        {
            var document = await LoadInitialDocumentAsync(path, cts.Token);
            if (!ReferenceEquals(_loadCts, cts) || cts.IsCancellationRequested)
            {
                return;
            }

            _currentDocument = document;
            _lastInlineErrorMessage = string.Empty;
            _rotationDegrees = 0;
            StartAnimation(document);
            loadStopwatch.Stop();
            _lastImageLoadDuration = loadStopwatch.Elapsed;

            UpdateVideoBadge();
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;

            if (_isFitMode)
            {
                FitToWindow();
                ScheduleFitToWindowAfterLayout(document);
            }
            else
            {
                await CenterAtScaleAsync(_scale);
            }

            FadeInBitmapView();
            UpdateInfoPanel();
            UpdateWindowTitle(path);
            UpdateCommands();
            UpdateThumbnailSelection();
            ShowBoundaryToastIfNeeded();
            PreloadAroundCurrent();
            ScheduleIdleFullResolutionLoad();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_loadCts, cts))
            {
                return;
            }

            _currentDocument = null;
            _rotationDegrees = 0;
            StopAnimation();
            HideBoundaryToast();
            UpdateVideoBadge();
            BitmapView.Source = null;
            ClearDisplayPreviewState();
            LoadingPanel.Visibility = Visibility.Collapsed;
            ShowInlineError($"无法打开该文件：\n{path}\n\n{FriendlyException(ex)}", ex);
            UpdateInfoPanel();
            UpdateCommands();
            UpdateThumbnailSelection();
            PreloadAroundCurrent();
            ScheduleIdleFullResolutionLoad();
        }
    }

    private async Task<ImageDocument> LoadInitialDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (ShouldUseDisplayPreview(path))
        {
            try
            {
                var previewDocument = await TryLoadDisplayPreviewDocumentAsync(path, cancellationToken);
                if (previewDocument is not null)
                {
                    return previewDocument;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        return await LoadDocumentAsync(path, cancellationToken);
    }

    private Task<ImageDocument> LoadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            RemoveCachedMedia(path);
            throw new FileNotFoundException("文件不存在。", path);
        }

        if (_cache.TryGet(path, out var cached) && cached is not null)
        {
            return Task.FromResult(cached);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ImageCatalog.IsSupportedVideoPath(path))
            {
                var videoDocument = VideoThumbnailLoader.LoadDocument(path, cancellationToken);
                _cache.Add(videoDocument);
                return videoDocument;
            }

            var document = ImageLoader.Load(path, cancellationToken);
            if (!document.IsAnimated)
            {
                _cache.Add(document);
            }

            return document;
        }, cancellationToken);
    }

    private void RemoveCachedMedia(string path)
    {
        _cache.Remove(path);
        _displayPreviewCache.RemoveByPath(path);
        _thumbnailCache.Remove(path);
    }

    private void ApplyRuntimeViewerSettings()
    {
        var mainImageCacheMegabytes = _viewerSettings.UseAutomaticCacheSizing
            ? ViewerSettings.AutomaticMainImageCacheCapMegabytes
            : NormalizeMegabytes(_viewerSettings.MainImageCacheMegabytes, ViewerSettings.DefaultMainImageCacheMegabytes);
        var displayPreviewCacheMegabytes = _viewerSettings.UseAutomaticCacheSizing
            ? ViewerSettings.AutomaticDisplayPreviewCacheCapMegabytes
            : NormalizeMegabytes(_viewerSettings.DisplayPreviewCacheMegabytes, ViewerSettings.DefaultDisplayPreviewCacheMegabytes);
        var performanceProfile = _memoryCacheCoordinator.ApplyConfiguredPerformance(
            mainImageCacheMegabytes,
            displayPreviewCacheMegabytes,
            _viewerSettings.UseAutomaticCacheSizing);
        _mainPreloadForwardRadius = performanceProfile.MainPreloadForwardRadius;
        _mainPreloadOppositeRadius = performanceProfile.MainPreloadOppositeRadius;
        _largeImagePreviewMaximumSide = LargeImagePolicy.ResolvePreviewMaximumSide(
            performanceProfile.CacheBudgets.PreviewMegabytes);
        ConfigureThumbnailLoadConcurrency(performanceProfile.ThumbnailLoadConcurrency);
        _thumbnailDiskCache.SetMaxMegabytes(NormalizeMegabytes(
            _viewerSettings.ThumbnailDiskCacheMegabytes,
            ViewerSettings.DefaultThumbnailDiskCacheMegabytes));
        _ = _thumbnailDiskCache.TrimToBytesAsync(_thumbnailDiskCache.MaxBytes);
        ApplyLowMemoryProtectionIfNeeded();
    }

    private void ConfigureThumbnailLoadConcurrency(int concurrency)
    {
        concurrency = Math.Clamp(concurrency, 1, 6);
        if (concurrency == _thumbnailLoadConcurrency)
        {
            return;
        }

        _thumbnailLoadConcurrency = concurrency;
        _thumbnailLoadSemaphore = new SemaphoreSlim(concurrency, concurrency);
    }

    private static int NormalizeMegabytes(int value, int fallback)
    {
        return value > 0 ? value : fallback;
    }

    private bool ShouldReduceBackgroundLoading()
    {
        return _memoryCacheCoordinator.ShouldReduceBackgroundLoading(_viewerSettings.EnableLowMemoryProtection);
    }

    private void ApplyLowMemoryProtectionIfNeeded()
    {
        if (!ShouldReduceBackgroundLoading())
        {
            return;
        }

        _memoryCacheCoordinator.TrimForMemoryPressure();
        TrimThumbnailCache(targetCount: 0);
    }

    private async Task<ImageDocument?> TryLoadDisplayPreviewDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (!ShouldUseDisplayPreview(path))
        {
            return null;
        }

        if (!_isFitMode || ViewerSurface.ActualWidth <= 0 || ViewerSurface.ActualHeight <= 0)
        {
            var requiresSafetyPreview = await Task.Run(
                () => ImageLoader.RequiresSafetyPreview(path, cancellationToken),
                cancellationToken);
            if (!requiresSafetyPreview)
            {
                return null;
            }
        }

        var (maxWidth, maxHeight) = GetDisplayPreviewBounds();
        var cacheKey = GetDisplayPreviewCacheKey(path, maxWidth, maxHeight);
        if (_displayPreviewCache.TryGet(cacheKey, path, out var preview) && preview is not null)
        {
            return await Task.Run(
                () => ImageLoader.CreatePreviewDocument(path, preview, cancellationToken),
                cancellationToken);
        }

        var document = await Task.Run(
            () => ImageLoader.LoadPreviewDocument(
                path,
                maxWidth,
                maxHeight,
                cancellationToken,
                _largeImagePreviewMaximumSide),
            cancellationToken);
        _displayPreviewCache.Add(cacheKey, path, document.Bitmap);
        return document;
    }

    private static bool ShouldUseDisplayPreview(string path)
    {
        if (!ImageCatalog.IsSupportedStillImagePath(path) || ImageCatalog.IsLikelyAnimatedImagePath(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase);
    }

    private string GetDisplayPreviewCacheKey(string path, int maxWidth, int maxHeight)
    {
        return $"{Path.GetFullPath(path)}|{maxWidth}x{maxHeight}|large:{_largeImagePreviewMaximumSide}";
    }

    private (int MaxWidth, int MaxHeight) GetDisplayPreviewBounds()
    {
        var maxWidth = (int)Math.Ceiling(Math.Max(DisplayPreviewMinimumWidth, ViewerSurface.ActualWidth * DisplayPreviewScale));
        var maxHeight = (int)Math.Ceiling(Math.Max(DisplayPreviewMinimumHeight, ViewerSurface.ActualHeight * DisplayPreviewScale));
        maxWidth = Math.Min(DisplayPreviewMaximumSide, Math.Max(1, maxWidth));
        maxHeight = Math.Min(DisplayPreviewMaximumSide, Math.Max(1, maxHeight));
        return (maxWidth, maxHeight);
    }

    private void PreloadAroundCurrent()
    {
        CancelPreload();
        if (_isClosing
            || _currentDocument?.IsLargeImagePreview == true
            || ShouldReduceBackgroundLoading())
        {
            ApplyLowMemoryProtectionIfNeeded();
            return;
        }

        var paths = _catalog.GetDirectionalNeighborPaths(
            _navigationDirection,
            forwardRadius: _mainPreloadForwardRadius,
            oppositeRadius: _mainPreloadOppositeRadius).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _preloadCts = cts;
        _ = RunPreloadQueueAsync(paths, cts);
    }

    private void ScheduleIdleFullResolutionLoad()
    {
        CancelIdleFullResolutionLoad();
        var document = _currentDocument;
        if (!_viewerSettings.LoadFullResolutionWhenIdle
            || document is not { IsPreview: true }
            || document.IsLargeImagePreview
            || _isClosing
            || ShouldReduceBackgroundLoading()
            || document.IsAnimated
            || document.IsVideo)
        {
            ApplyLowMemoryProtectionIfNeeded();
            return;
        }

        _idleFullResolutionTimer.Start();
    }

    private void CancelIdleFullResolutionLoad()
    {
        _idleFullResolutionTimer.Stop();
        var cts = _idleFullResolutionCts;
        _idleFullResolutionCts = null;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void IdleFullResolutionTimer_Tick(object? sender, EventArgs e)
    {
        _idleFullResolutionTimer.Stop();
        var document = _currentDocument;
        if (!_viewerSettings.LoadFullResolutionWhenIdle
            || document is not { IsPreview: true }
            || document.IsLargeImagePreview
            || _isClosing
            || ShouldReduceBackgroundLoading()
            || _cache.TryGet(document.Path, out _))
        {
            ApplyLowMemoryProtectionIfNeeded();
            return;
        }

        var path = document.Path;
        var cts = new CancellationTokenSource();
        _idleFullResolutionCts = cts;

        try
        {
            _ = await LoadDocumentAsync(path, cts.Token);
            if (cts.IsCancellationRequested
                || !ReferenceEquals(_idleFullResolutionCts, cts)
                || !string.Equals(_catalog.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Idle loading only warms the cache; foreground loading reports real errors.
        }
        finally
        {
            if (ReferenceEquals(_idleFullResolutionCts, cts))
            {
                _idleFullResolutionCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task RunPreloadQueueAsync(IReadOnlyList<string> paths, CancellationTokenSource cts)
    {
        try
        {
            var token = cts.Token;
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                if (ShouldReduceBackgroundLoading())
                {
                    ApplyLowMemoryProtectionIfNeeded();
                    return;
                }

                if (!ShouldPreloadPath(path))
                {
                    continue;
                }

                if (ShouldUseDisplayPreview(path))
                {
                    await PreloadDisplayPreviewAsync(path, token);
                    continue;
                }

                try
                {
                    await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        var document = ImageLoader.Load(path, token);
                        token.ThrowIfCancellationRequested();
                        _cache.Add(document);
                    }, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Preload failures are ignored so damaged neighbors do not affect the current image.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_preloadCts, cts))
            {
                _preloadCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task PreloadDisplayPreviewAsync(string path, CancellationToken cancellationToken)
    {
        if (ViewerSurface.ActualWidth <= 0 || ViewerSurface.ActualHeight <= 0)
        {
            return;
        }

        var (maxWidth, maxHeight) = GetDisplayPreviewBounds();
        var cacheKey = GetDisplayPreviewCacheKey(path, maxWidth, maxHeight);
        if (_displayPreviewCache.TryGet(cacheKey, path, out _))
        {
            return;
        }

        try
        {
            var requiresSafetyPreview = await Task.Run(
                () => ImageLoader.RequiresSafetyPreview(path, cancellationToken),
                cancellationToken);
            if (requiresSafetyPreview)
            {
                return;
            }

            var preview = await Task.Run(
                () => ImageLoader.LoadPreview(
                    path,
                    maxWidth,
                    maxHeight,
                    cancellationToken,
                    _largeImagePreviewMaximumSide),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _displayPreviewCache.Add(cacheKey, path, preview);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Preload failures are ignored so damaged neighbors do not affect the current image.
        }
    }

    private bool ShouldPreloadPath(string path)
    {
        if (_isClosing || !File.Exists(path) || _cache.TryGet(path, out _))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".apng", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ImageCatalog.IsSupportedVideoPath(path);
    }

    private void CancelPreload()
    {
        try
        {
            _preloadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task NavigateNextAsync()
    {
        if (_catalog.MoveNext())
        {
            _navigationDirection = 1;
            PrepareForNavigation();
            await LoadCurrentAsync();
        }
    }

    private async Task NavigatePreviousAsync()
    {
        if (_catalog.MovePrevious())
        {
            _navigationDirection = -1;
            PrepareForNavigation();
            await LoadCurrentAsync();
        }
    }

    private void PrepareForNavigation()
    {
        if (!_viewerSettings.KeepViewStateWhenNavigating)
        {
            EnterDefaultFitMode();
        }
    }

    private void EnterDefaultFitMode()
    {
        _isFitMode = true;
        _fitAllowsUpscale = false;
    }

    private void FitToWindow()
    {
        FitToWindow(_fitAllowsUpscale);
    }

    private void FitToWindow(bool allowUpscale, bool showZoomIndicator = false)
    {
        if ((_currentDocument is null && BitmapView.Source is null)
            || ViewerSurface.ActualWidth <= 0
            || ViewerSurface.ActualHeight <= 0)
        {
            return;
        }

        var displayPixelWidth = GetDisplayPixelWidth();
        var displayPixelHeight = GetDisplayPixelHeight();
        if (displayPixelWidth <= 0 || displayPixelHeight <= 0)
        {
            return;
        }

        var transform = ImageViewportMath.CalculateFitTransform(
            displayPixelWidth,
            displayPixelHeight,
            ViewerSurface.ActualWidth,
            ViewerSurface.ActualHeight,
            MinScale,
            allowUpscale ? GetMaximumInteractiveScale() : GetDefaultFitMaxScale(displayPixelWidth, displayPixelHeight));

        _isFitMode = true;
        _fitAllowsUpscale = allowUpscale;
        SetTransform(transform.Scale, transform.OffsetX, transform.OffsetY);
        if (showZoomIndicator)
        {
            ShowZoomIndicator(transform.Scale);
        }
    }

    private void ScheduleFitToWindowAfterLayout(ImageDocument? expectedDocument = null)
    {
        if (!_isFitMode || _isClosing)
        {
            return;
        }

        var document = expectedDocument ?? _currentDocument;
        if (document is null)
        {
            return;
        }

        var generation = ++_scheduledFitGeneration;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation != _scheduledFitGeneration
                || !_isFitMode
                || _isClosing
                || !ReferenceEquals(_currentDocument, document))
            {
                return;
            }

            FitToWindow();
        }), DispatcherPriority.ContextIdle);
    }

    private double GetDefaultFitMaxScale(int displayPixelWidth, int displayPixelHeight)
    {
        if (_currentDocument is null || displayPixelWidth <= 0 || displayPixelHeight <= 0)
        {
            return 1.0;
        }

        var originalWidth = _currentDocument.PixelWidth;
        var originalHeight = _currentDocument.PixelHeight;
        var rotation = NormalizeRotation(_rotationDegrees);
        if (rotation is 90 or 270)
        {
            (originalWidth, originalHeight) = (originalHeight, originalWidth);
        }

        var maxScale = Math.Min(
            originalWidth / (double)displayPixelWidth,
            originalHeight / (double)displayPixelHeight);

        if (double.IsNaN(maxScale) || double.IsInfinity(maxScale))
        {
            return 1.0;
        }

        return Math.Clamp(maxScale, 1.0, GetMaximumInteractiveScale());
    }

    private async Task ShowActualSizeAsync()
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (!await EnsureFullResolutionDocumentAsync(allowLargeImagePreview: true))
        {
            return;
        }

        _isFitMode = false;
        _fitAllowsUpscale = false;
        SetCenteredTransform(1.0, showZoomIndicator: true);
    }

    private async Task ToggleFitActualAsync()
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (_isFitMode)
        {
            if (!_fitAllowsUpscale && CanUpscaleCurrentFit())
            {
                FitToWindow(allowUpscale: true, showZoomIndicator: true);
                return;
            }

            await ShowActualSizeAsync();
            return;
        }

        if (Math.Abs(_scale - 1.0) > 0.001)
        {
            await ShowActualSizeAsync();
        }
        else
        {
            FitToWindow(allowUpscale: true, showZoomIndicator: true);
        }
    }

    private bool CanUpscaleCurrentFit()
    {
        var displayPixelWidth = GetDisplayPixelWidth();
        var displayPixelHeight = GetDisplayPixelHeight();
        if (displayPixelWidth <= 0
            || displayPixelHeight <= 0
            || ViewerSurface.ActualWidth <= 0
            || ViewerSurface.ActualHeight <= 0)
        {
            return false;
        }

        var fullFitScale = Math.Clamp(
            Math.Min(
                ViewerSurface.ActualWidth / displayPixelWidth,
                ViewerSurface.ActualHeight / displayPixelHeight),
            MinScale,
            GetMaximumInteractiveScale());

        return fullFitScale > Math.Max(1.001, _scale + 0.001);
    }

    private async Task CenterAtScaleAsync(double scale)
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (!await EnsureFullResolutionDocumentAsync(allowLargeImagePreview: true))
        {
            return;
        }

        SetCenteredTransform(Math.Clamp(scale, MinScale, GetMaximumInteractiveScale()));
    }

    private void CenterAtScale(double scale)
    {
        if (_currentDocument is null)
        {
            return;
        }

        SetCenteredTransform(Math.Clamp(scale, MinScale, GetMaximumInteractiveScale()));
    }

    private void SetCenteredTransform(double scale, bool showZoomIndicator = false)
    {
        if (_currentDocument is null)
        {
            return;
        }

        var displayPixelWidth = GetDisplayPixelWidth();
        var displayPixelHeight = GetDisplayPixelHeight();
        if (displayPixelWidth <= 0 || displayPixelHeight <= 0)
        {
            return;
        }

        var transform = ImageViewportMath.CalculateCenteredTransform(
            displayPixelWidth,
            displayPixelHeight,
            ViewerSurface.ActualWidth,
            ViewerSurface.ActualHeight,
            scale);

        SetTransform(transform.Scale, transform.OffsetX, transform.OffsetY);
        if (showZoomIndicator)
        {
            ShowZoomIndicator(transform.Scale);
        }
    }

    private async Task ZoomAtAsync(Point center, double targetScale)
    {
        if (_currentDocument is null)
        {
            return;
        }

        var zoomFactor = _scale > 0 ? targetScale / _scale : 1.0;
        if (!await EnsureFullResolutionDocumentAsync(allowLargeImagePreview: true))
        {
            return;
        }

        if (_scale > 0)
        {
            targetScale = _scale * zoomFactor;
        }

        var newScale = Math.Clamp(targetScale, MinScale, GetMaximumInteractiveScale());
        var imageX = (center.X - BitmapView.OffsetX) / _scale;
        var imageY = (center.Y - BitmapView.OffsetY) / _scale;
        var newX = center.X - imageX * newScale;
        var newY = center.Y - imageY * newScale;

        _isFitMode = false;
        _fitAllowsUpscale = false;
        UseInteractiveScaling();
        SetTransform(newScale, newX, newY);
        ShowZoomIndicator(newScale);
    }

    private async Task ZoomByAsync(double factor)
    {
        var center = new Point(ViewerSurface.ActualWidth / 2.0, ViewerSurface.ActualHeight / 2.0);
        await ZoomAtAsync(center, _scale * factor);
    }

    private void SetTransform(double scale, double x, double y)
    {
        _scale = scale;
        BitmapView.ViewScale = scale;
        BitmapView.OffsetX = x;
        BitmapView.OffsetY = y;
    }

    private void ShowZoomIndicator(double scale)
    {
        if (!_viewerSettings.ShowZoomIndicator)
        {
            _zoomIndicatorTimer.Stop();
            ZoomIndicator.BeginAnimation(OpacityProperty, null);
            ZoomIndicator.Opacity = 0;
            ZoomIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        _zoomIndicatorGeneration++;
        _zoomIndicatorTimer.Stop();
        ZoomIndicator.BeginAnimation(OpacityProperty, null);
        ZoomIndicatorText.Text = FormatZoomScale(
            scale,
            _viewerSettings.ZoomIndicatorDisplayMode,
            GetFitReferenceScale());
        ZoomIndicator.Visibility = Visibility.Visible;
        ZoomIndicator.Opacity = 1;
        _zoomIndicatorTimer.Start();
    }

    private double GetFitReferenceScale()
    {
        var displayPixelWidth = GetDisplayPixelWidth();
        var displayPixelHeight = GetDisplayPixelHeight();
        if (displayPixelWidth <= 0
            || displayPixelHeight <= 0
            || ViewerSurface.ActualWidth <= 0
            || ViewerSurface.ActualHeight <= 0)
        {
            return 1.0;
        }

        return ImageViewportMath.CalculateFitTransform(
            displayPixelWidth,
            displayPixelHeight,
            ViewerSurface.ActualWidth,
            ViewerSurface.ActualHeight,
            MinScale,
            GetDefaultFitMaxScale(displayPixelWidth, displayPixelHeight)).Scale;
    }

    private static string FormatZoomScale(
        double scale,
        ZoomIndicatorDisplayMode displayMode,
        double fitReferenceScale)
    {
        if (displayMode == ZoomIndicatorDisplayMode.Multiplier)
        {
            var relativeScale = fitReferenceScale > 0
                ? scale / fitReferenceScale
                : scale;
            return relativeScale < 0.1
                ? $"{relativeScale:0.000}×"
                : relativeScale < 10
                    ? $"{relativeScale:0.00}×"
                    : $"{relativeScale:0.0}×";
        }

        var percentage = scale * 100;
        if (percentage < 1)
        {
            return $"{percentage:0.0}%";
        }

        return $"{percentage:0}%";
    }

    private static string FormatElapsedDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMilliseconds(1))
        {
            return "<1 毫秒";
        }

        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return $"{elapsed.TotalMilliseconds:0} 毫秒";
        }

        return $"{elapsed.TotalSeconds:0.0} 秒";
    }

    private void ZoomIndicatorTimer_Tick(object? sender, EventArgs e)
    {
        _zoomIndicatorTimer.Stop();
        var generation = _zoomIndicatorGeneration;
        var animation = new DoubleAnimation
        {
            From = ZoomIndicator.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(320),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        animation.Completed += (_, _) =>
        {
            if (generation != _zoomIndicatorGeneration)
            {
                return;
            }

            ZoomIndicator.Opacity = 0;
            ZoomIndicator.Visibility = Visibility.Collapsed;
        };

        ZoomIndicator.BeginAnimation(OpacityProperty, animation);
    }

    private int GetDisplayPixelWidth()
    {
        return BitmapView.Source?.PixelWidth ?? _currentDocument?.PixelWidth ?? 0;
    }

    private int GetDisplayPixelHeight()
    {
        return BitmapView.Source?.PixelHeight ?? _currentDocument?.PixelHeight ?? 0;
    }

    private void SetViewerSource(BitmapSource source)
    {
        BitmapView.Source = CreateDisplayBitmap(source);
    }

    private void SetDisplayPreviewSource(string path, BitmapSource source)
    {
        _isPreviewDisplay = true;
        _previewDisplayPath = Path.GetFullPath(path);
        _previewDisplayBitmap = source;
        BitmapView.Source = CreateDisplayBitmap(source);
    }

    private double GetMaximumInteractiveScale()
    {
        return _currentDocument?.IsLargeImagePreview == true ? 1.0 : MaxScale;
    }

    private async Task<bool> EnsureFullResolutionDocumentAsync(bool allowLargeImagePreview = false)
    {
        if (_currentDocument is null)
        {
            return false;
        }

        if (_currentDocument.IsLargeImagePreview)
        {
            if (allowLargeImagePreview)
            {
                return true;
            }

            ShowUserError(
                "当前图片超过 1.2 亿像素，正在使用超大图安全预览。\n\n" +
                "此操作需要完整原始像素，安全预览模式下暂不可用。");
            return false;
        }

        if (!_currentDocument.IsPreview)
        {
            return true;
        }

        CancelIdleFullResolutionLoad();
        var path = _currentDocument.Path;
        var fitMode = _isFitMode;
        var token = _loadCts?.Token ?? CancellationToken.None;

        try
        {
            LoadingText.Text = "正在加载原图...";
            LoadingPanel.Visibility = Visibility.Visible;

            var document = await LoadDocumentAsync(path, token);
            if (token.IsCancellationRequested
                || !string.Equals(_catalog.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _currentDocument = document;
            StartAnimation(document);
            if (fitMode)
            {
                FitToWindow();
                ScheduleFitToWindowAfterLayout(document);
            }

            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            UpdateInfoPanel();
            UpdateCommands();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            ShowUserError($"无法加载原图：\n{path}\n\n{FriendlyException(ex)}", ex);
            return false;
        }
    }

    private void ClearDisplayPreviewState()
    {
        _isPreviewDisplay = false;
        _previewDisplayPath = null;
        _previewDisplayBitmap = null;
    }

    private BitmapSource CreateDisplayBitmap(BitmapSource source)
    {
        var rotation = NormalizeRotation(_rotationDegrees);
        if (rotation == 0)
        {
            return source;
        }

        var rotated = new TransformedBitmap(source, new RotateTransform(rotation));
        return CreateBitmapSnapshot(rotated);
    }

    private static BitmapSource CreateBitmapSnapshot(BitmapSource source)
    {
        BitmapSource pixelSource = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = checked(pixelSource.PixelWidth * 4);
        var pixels = new byte[checked(stride * pixelSource.PixelHeight)];
        pixelSource.CopyPixels(pixels, stride, 0);

        var snapshot = BitmapSource.Create(
            pixelSource.PixelWidth,
            pixelSource.PixelHeight,
            pixelSource.DpiX,
            pixelSource.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        if (snapshot.CanFreeze)
        {
            snapshot.Freeze();
        }

        return snapshot;
    }

    private static int NormalizeRotation(int degrees)
    {
        var normalized = degrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private void StartAnimation(ImageDocument document)
    {
        StopAnimation();
        ClearDisplayPreviewState();

        if (!document.IsAnimated)
        {
            UseInteractiveScaling(InitialQualityRestoreDelay);
            SetViewerSource(document.Bitmap);
            return;
        }

        _animationFrameIndex = 0;
        _isAnimationPaused = false;
        BitmapView.ScalingMode = BitmapScalingMode.Linear;
        SetViewerSource(document.AnimationFrames[0].Bitmap);
        _animationTimer.Interval = document.AnimationFrames[0].Delay;
        _animationTimer.Start();
        UpdateAnimationControls();
    }

    private void StopAnimation()
    {
        if (_animationTimer.IsEnabled)
        {
            _animationTimer.Stop();
        }

        _animationFrameIndex = 0;
        _isAnimationPaused = false;
        UpdateAnimationControls();
    }

    private void ToggleAnimationPlayback()
    {
        if (_currentDocument?.IsAnimated != true)
        {
            return;
        }

        if (_isAnimationPaused)
        {
            ResumeAnimation();
        }
        else
        {
            PauseAnimation();
        }
    }

    private void PauseAnimation()
    {
        if (_currentDocument?.IsAnimated != true)
        {
            return;
        }

        _animationTimer.Stop();
        _isAnimationPaused = true;
        UpdateAnimationControls();
        UpdateInfoPanel();
    }

    private void ResumeAnimation()
    {
        var document = _currentDocument;
        if (document?.IsAnimated != true || document.AnimationFrames.Count == 0)
        {
            return;
        }

        _animationFrameIndex = Math.Clamp(_animationFrameIndex, 0, document.AnimationFrames.Count - 1);
        _animationTimer.Interval = document.AnimationFrames[_animationFrameIndex].Delay;
        _animationTimer.Start();
        _isAnimationPaused = false;
        UpdateAnimationControls();
        UpdateInfoPanel();
    }

    private void RestartAnimation()
    {
        var document = _currentDocument;
        if (document?.IsAnimated != true || document.AnimationFrames.Count == 0)
        {
            return;
        }

        _animationFrameIndex = 0;
        _isAnimationPaused = false;
        SetViewerSource(document.AnimationFrames[0].Bitmap);
        _animationTimer.Interval = document.AnimationFrames[0].Delay;
        _animationTimer.Start();
        UpdateAnimationControls();
        UpdateInfoPanel();
    }

    private void UseInteractiveScaling()
    {
        UseInteractiveScaling(InteractiveQualityRestoreDelay);
    }

    private void UseInteractiveScaling(TimeSpan restoreDelay)
    {
        BitmapView.ScalingMode = BitmapScalingMode.Linear;
        _qualityRestoreTimer.Stop();
        _qualityRestoreTimer.Interval = restoreDelay;
        _qualityRestoreTimer.Start();
    }

    private void UseFinalScaling()
    {
        _qualityRestoreTimer.Stop();
        BitmapView.ScalingMode = _currentDocument?.IsAnimated == true
            ? BitmapScalingMode.Linear
            : BitmapScalingMode.HighQuality;
    }

    private void QualityRestoreTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDragging)
        {
            return;
        }

        UseFinalScaling();
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        var document = _currentDocument;
        if (document is null || !document.IsAnimated)
        {
            StopAnimation();
            return;
        }

        if (_isAnimationPaused)
        {
            _animationTimer.Stop();
            return;
        }

        _animationFrameIndex = (_animationFrameIndex + 1) % document.AnimationFrames.Count;
        var frame = document.AnimationFrames[_animationFrameIndex];
        SetViewerSource(frame.Bitmap);
        _animationTimer.Interval = frame.Delay;
    }

    private void FadeInBitmapView()
    {
        BitmapView.BeginAnimation(OpacityProperty, null);
        BitmapView.Opacity = 0.88;
        BitmapView.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                From = 0.88,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(110),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void ShowBoundaryToastIfNeeded()
    {
        if (!_viewerSettings.ShowOperationNotifications)
        {
            HideBoundaryToast();
            return;
        }

        if (_currentDocument?.IsLargeImagePreview == true)
        {
            ShowBoundaryToast(
                $"超大图片已使用 {_currentDocument.Bitmap.PixelWidth:N0} × {_currentDocument.Bitmap.PixelHeight:N0} 安全预览，打开用时 {FormatElapsedDuration(_lastImageLoadDuration)}");
            return;
        }

        if (_catalog.Count <= 1 || _catalog.Index < 0)
        {
            HideBoundaryToast();
            return;
        }

        if (_catalog.Index == 0)
        {
            ShowBoundaryToast("当前是第一张图");
            return;
        }

        if (_catalog.Index == _catalog.Count - 1)
        {
            ShowBoundaryToast("当前是最后一张图");
            return;
        }

        HideBoundaryToast();
    }

    private void ShowBoundaryToast(string message)
    {
        if (!_viewerSettings.ShowOperationNotifications)
        {
            HideBoundaryToast();
            return;
        }

        _boundaryToastTimer.Stop();
        BoundaryToast.BeginAnimation(OpacityProperty, null);
        BoundaryToastText.Text = message;
        BoundaryToast.Visibility = Visibility.Visible;
        BoundaryToast.Opacity = 1;
        _boundaryToastTimer.Start();
    }

    private void HideBoundaryToast()
    {
        _boundaryToastTimer.Stop();
        BoundaryToast.BeginAnimation(OpacityProperty, null);
        BoundaryToast.Opacity = 0;
        BoundaryToast.Visibility = Visibility.Collapsed;
    }

    private void BoundaryToastTimer_Tick(object? sender, EventArgs e)
    {
        _boundaryToastTimer.Stop();
        var animation = new DoubleAnimation
        {
            From = BoundaryToast.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(650),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        animation.Completed += (_, _) =>
        {
            BoundaryToast.Opacity = 0;
            BoundaryToast.Visibility = Visibility.Collapsed;
        };

        BoundaryToast.BeginAnimation(OpacityProperty, animation);
    }

    private void ShowQuickSearch(bool rememberForStartup = true)
    {
        if (rememberForStartup)
        {
            SetQuickSearchStartupPreference(true);
        }

        _quickSearchDebounceTimer.Stop();
        CancelQuickSearchQuery();
        _quickSearchResult = null;
        _quickSearchTargetIndex = null;
        _isQuickSearchVisible = true;
        QuickSearchOverlay.Visibility = Visibility.Visible;
        QuickSearchOverlay.IsHitTestVisible = true;
        ApplyQuickSearchPosition(new Point(
            _viewerSettings.QuickSearchOffsetX,
            _viewerSettings.QuickSearchOffsetY));
        _activeQuickSearchMode = _viewerSettings.QuickSearchMode;
        ApplyQuickSearchModeUi();
        QuickSearchTextBox.Text = string.Empty;
        SetQuickSearchTextEntryActive(false);
        UpdateQuickSearchResult();

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        QuickSearchOverlay.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(QuickSearchOverlay.Opacity, 1, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = easing,
            });
        QuickSearchScaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(QuickSearchScaleTransform.ScaleX, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = easing,
            });
        QuickSearchScaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(QuickSearchScaleTransform.ScaleY, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = easing,
            });
        QuickSearchTranslateTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(QuickSearchTranslateTransform.Y, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = easing,
            });

        Dispatcher.BeginInvoke(() =>
        {
            ApplyQuickSearchPosition(new Point(
                _viewerSettings.QuickSearchOffsetX,
                _viewerSettings.QuickSearchOffsetY));
            UpdateQuickSearchBackdropViewbox();
            Focus();
            Keyboard.Focus(this);
        }, DispatcherPriority.Input);
    }

    private void QuickSearchGlass_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isQuickSearchVisible)
        {
            ApplyQuickSearchPosition(new Point(
                _viewerSettings.QuickSearchOffsetX,
                _viewerSettings.QuickSearchOffsetY));
            UpdateQuickSearchBackdropViewbox();
        }
    }

    private void QuickSearchGlass_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isQuickSearchVisible
            || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        _isQuickSearchDragging = true;
        _quickSearchDragStartPoint = e.GetPosition(Root);
        _quickSearchDragStartOffset = new Point(
            QuickSearchPositionTransform.X,
            QuickSearchPositionTransform.Y);
        QuickSearchGlass.Cursor = Cursors.SizeAll;
        QuickSearchGlass.CaptureMouse();
        e.Handled = true;
    }

    private void QuickSearchGlass_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isQuickSearchDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(Root);
        ApplyQuickSearchPosition(new Point(
            _quickSearchDragStartOffset.X + currentPoint.X - _quickSearchDragStartPoint.X,
            _quickSearchDragStartOffset.Y + currentPoint.Y - _quickSearchDragStartPoint.Y));
        e.Handled = true;
    }

    private void QuickSearchGlass_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isQuickSearchDragging)
        {
            return;
        }

        e.Handled = true;
        FinishQuickSearchDrag(persist: true);
    }

    private void QuickSearchGlass_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isQuickSearchDragging)
        {
            FinishQuickSearchDrag(persist: true);
        }
    }

    private void FinishQuickSearchDrag(bool persist)
    {
        _isQuickSearchDragging = false;
        QuickSearchGlass.Cursor = null;
        if (QuickSearchGlass.IsMouseCaptured)
        {
            QuickSearchGlass.ReleaseMouseCapture();
        }

        if (persist)
        {
            SaveQuickSearchPosition();
        }
    }

    private void ApplyQuickSearchPosition(Point requestedOffset)
    {
        var offset = ClampQuickSearchOffset(requestedOffset);
        QuickSearchPositionTransform.X = offset.X;
        QuickSearchPositionTransform.Y = offset.Y;
        _viewerSettings.QuickSearchOffsetX = offset.X;
        _viewerSettings.QuickSearchOffsetY = offset.Y;
        UpdateQuickSearchBackdropViewbox();
    }

    private Point ClampQuickSearchOffset(Point requestedOffset)
    {
        var requestedX = double.IsFinite(requestedOffset.X) ? requestedOffset.X : 0;
        var requestedY = double.IsFinite(requestedOffset.Y) ? requestedOffset.Y : 0;
        if (Root.ActualWidth <= 0
            || Root.ActualHeight <= 0
            || QuickSearchGlass.ActualWidth <= 0
            || QuickSearchGlass.ActualHeight <= 0)
        {
            return new Point(requestedX, requestedY);
        }

        var defaultLeft = (Root.ActualWidth - QuickSearchGlass.ActualWidth) / 2;
        var defaultTop = QuickSearchOverlay.Margin.Top;
        var minimumX = QuickSearchEdgePadding - defaultLeft;
        var maximumX = Root.ActualWidth
            - QuickSearchEdgePadding
            - defaultLeft
            - QuickSearchGlass.ActualWidth;
        var minimumY = QuickSearchEdgePadding - defaultTop;
        var maximumY = Root.ActualHeight
            - QuickSearchEdgePadding
            - defaultTop
            - QuickSearchGlass.ActualHeight;

        return new Point(
            minimumX <= maximumX ? Math.Clamp(requestedX, minimumX, maximumX) : 0,
            minimumY <= maximumY ? Math.Clamp(requestedY, minimumY, maximumY) : 0);
    }

    private void SaveQuickSearchPosition()
    {
        try
        {
            _viewerSettings.Save();
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("SaveQuickSearchPosition", "保存快速搜索位置失败。", ex);
        }
    }

    private void ResetPersistedQuickSearchPosition()
    {
        if (_viewerSettings.QuickSearchOffsetX == 0
            && _viewerSettings.QuickSearchOffsetY == 0)
        {
            return;
        }

        _viewerSettings.QuickSearchOffsetX = 0;
        _viewerSettings.QuickSearchOffsetY = 0;
        SaveQuickSearchPosition();
    }

    private void UpdateQuickSearchBackdropViewbox()
    {
        if (QuickSearchGlass.ActualWidth <= 0
            || QuickSearchGlass.ActualHeight <= 0
            || ViewerSurface.ActualWidth <= 0
            || ViewerSurface.ActualHeight <= 0)
        {
            return;
        }

        var topLeft = QuickSearchGlass.TranslatePoint(new Point(0, 0), ViewerSurface);
        QuickSearchBackdropBrush.Viewbox = new Rect(
            topLeft.X,
            topLeft.Y,
            QuickSearchGlass.ActualWidth,
            QuickSearchGlass.ActualHeight);
    }

    private void HideQuickSearch(bool rememberForStartup = true)
    {
        if (!_isQuickSearchVisible)
        {
            return;
        }

        if (rememberForStartup)
        {
            SetQuickSearchStartupPreference(false);
        }

        _isQuickSearchVisible = false;
        SetQuickSearchTextEntryActive(false);
        if (_isQuickSearchDragging)
        {
            FinishQuickSearchDrag(persist: true);
        }
        _quickSearchDebounceTimer.Stop();
        CancelQuickSearchQuery();
        ClearQuickSearchThumbnailFilter(rebuildIfVisible: _showThumbnailSidebar);
        QuickSearchOverlay.IsHitTestVisible = false;
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacityAnimation = new DoubleAnimation(QuickSearchOverlay.Opacity, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = easing,
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (_isQuickSearchVisible)
            {
                return;
            }

            QuickSearchOverlay.BeginAnimation(OpacityProperty, null);
            QuickSearchScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            QuickSearchScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            QuickSearchTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
            QuickSearchTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            QuickSearchOverlay.Visibility = Visibility.Collapsed;
            QuickSearchOverlay.Opacity = 0;
            QuickSearchScaleTransform.ScaleX = 0.96;
            QuickSearchScaleTransform.ScaleY = 0.96;
            QuickSearchTranslateTransform.X = 0;
            QuickSearchTranslateTransform.Y = -18;
            Focus();
        };

        QuickSearchOverlay.BeginAnimation(OpacityProperty, opacityAnimation);
        QuickSearchScaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(QuickSearchScaleTransform.ScaleX, 0.98, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = easing,
            });
        QuickSearchScaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(QuickSearchScaleTransform.ScaleY, 0.98, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = easing,
            });
        QuickSearchTranslateTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(QuickSearchTranslateTransform.Y, -10, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = easing,
            });
    }

    private void SetQuickSearchStartupPreference(bool enabled)
    {
        if (_viewerSettings.ShowQuickSearchOnStartup == enabled)
        {
            return;
        }

        try
        {
            _viewerSettings.ShowQuickSearchOnStartup = enabled;
            _viewerSettings.Save();
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("SaveQuickSearchStartupPreference", "保存快速搜索启动偏好失败。", ex);
        }
    }

    private void ToggleQuickSearch()
    {
        if (_isQuickSearchVisible)
        {
            HideQuickSearch();
            ResetPersistedQuickSearchPosition();
            return;
        }

        ShowQuickSearch();
    }

    private void QuickSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isQuickSearchVisible && QuickSearchTextBox.IsKeyboardFocusWithin)
        {
            SetQuickSearchTextEntryActive(true);
        }

        _quickSearchDebounceTimer.Stop();
        CancelQuickSearchQuery();
        if (_activeQuickSearchMode == QuickSearchMode.FileName
            && !string.IsNullOrWhiteSpace(QuickSearchTextBox.Text))
        {
            SetQuickSearchHint("正在搜索…");
            _quickSearchDebounceTimer.Start();
        }
        else
        {
            _quickSearchResult = null;
            ClearQuickSearchThumbnailFilter(rebuildIfVisible: _showThumbnailSidebar);
            UpdateQuickSearchResult();
        }
    }

    private void QuickSearchTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            SetQuickSearchTextEntryActive(true);
        }
    }

    private void QuickSearchTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SetQuickSearchTextEntryActive(false);
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isQuickSearchVisible
            || !_quickSearchInteractionState.IsTextEntryActive
            || QuickSearchTextBox.IsMouseOver)
        {
            return;
        }

        DeactivateQuickSearchTextEntry();
    }

    private void DeactivateQuickSearchTextEntry()
    {
        SetQuickSearchTextEntryActive(false);
        Focus();
        Keyboard.Focus(this);
    }

    private void SetQuickSearchTextEntryActive(bool active)
    {
        _quickSearchInteractionState.SetTextEntryActive(active);
        QuickSearchTextBox.CaretBrush = active ? Brushes.White : Brushes.Transparent;
    }

    private async void QuickSearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _quickSearchDebounceTimer.Stop();
        await RefreshQuickSearchAsync();
    }

    private async void QuickSearchModeButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _activeQuickSearchMode = _activeQuickSearchMode == QuickSearchMode.Index
            ? QuickSearchMode.FileName
            : QuickSearchMode.Index;
        CancelQuickSearchQuery();
        _quickSearchResult = null;
        if (_activeQuickSearchMode == QuickSearchMode.Index)
        {
            ClearQuickSearchThumbnailFilter(rebuildIfVisible: _showThumbnailSidebar);
        }

        ApplyQuickSearchModeUi();
        _quickSearchDebounceTimer.Stop();
        if (_activeQuickSearchMode == QuickSearchMode.FileName
            && !string.IsNullOrWhiteSpace(QuickSearchTextBox.Text))
        {
            await RefreshQuickSearchAsync();
        }
        else
        {
            UpdateQuickSearchResult();
        }

        QuickSearchTextBox.Focus();
        Keyboard.Focus(QuickSearchTextBox);
        SetQuickSearchTextEntryActive(true);
    }

    private async void QuickSearchGoButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ExecuteQuickSearchAsync();
    }

    private void ApplyQuickSearchModeUi()
    {
        var searchesByFileName = _activeQuickSearchMode == QuickSearchMode.FileName;
        QuickSearchModeText.Text = searchesByFileName
            ? "名称"
            : "序号";
        QuickSearchModeButton.ToolTip = searchesByFileName
            ? "当前按文件名查找，点击切换为序号"
            : "当前按序号跳转，点击切换为文件名";
        AutomationProperties.SetName(
            QuickSearchModeButton,
            searchesByFileName ? "当前按文件名搜索，点击切换为序号" : "当前按图片序号搜索，点击切换为文件名");
        AutomationProperties.SetHelpText(
            QuickSearchTextBox,
            searchesByFileName ? "输入文件名关键词并按回车跳转" : "输入图片序号并按回车跳转");
    }

    private bool ShouldShowQuickSearchThumbnailResults()
    {
        return _isQuickSearchVisible
            && _activeQuickSearchMode == QuickSearchMode.FileName
            && _showThumbnailSidebar
            && !string.IsNullOrWhiteSpace(QuickSearchTextBox.Text);
    }

    private void ApplyQuickSearchThumbnailFilter()
    {
        if (!ShouldShowQuickSearchThumbnailResults())
        {
            ClearQuickSearchThumbnailFilter(rebuildIfVisible: _showThumbnailSidebar);
            return;
        }

        var result = _quickSearchResult;
        if (result is null
            || !string.Equals(result.Query, QuickSearchTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (result.MatchingIndices.Count == 0)
        {
            // 搜索框已经给出“没有找到”的反馈，右侧保留上一次有效内容和滚动位置。
            return;
        }

        _thumbnailSearchItems = new IndexedProjectionList<ThumbnailItem>(
            _thumbnailItems,
            result.MatchingIndices);
        _thumbnailScrollViewer = null;
        _thumbnailVisibleStartIndex = 0;
        _thumbnailVisibleEndIndex = -1;
        RebuildThumbnailRows();
        UpdateThumbnailSidebarTitle();
        UpdateThumbnailSelection();
        QueueVisibleThumbnails();
    }

    private void ClearQuickSearchThumbnailFilter(bool rebuildIfVisible)
    {
        if (_thumbnailSearchItems is null && !_thumbnailRowsNeedRefresh)
        {
            return;
        }

        _thumbnailSearchItems = null;
        _thumbnailScrollViewer = null;
        _thumbnailVisibleStartIndex = 0;
        _thumbnailVisibleEndIndex = -1;
        UpdateThumbnailSidebarTitle();
        if (!rebuildIfVisible)
        {
            _thumbnailRowsNeedRefresh = true;
            return;
        }

        RebuildThumbnailRows();
        UpdateThumbnailSelection();
        QueueVisibleThumbnails();
    }

    private void UpdateQuickSearchResult()
    {
        _quickSearchTargetIndex = null;
        var query = QuickSearchTextBox.Text.Trim();
        if (_catalog.Count == 0)
        {
            SetQuickSearchHint("当前没有可跳转的图片", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            SetQuickSearchHint(string.Empty);
            return;
        }

        if (_activeQuickSearchMode == QuickSearchMode.Index)
        {
            if (!QuickSearchMatcher.TryResolveOneBasedIndex(query, _catalog.Count, out var targetIndex))
            {
                SetQuickSearchHint($"请输入 1–{_catalog.Count:N0} 之间的序号", isError: true);
                return;
            }

            _quickSearchTargetIndex = targetIndex;
            var fileName = Path.GetFileName(_catalog.Paths[targetIndex]);
            SetQuickSearchHint(
                $"第 {targetIndex + 1:N0} 张  ·  {QuickSearchMatcher.CompactFileName(fileName)}",
                toolTip: fileName);
            return;
        }

        var result = _quickSearchResult;
        if (result is null
            || !string.Equals(result.Query, query, StringComparison.OrdinalIgnoreCase))
        {
            SetQuickSearchHint("正在搜索…");
            return;
        }

        var matchIndex = result.FirstIndex;
        if (matchIndex >= 0)
        {
            _quickSearchTargetIndex = matchIndex;
            var fileName = _quickSearchIndex.GetFileName(matchIndex) ?? Path.GetFileName(_catalog.Paths[matchIndex]);
            SetQuickSearchHint(
                $"第 {matchIndex + 1:N0} 张  ·  {QuickSearchMatcher.CompactFileName(fileName)}",
                toolTip: fileName);
            return;
        }

        SetQuickSearchHint("没有找到匹配的文件名", isError: true);
    }

    private async Task RefreshQuickSearchAsync()
    {
        var query = QuickSearchTextBox.Text.Trim();
        if (_activeQuickSearchMode != QuickSearchMode.FileName || query.Length == 0)
        {
            CancelQuickSearchQuery();
            _quickSearchResult = null;
            ClearQuickSearchThumbnailFilter(rebuildIfVisible: _showThumbnailSidebar);
            UpdateQuickSearchResult();
            return;
        }

        CancelQuickSearchQuery();
        var cts = new CancellationTokenSource();
        _quickSearchCts = cts;
        var catalogGeneration = _thumbnailGeneration;
        var catalogPaths = _catalog.Paths;
        var needsIndexReset = _quickSearchIndexGeneration != catalogGeneration;
        var previousResult = needsIndexReset ? null : _quickSearchResult;
        try
        {
            var result = await Task.Run(
                () =>
                {
                    return needsIndexReset
                        ? _quickSearchIndex.ResetAndSearch(catalogPaths, query, cts.Token)
                        : _quickSearchIndex.Search(query, previousResult, cts.Token);
                },
                cts.Token);
            if (cts.IsCancellationRequested
                || !ReferenceEquals(_quickSearchCts, cts)
                || _activeQuickSearchMode != QuickSearchMode.FileName
                || !string.Equals(query, QuickSearchTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _quickSearchIndexGeneration = catalogGeneration;
            _quickSearchResult = result;
            UpdateQuickSearchResult();
            ApplyQuickSearchThumbnailFilter();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_quickSearchCts, cts))
            {
                _quickSearchCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelQuickSearchQuery()
    {
        var cts = _quickSearchCts;
        _quickSearchCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetQuickSearchHint(string text, bool isError = false, string? toolTip = null)
    {
        QuickSearchHintText.Text = text;
        QuickSearchHintText.ToolTip = toolTip;
        QuickSearchHintText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(255, 190, 190))
            : new SolidColorBrush(Color.FromRgb(239, 242, 245));
    }

    private async Task ExecuteQuickSearchAsync()
    {
        _quickSearchDebounceTimer.Stop();
        if (_activeQuickSearchMode == QuickSearchMode.FileName)
        {
            await RefreshQuickSearchAsync();
        }
        else
        {
            UpdateQuickSearchResult();
        }
        if (_quickSearchTargetIndex is not { } targetIndex)
        {
            ShakeQuickSearch();
            return;
        }

        var previousIndex = _catalog.Index;
        if (!_catalog.MoveToIndex(targetIndex))
        {
            return;
        }

        _navigationDirection = Math.Sign(targetIndex - previousIndex);

        if (_viewerSettings.HideQuickSearchAfterJump)
        {
            HideQuickSearch(rememberForStartup: false);
        }

        PrepareForNavigation();
        await LoadCurrentAsync();
    }

    private void ShakeQuickSearch()
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(260),
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromPercent(0.2)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(6, KeyTime.FromPercent(0.45)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromPercent(0.7)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        QuickSearchTranslateTransform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var shortcut = KeyboardShortcut.FromKeyEvent(e);

        if (_isQuickSearchVisible)
        {
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                HideQuickSearch();
                return;
            }

            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await ExecuteQuickSearchAsync();
                return;
            }

            if (_shortcutSettings.Matches(ShortcutAction.ShowQuickSearch, shortcut))
            {
                e.Handled = true;
                ToggleQuickSearch();
                return;
            }

            if (_quickSearchInteractionState.ShouldTextBoxHandleKey(
                    e.Key,
                    KeyboardShortcut.NormalizeModifiers(Keyboard.Modifiers)))
            {
                // 用户明确进入文字输入状态后，文本和常用编辑键优先于查看器快捷键。
                return;
            }

            // 搜索刚打开且尚未开始输入时，已配置的查看器快捷键仍然优先执行。
        }

        if (_shortcutSettings.Matches(ShortcutAction.ShowQuickSearch, shortcut))
        {
            e.Handled = true;
            ToggleQuickSearch();
            return;
        }

        if (_isCropMode)
        {
            e.Handled = true;
            if ((e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
                || _shortcutSettings.Matches(ShortcutAction.CancelOrClose, shortcut))
            {
                ExitCropMode();
            }
            else if (_shortcutSettings.Matches(ShortcutAction.SaveCrop, shortcut))
            {
                await SaveCropAsync();
            }

            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.CancelOrClose, shortcut))
        {
            e.Handled = true;
            if (_isFullScreen)
            {
                await ExitFullScreenAsync();
            }
            else
            {
                Close();
            }

            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.CopyName, shortcut))
        {
            CopyCurrentName();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.CopyPath, shortcut))
        {
            CopyCurrentPath();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.CopyFile, shortcut))
        {
            CopyCurrentFile();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.OpenContainingFolder, shortcut))
        {
            e.Handled = true;
            await OpenContainingFolderAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.OpenFolder, shortcut))
        {
            e.Handled = true;
            await ShowOpenFolderDialogAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.OpenImage, shortcut))
        {
            e.Handled = true;
            await ShowOpenImageDialogAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.PreviousImage, shortcut))
        {
            // 必须在异步切图前阻止事件继续传给中文输入法，否则会弹出拼音组合栏。
            e.Handled = true;
            await NavigatePreviousAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.NextImage, shortcut))
        {
            // 必须在异步切图前阻止事件继续传给中文输入法，否则会弹出拼音组合栏。
            e.Handled = true;
            await NavigateNextAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ToggleAnimationPlayback, shortcut))
        {
            ToggleAnimationPlayback();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.RestartAnimation, shortcut))
        {
            RestartAnimation();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.DeleteImage, shortcut))
        {
            e.Handled = true;
            await DeleteCurrentAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.BatchDeleteCurrentFolder, shortcut))
        {
            e.Handled = true;
            await BatchDeleteCurrentDirectoryMediaAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ToggleFavorite, shortcut))
        {
            e.Handled = true;
            await ToggleCurrentFavoriteAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ToggleFavoritesView, shortcut))
        {
            e.Handled = true;
            await ToggleFavoritesViewAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ToggleFullScreen, shortcut))
        {
            e.Handled = true;
            await ToggleFullScreenAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ToggleFitActual, shortcut))
        {
            e.Handled = true;
            await ToggleFitActualAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ToggleThumbnailSidebar, shortcut))
        {
            ToggleThumbnailSidebar();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ToggleThumbnailColumns, shortcut))
        {
            ToggleThumbnailColumns();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.RotateLeft, shortcut))
        {
            RotateCurrent(-90);
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.RotateRight, shortcut))
        {
            RotateCurrent(90);
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.CycleSortMode, shortcut))
        {
            await CycleSortModeAsync();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ZoomIn, shortcut))
        {
            e.Handled = true;
            await ZoomByAsync(1.15);
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ZoomOut, shortcut))
        {
            e.Handled = true;
            await ZoomByAsync(1.0 / 1.15);
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ActualSize, shortcut))
        {
            e.Handled = true;
            await ShowActualSizeAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.FitWindow, shortcut))
        {
            FitToWindow(allowUpscale: true, showZoomIndicator: true);
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.CropImage, shortcut))
        {
            e.Handled = true;
            await BeginCropModeAsync(CropShape.Rectangle);
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.CircleCropImage, shortcut))
        {
            e.Handled = true;
            await BeginCropModeAsync(CropShape.Circle);
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.CompressImage, shortcut))
        {
            e.Handled = true;
            await CompressCurrentAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.OpenBatchCompressTools, shortcut))
        {
            OpenBatchCompressTools();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.SaveVideoCover, shortcut))
        {
            e.Handled = true;
            await SaveVideoCoverAsync();
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ToggleInfo, shortcut))
        {
            ToggleInfoPanel();
            e.Handled = true;
            return;
        }

        if (_shortcutSettings.Matches(ShortcutAction.ShowShortcutSettings, shortcut))
        {
            ShowShortcutSettingsDialog();
            e.Handled = true;
            return;
        }

        if (_isQuickSearchVisible
            && _quickSearchInteractionState.ShouldSuppressUnboundKey(
                e.Key,
                KeyboardShortcut.NormalizeModifiers(Keyboard.Modifiers)))
        {
            // 快捷键状态下未绑定的可输入字符保持无动作，不能继续落入仍有逻辑焦点的搜索框。
            e.Handled = true;
        }
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedPath(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!TryGetDroppedPath(e, out var path) || path is null)
        {
            ShowUserError("拖入的内容中没有支持的图片或文件夹。");
            return;
        }

        await OpenPathAsync(path);
    }

    private static bool TryGetDroppedPath(DragEventArgs e, out string? path)
    {
        path = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        path = paths.FirstOrDefault(static item =>
            Directory.Exists(item) ||
            (File.Exists(item) && ImageCatalog.IsSupportedMediaPath(item)));

        return path is not null;
    }

    private async void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsPointInsideElement(e.GetPosition(ThumbnailSidebar), ThumbnailSidebar))
        {
            return;
        }

        if (_isCropMode)
        {
            e.Handled = true;
            return;
        }

        if (_currentDocument is null)
        {
            return;
        }

        var factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        await ZoomAtAsync(e.GetPosition(ViewerSurface), _scale * factor);
        e.Handled = true;
    }

    private static bool IsPointInsideElement(Point point, FrameworkElement element)
    {
        return element.IsVisible
            && point.X >= 0
            && point.Y >= 0
            && point.X <= element.ActualWidth
            && point.Y <= element.ActualHeight;
    }

    private void ViewerSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (_currentDocument is null)
        {
            return;
        }

        if (_isCropMode)
        {
            BeginCropSelection(e.GetPosition(ViewerSurface));
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleLargeScreen();
            e.Handled = true;
            return;
        }

        _isDragging = true;
        _dragStartPoint = e.GetPosition(ViewerSurface);
        _dragStartOffset = new Point(BitmapView.OffsetX, BitmapView.OffsetY);
        ViewerSurface.CaptureMouse();
        e.Handled = true;
    }

    private void ViewerSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isCropDragging)
        {
            var position = e.GetPosition(ViewerSurface);
            UpdateCropSelection(position);
            _isCropDragging = false;
            _cropDragMode = CropDragMode.None;
            ViewerSurface.ReleaseMouseCapture();
            UpdateCropCursor(position);
            e.Handled = true;
            return;
        }

        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ViewerSurface.ReleaseMouseCapture();
        UseFinalScaling();
        e.Handled = true;
    }

    private void ViewerSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isCropDragging)
        {
            UpdateCropSelection(e.GetPosition(ViewerSurface));
            e.Handled = true;
            return;
        }

        if (_isCropMode)
        {
            UpdateCropCursor(e.GetPosition(ViewerSurface));
            e.Handled = true;
            return;
        }

        if (!_isDragging || _currentDocument is null)
        {
            return;
        }

        var current = e.GetPosition(ViewerSurface);
        var delta = current - _dragStartPoint;
        _isFitMode = false;
        _fitAllowsUpscale = false;
        UseInteractiveScaling();
        SetTransform(_scale, _dragStartOffset.X + delta.X, _dragStartOffset.Y + delta.Y);
    }

    private void ViewerSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (_isCropMode)
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            FitToWindow(allowUpscale: true, showZoomIndicator: true);
            e.Handled = true;
        }
    }

    private void ViewerSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isQuickSearchVisible)
        {
            ApplyQuickSearchPosition(new Point(
                _viewerSettings.QuickSearchOffsetX,
                _viewerSettings.QuickSearchOffsetY));
            UpdateQuickSearchBackdropViewbox();
        }

        if (_isCropMode)
        {
            ClearCropSelection();
        }

        if (_isFitMode)
        {
            FitToWindow();
            ScheduleFitToWindowAfterLayout();
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_isFitMode)
        {
            ScheduleFitToWindowAfterLayout();
        }

        UpdateCommands();
    }

    private async void OpenImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowOpenImageDialogAsync();
    }

    private async Task ShowOpenImageDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开图片",
            Filter = "图片/视频文件|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.apng;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico;*.cur;*.heic;*.heif;*.avif;*.avifs;*.jxr;*.wdp;*.hdp;*.hdr;*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.wmv;*.webm;*.mpeg;*.mpg;*.3gp;*.3g2;*.ts;*.m2ts;*.mts|图片文件|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.apng;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico;*.cur;*.heic;*.heif;*.avif;*.avifs;*.jxr;*.wdp;*.hdp;*.hdr|视频文件|*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.wmv;*.webm;*.mpeg;*.mpg;*.3gp;*.3g2;*.ts;*.m2ts;*.mts|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenPathAsync(dialog.FileName);
        }
    }

    private async void OpenFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowOpenFolderDialogAsync();
    }

    private async Task ShowOpenFolderDialogAsync()
    {
        var initialFolder = GetOpenFolderDialogInitialFolder();

        var dialog = new OpenFileDialog
        {
            Title = "打开目录 - 进入目标文件夹后直接点打开",
            Filter = "图片/视频文件|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.apng;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico;*.cur;*.heic;*.heif;*.avif;*.avifs;*.jxr;*.wdp;*.hdp;*.hdr;*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.wmv;*.webm;*.mpeg;*.mpg;*.3gp;*.3g2;*.ts;*.m2ts;*.mts|图片文件|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.apng;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico;*.cur;*.heic;*.heif;*.avif;*.avifs;*.jxr;*.wdp;*.hdp;*.hdr|视频文件|*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.wmv;*.webm;*.mpeg;*.mpg;*.3gp;*.3g2;*.ts;*.m2ts;*.mts|所有文件|*.*",
            FileName = "选择当前目录",
            CheckFileExists = false,
            ValidateNames = false,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            if (File.Exists(dialog.FileName))
            {
                if (!ImageCatalog.IsSupportedMediaPath(dialog.FileName))
                {
                    ShowUserError("请选择图片或视频文件，或进入目标目录后直接点击打开。");
                    return;
                }

                await OpenPathAsync(dialog.FileName);
                return;
            }

            var selectedFolder = Directory.Exists(dialog.FileName)
                ? dialog.FileName
                : Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrWhiteSpace(selectedFolder) || !Directory.Exists(selectedFolder))
            {
                ShowUserError("请选择有效目录，或选择目录中的图片/视频。");
                return;
            }

            await OpenFolderAsync(selectedFolder);
        }
    }

    private string GetOpenFolderDialogInitialFolder()
    {
        if (_catalog.CurrentPath is not null)
        {
            var currentFolder = Path.GetDirectoryName(_catalog.CurrentPath);
            if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            {
                return currentFolder;
            }
        }

        if (TryGetRememberedFolder(out var rememberedFolder))
        {
            return rememberedFolder;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    private bool TryGetRememberedFolder(out string folder)
    {
        folder = string.Empty;
        var savedFolder = _viewerSettings.LastOpenedFolder;
        if (string.IsNullOrWhiteSpace(savedFolder))
        {
            return false;
        }

        try
        {
            var fullFolder = Path.GetFullPath(savedFolder);
            if (!Directory.Exists(fullFolder))
            {
                return false;
            }

            folder = fullFolder;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RememberOpenedFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var fullFolder = Path.GetFullPath(folder);
            if (string.Equals(_viewerSettings.LastOpenedFolder, fullFolder, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _viewerSettings.LastOpenedFolder = fullFolder;
            SaveViewerSettings();
        }
        catch
        {
        }
    }

    private async void PreviousMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await NavigatePreviousAsync();
    }

    private async void NextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await NavigateNextAsync();
    }

    private void FitMenuItem_Click(object sender, RoutedEventArgs e)
    {
            FitToWindow(allowUpscale: true, showZoomIndicator: true);
    }

    private async void ActualSizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowActualSizeAsync();
    }

    private async void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ToggleCurrentFavoriteAsync();
    }

    private async void FavoritesViewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ToggleFavoritesViewAsync();
    }

    private void RotateLeftMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RotateCurrent(-90);
    }

    private void RotateRightMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RotateCurrent(90);
    }

    private async void SortByNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SetSortModeAsync(ImageSortMode.NameNatural);
    }

    private async void SortByNameDescendingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SetSortModeAsync(ImageSortMode.NameNaturalDescending);
    }

    private async void SortByNewestMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SetSortModeAsync(ImageSortMode.LastWriteTimeNewest);
    }

    private async void SortByOldestMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SetSortModeAsync(ImageSortMode.LastWriteTimeOldest);
    }

    private async void SortByLargestMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SetSortModeAsync(ImageSortMode.FileSizeLargest);
    }

    private async void SortBySmallestMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SetSortModeAsync(ImageSortMode.FileSizeSmallest);
    }

    private async void CropMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isCropMode)
        {
            ExitCropMode();
            return;
        }

        await BeginCropModeAsync(CropShape.Rectangle);
    }

    private async void CircleCropMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isCropMode)
        {
            ExitCropMode();
            return;
        }

        await BeginCropModeAsync(CropShape.Circle);
    }

    private async void CompressMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await CompressCurrentAsync();
    }

    private async void SetAsWallpaperMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SetCurrentAsWallpaperAsync();
    }

    private void OpenBatchCompressToolsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenBatchCompressTools();
    }

    private async void SaveVideoCoverMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SaveVideoCoverAsync();
    }

    private void CopyFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyCurrentFile();
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyCurrentPath();
    }

    private void CopyDiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyDiagnosticsInfo();
    }

    private void CopyNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyCurrentName();
    }

    private async void OpenContainingFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenContainingFolderAsync();
    }

    private void ToggleInfoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleInfoPanel();
    }

    private void ToggleThumbnailSidebarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleThumbnailSidebar();
    }

    private void ToggleThumbnailColumnsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleThumbnailColumns();
    }

    private void LargeScreenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleLargeScreen();
    }

    private async void FullScreenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ToggleFullScreenAsync();
    }

    private void ShortcutSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowShortcutSettingsDialog();
    }

    private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await DeleteCurrentAsync();
    }

    private async void BatchDeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await BatchDeleteCurrentDirectoryMediaAsync();
    }

    private void ToggleAnimationButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleAnimationPlayback();
    }

    private void RestartAnimationButton_Click(object sender, RoutedEventArgs e)
    {
        RestartAnimation();
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateCommands();
    }

    private async void ThumbnailCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThumbnailItem item)
        {
            return;
        }

        Focus();
        await NavigateToThumbnailAsync(item);
        UpdateCommands();
        e.Handled = true;
    }

    private void ThumbnailList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
        {
            if (!_isThumbnailScrollActive)
            {
                _isThumbnailScrollActive = true;
                RestartThumbnailViewportLoads();
            }

            _thumbnailScrollIdleTimer.Stop();
            _thumbnailScrollIdleTimer.Start();
            UpdateVisibleThumbnailRange(queueLoads: false, bufferRows: 1);
            return;
        }

        if (e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
        {
            QueueVisibleThumbnails();
        }
    }

    private void ThumbnailScrollIdleTimer_Tick(object? sender, EventArgs e)
    {
        _thumbnailScrollIdleTimer.Stop();
        _isThumbnailScrollActive = false;
        QueueVisibleThumbnails();
    }

    private void RestartThumbnailViewportLoads()
    {
        _thumbnailViewportCts?.Cancel();
        _thumbnailViewportCts?.Dispose();
        _thumbnailViewportCts = null;
        _thumbnailViewportGeneration++;

        List<ThumbnailItem> interruptedItems;
        lock (_thumbnailQueueLock)
        {
            interruptedItems = _queuedThumbnailPaths
                .Select(path => _thumbnailItemByPath.TryGetValue(path, out var item) ? item : null)
                .Where(static item => item is not null)
                .Cast<ThumbnailItem>()
                .ToList();
            _queuedThumbnailPaths.Clear();
        }

        foreach (var item in interruptedItems)
        {
            if (item.Thumbnail is null && !item.HasLoadFailed)
            {
                item.IsLoading = false;
                item.StatusText = string.Empty;
            }
        }

        if (_thumbnailCts is not null && !_thumbnailCts.IsCancellationRequested)
        {
            _thumbnailViewportCts = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailCts.Token);
        }
    }

    private void CopyCurrentFile()
    {
        var path = _catalog.CurrentPath;
        if (path is null)
        {
            return;
        }

        try
        {
            var files = new StringCollection { path };
            Clipboard.SetFileDropList(files);
            ShowBoundaryToast(_currentDocument?.IsVideo == true ? "视频文件已复制" : "图片文件已复制");
        }
        catch (Exception ex)
        {
            ShowUserError($"复制图片文件失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private void CopyCurrentPath()
    {
        var path = _catalog.CurrentPath;
        if (path is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(path);
            ShowBoundaryToast("文件路径已复制");
        }
        catch (Exception ex)
        {
            ShowUserError($"复制文件路径失败：\n{FriendlyException(ex)}", ex);
        }
    }

    internal void CopyDiagnosticsInfo()
    {
        try
        {
            Clipboard.SetText(CreateDiagnosticsInfo());
            ShowBoundaryToast("诊断信息已复制");
        }
        catch (Exception ex)
        {
            ShowUserError($"复制诊断信息失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private string CreateDiagnosticsInfo()
    {
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var currentPath = _catalog.CurrentPath;
        var currentDocument = _currentDocument;
        var memoryInfo = GC.GetGCMemoryInfo();
        var cacheSnapshot = _memoryCacheCoordinator.CaptureSnapshot(_viewerSettings.EnableLowMemoryProtection);
        var thumbnailDiskCacheStatistics = _thumbnailDiskCache.GetStatistics();

        using var process = Process.GetCurrentProcess();
        var includePrivatePaths = _viewerSettings.IncludePrivatePathsInDiagnostics;
        var builder = new StringBuilder();
        builder.AppendLine($"{AppInfo.Name} 诊断信息");
        builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"版本：{version}");
        builder.AppendLine($"程序：{FormatDiagnosticPath(Environment.ProcessPath ?? AppContext.BaseDirectory, includePrivatePaths)}");
        builder.AppendLine($"系统：{RuntimeInformation.OSDescription}");
        builder.AppendLine($"框架：{RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"进程架构：{RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"工作目录：{FormatDiagnosticPath(Environment.CurrentDirectory, includePrivatePaths, directory: true)}");
        builder.AppendLine();

        builder.AppendLine("当前状态");
        builder.AppendLine($"来源目录：{FormatDiagnosticPath(_catalog.SourceFolder, includePrivatePaths, directory: true)}");
        builder.AppendLine($"当前文件：{FormatDiagnosticPath(currentPath, includePrivatePaths)}");
        builder.AppendLine($"当前位置：{(_catalog.Count > 0 ? $"{_catalog.Index + 1:N0}/{_catalog.Count:N0}" : "0/0")}");
        builder.AppendLine($"排序：{GetSortModeDisplayName(_catalog.SortMode)}");
        builder.AppendLine($"收藏视图：{FormatOnOff(_isFavoritesView)}");
        builder.AppendLine($"缩略图栏：{FormatOnOff(_showThumbnailSidebar)}");
        builder.AppendLine($"适应窗口：{FormatOnOff(_isFitMode)}");
        builder.AppendLine($"缩放：{_scale:P0}");
        builder.AppendLine($"旋转：{_rotationDegrees}°");

        if (currentPath is not null)
        {
            AppendFileDiagnostics(builder, currentPath);
        }

        builder.AppendLine();
        builder.AppendLine("当前文档");
        if (currentDocument is null)
        {
            builder.AppendLine("状态：未加载");
        }
        else
        {
            builder.AppendLine($"格式：{currentDocument.FormatName}");
            builder.AppendLine($"尺寸：{currentDocument.PixelWidth:N0} x {currentDocument.PixelHeight:N0}");
            builder.AppendLine($"文件大小：{FormatFileSize(currentDocument.FileSize)}");
            builder.AppendLine($"修改时间：{currentDocument.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"视频：{FormatOnOff(currentDocument.IsVideo)}");
            builder.AppendLine($"动图：{FormatOnOff(currentDocument.IsAnimated)}");
            builder.AppendLine($"动图帧数：{currentDocument.AnimationFrames.Count:N0}");
            builder.AppendLine($"当前为预览图：{FormatOnOff(currentDocument.IsPreview)}");
            builder.AppendLine($"当前为超大图安全预览：{FormatOnOff(currentDocument.IsLargeImagePreview)}");
            if (currentDocument.IsLargeImagePreview)
            {
                builder.AppendLine(
                    $"安全预览尺寸：{currentDocument.Bitmap.PixelWidth:N0} x {currentDocument.Bitmap.PixelHeight:N0}");
            }
            builder.AppendLine($"BitmapSource：{currentDocument.Bitmap.PixelWidth:N0} x {currentDocument.Bitmap.PixelHeight:N0}，{currentDocument.Bitmap.Format}");
        }

        builder.AppendLine();
        builder.AppendLine("缓存与内存");
        builder.AppendLine($"主图缓存上限：{FormatFileSize(cacheSnapshot.MainImageBudgetBytes)}");
        builder.AppendLine($"主图缓存估算占用：{FormatFileSize(cacheSnapshot.MainImageEstimatedBytes)}");
        builder.AppendLine($"主图缓存数量：{_cache.Count:N0}");
        builder.AppendLine($"显示预览缓存上限：{FormatFileSize(cacheSnapshot.PreviewBudgetBytes)}");
        builder.AppendLine($"显示预览缓存估算占用：{FormatFileSize(cacheSnapshot.PreviewEstimatedBytes)}");
        builder.AppendLine($"内存缓存自动调整：{FormatOnOff(_viewerSettings.UseAutomaticCacheSizing)}");
        builder.AppendLine($"邻图预加载：前向 {_mainPreloadForwardRadius:N0}，反向 {_mainPreloadOppositeRadius:N0}");
        builder.AppendLine($"缩略图加载并发：{_thumbnailLoadConcurrency:N0}");
        builder.AppendLine($"缩略图内存缓存数量：{_thumbnailCache.Count:N0}/{MaxThumbnailCacheItems:N0}");
        builder.AppendLine($"已创建缩略图模型：{_thumbnailItems.CreatedCount:N0}/{_thumbnailItems.Count:N0}");
        builder.AppendLine($"缩略图磁盘缓存：{FormatOnOff(_viewerSettings.UseThumbnailDiskCache)}");
        builder.AppendLine($"缩略图磁盘缓存上限：{FormatFileSize(_thumbnailDiskCache.MaxBytes)}");
        builder.AppendLine($"缩略图磁盘缓存占用：{FormatFileSize(thumbnailDiskCacheStatistics.TotalBytes)}（{thumbnailDiskCacheStatistics.FileCount:N0} 项）");
        builder.AppendLine($"缩略图磁盘缓存目录：{FormatDiagnosticPath(_thumbnailDiskCache.Folder, includePrivatePaths, directory: true)}");
        builder.AppendLine($"低内存保护：{FormatOnOff(_viewerSettings.EnableLowMemoryProtection)}");
        builder.AppendLine($"当前是否降低后台加载：{FormatOnOff(cacheSnapshot.IsUnderPressure)}");
        builder.AppendLine($"GC 当前内存：{FormatFileSize(GC.GetTotalMemory(forceFullCollection: false))}");
        builder.AppendLine($"GC 内存负载：{FormatFileSize(memoryInfo.MemoryLoadBytes)}");
        builder.AppendLine($"GC 高内存阈值：{FormatFileSize(memoryInfo.HighMemoryLoadThresholdBytes)}");
        builder.AppendLine($"运行环境可用内存：{FormatFileSize(cacheSnapshot.TotalAvailableMemoryBytes)}");
        builder.AppendLine($"进程工作集：{FormatFileSize(process.WorkingSet64)}");
        builder.AppendLine($"进程私有内存：{FormatFileSize(process.PrivateMemorySize64)}");

        builder.AppendLine();
        builder.AppendLine("设置");
        builder.AppendLine($"启动时打开上次目录：{FormatOnOff(_viewerSettings.OpenLastFolderOnStartup)}");
        builder.AppendLine($"上次目录：{FormatDiagnosticPath(_viewerSettings.LastOpenedFolder, includePrivatePaths, directory: true)}");
        builder.AppendLine($"复用已有窗口：{FormatOnOff(_viewerSettings.ReuseExistingWindow)}");
        builder.AppendLine($"记住主窗口状态：{FormatOnOff(_viewerSettings.RememberMainWindowPlacement)}");
        builder.AppendLine($"自动监视目录变化：{FormatOnOff(_viewerSettings.WatchFolderChanges)}");
        builder.AppendLine($"切图保持缩放：{FormatOnOff(_viewerSettings.KeepViewStateWhenNavigating)}");
        builder.AppendLine($"显示动图控制：{FormatOnOff(_viewerSettings.ShowAnimationControls)}");
        builder.AppendLine($"显示操作提醒：{FormatOnOff(_viewerSettings.ShowOperationNotifications)}");
        builder.AppendLine($"空闲时后台加载当前原图：{FormatOnOff(_viewerSettings.LoadFullResolutionWhenIdle)}");

        if (!string.IsNullOrWhiteSpace(_lastInlineErrorMessage))
        {
            builder.AppendLine();
            builder.AppendLine("最近打开错误");
            builder.AppendLine(_lastInlineErrorMessage);
        }

        builder.AppendLine();
        builder.AppendLine($"日志：{FormatDiagnosticPath(ErrorLog.LogPath, includePrivatePaths)}");
        return builder.ToString();
    }

    private static string FormatDiagnosticPath(string? path, bool includePrivatePaths, bool directory = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(无)";
        }

        if (includePrivatePaths)
        {
            return path;
        }

        if (directory)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? "(已隐藏)" : $"…\\{name}";
        }

        return $"…\\{Path.GetFileName(path)}";
    }

    private static void AppendFileDiagnostics(StringBuilder builder, string path)
    {
        builder.AppendLine();
        builder.AppendLine("当前文件");
        try
        {
            var fileInfo = new FileInfo(path);
            builder.AppendLine($"存在：{FormatOnOff(fileInfo.Exists)}");
            if (fileInfo.Exists)
            {
                builder.AppendLine($"扩展名：{fileInfo.Extension}");
                builder.AppendLine($"文件大小：{FormatFileSize(fileInfo.Length)}");
                builder.AppendLine($"修改时间：{fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                builder.AppendLine($"只读：{FormatOnOff(fileInfo.IsReadOnly)}");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"读取文件信息失败：{FriendlyException(ex)}");
        }
    }

    private static string FormatOnOff(bool value)
    {
        return value ? "开" : "关";
    }

    private void CopyCurrentName()
    {
        var path = _catalog.CurrentPath;
        if (path is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(Path.GetFileName(path));
            ShowBoundaryToast("文件名已复制");
        }
        catch (Exception ex)
        {
            ShowUserError($"复制文件名失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private async Task OpenContainingFolderAsync()
    {
        var path = _catalog.CurrentPath;
        if (path is null)
        {
            return;
        }

        try
        {
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                ShowUserError("所在文件夹不存在或无法访问。");
                return;
            }

            OpenExplorerSelection(path, folder);
            if (File.Exists(path))
            {
                await EnsureExplorerSelectionVisibleAsync(folder, path);
            }

            ShowBoundaryToast("已打开文件位置");
        }
        catch (Exception ex)
        {
            ShowUserError($"打开文件位置失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private static void OpenExplorerSelection(string path, string folder)
    {
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{folder}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arguments,
            UseShellExecute = true,
        });
    }

    private static async Task<bool> EnsureExplorerSelectionVisibleAsync(string folder, string path)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(120);
            if (TryEnsureExplorerSelectionVisible(folder, path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryEnsureExplorerSelectionVisible(string folder, string path)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return false;
            }

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            var windows = shell.Windows();
            var fileName = Path.GetFileName(path);
            for (var i = 0; i < windows.Count; i++)
            {
                try
                {
                    var window = windows.Item(i);
                    var view = window.Document;
                    var viewFolder = (string)view.Folder.Self.Path;
                    if (!string.Equals(viewFolder, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var item = view.Folder.ParseName(fileName);
                    if (item is null)
                    {
                        continue;
                    }

                    view.SelectItem(item, ShellSelectItemFlags);
                    return true;
                }
                catch
                {
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static System.Windows.Automation.ScrollPattern? FindExplorerScrollPattern(IntPtr explorerHwnd)
    {
        var root = System.Windows.Automation.AutomationElement.FromHandle(explorerHwnd);
        var candidates = root.FindAll(
            System.Windows.Automation.TreeScope.Descendants,
            new System.Windows.Automation.OrCondition(
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.List),
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.DataGrid),
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Pane)));

        foreach (System.Windows.Automation.AutomationElement candidate in candidates)
        {
            if (candidate.TryGetCurrentPattern(
                    System.Windows.Automation.ScrollPattern.Pattern,
                    out var pattern)
                && pattern is System.Windows.Automation.ScrollPattern scrollPattern
                && scrollPattern.Current.VerticallyScrollable)
            {
                return scrollPattern;
            }
        }

        return null;
    }

    private async Task DeleteCurrentAsync()
    {
        var path = _catalog.CurrentPath;
        if (path is null)
        {
            return;
        }

        if (_viewerSettings.ConfirmDeleteToRecycleBin)
        {
            var confirm = MessageBox.Show(
                this,
                $"确定将当前文件删除到回收站吗？\n\n{Path.GetFileName(path)}",
                "删除到回收站",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            RemoveCachedMedia(path);
            CancelCatalogCompletion();
            if (_favorites.Remove(path))
            {
                SaveFavorites();
            }

            _catalog.RemoveCurrent();
            RefreshThumbnailItems();

            if (_catalog.Count > 0)
            {
                EnterDefaultFitMode();
                await LoadCurrentAsync();
                ShowBoundaryToast("已删除到回收站");
            }
            else
            {
                ClearImage();
                ShowBoundaryToast("已删除到回收站");
            }
        }
        catch (Exception ex)
        {
            ShowUserError($"删除失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private async Task BatchDeleteCurrentDirectoryMediaAsync()
    {
        if (_isFavoritesView)
        {
            ShowUserError("收藏视图下不支持批量删除。请先退出收藏视图，再对当前目录执行批量删除。");
            return;
        }

        var folder = _catalog.SourceFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            ShowUserError("请先打开一个有效目录，再使用批量删除。");
            return;
        }

        var candidates = EnumerateBatchDeleteCandidates(folder, includeSubfolders: false);
        var summary = candidates.Count == 0
            ? "当前目录直属媒体文件：0 个。\n如需删除子目录下的媒体文件，请勾选下面的选项。"
            : CreateBatchDeleteSummary(candidates);
        var preview = new BatchDeletePreviewWindow(folder, summary)
        {
            Owner = this,
        };
        if (preview.ShowDialog() != true)
        {
            return;
        }

        var includeSubfolders = preview.IncludeSubfolders;
        if (includeSubfolders)
        {
            LoadingText.Text = "正在扫描子目录...";
            LoadingPanel.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;

            candidates = await Task.Run(() => EnumerateBatchDeleteCandidates(folder, includeSubfolders: true));

            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
        }

        if (candidates.Count == 0)
        {
            ShowBoundaryToast(includeSubfolders
                ? "当前目录及子目录没有可删除的图片/动图/视频"
                : "当前目录没有可删除的图片/动图/视频");
            return;
        }

        try
        {
            LoadingText.Text = "正在批量删除...";
            LoadingPanel.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;

            var result = await Task.Run(() => DeleteFilesToRecycleBin(candidates));
            var logPath = WriteBatchDeleteLog(folder, includeSubfolders, candidates, result);
            var favoritesChanged = false;
            foreach (var path in result.DeletedItems.Select(static item => item.Path))
            {
                RemoveCachedMedia(path);
                _thumbnailCache.Remove(path);
                favoritesChanged |= _favorites.Remove(path);
            }

            if (favoritesChanged)
            {
                SaveFavorites();
            }

            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;

            if (Directory.Exists(folder))
            {
                CancelCatalogCompletion();
                _catalog.LoadFromFolder(folder);
                RefreshThumbnailItems();
                if (_catalog.Count > 0)
                {
                    EnterDefaultFitMode();
                    await LoadCurrentAsync();
                }
                else
                {
                    ClearImage();
                }
            }
            else
            {
                ClearImage();
            }

            ShowBoundaryToast(result.FailedCount == 0
                ? $"已删除 {result.DeletedCount:N0} 个文件到回收站"
                : $"已删除 {result.DeletedCount:N0} 个，失败 {result.FailedCount:N0} 个");

            if (_viewerSettings.ShowOperationNotifications || result.FailedCount > 0)
            {
                var resultIcon = result.FailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
                MessageBox.Show(
                    this,
                    $"批量删除完成。\n\n{CreateBatchDeleteSummary(candidates)}\n\n成功：{result.DeletedCount:N0} 个\n失败：{result.FailedCount:N0} 个\n日志：{logPath}",
                    "批量删除结果",
                    MessageBoxButton.OK,
                    resultIcon);
            }
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
            ShowUserError($"批量删除失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private static BatchDeleteResult DeleteFilesToRecycleBin(IReadOnlyList<BatchDeleteCandidate> candidates)
    {
        var deleted = new List<BatchDeleteCandidate>();
        var failed = new List<BatchDeleteFailure>();
        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate.Path))
                {
                    failed.Add(new BatchDeleteFailure(candidate, "文件不存在。"));
                    continue;
                }

                FileSystem.DeleteFile(candidate.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                deleted.Add(candidate);
            }
            catch (Exception ex)
            {
                failed.Add(new BatchDeleteFailure(candidate, FriendlyException(ex)));
            }
        }

        return new BatchDeleteResult(deleted, failed);
    }

    private static List<BatchDeleteCandidate> EnumerateBatchDeleteCandidates(string folder, bool includeSubfolders)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSubfolders,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
            };

            return Directory.EnumerateFiles(folder, "*", options)
                .Where(ImageCatalog.IsSupportedMediaPath)
                .Select(static path => new BatchDeleteCandidate(path, ImageCatalog.IsSupportedVideoPath(path)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string CreateBatchDeleteSummary(IReadOnlyList<BatchDeleteCandidate> candidates)
    {
        var imageCount = candidates.Count(static item => !item.IsVideo);
        var videoCount = candidates.Count(static item => item.IsVideo);
        var lines = new List<string>
        {
            $"共 {candidates.Count:N0} 个媒体文件：图片/动图 {imageCount:N0} 个，视频 {videoCount:N0} 个。",
        };

        var imageExtensions = FormatExtensionCounts(candidates.Where(static item => !item.IsVideo).Select(static item => item.Path));
        if (!string.IsNullOrWhiteSpace(imageExtensions))
        {
            lines.Add($"图片/动图类型：{imageExtensions}");
        }

        var videoExtensions = FormatExtensionCounts(candidates.Where(static item => item.IsVideo).Select(static item => item.Path));
        if (!string.IsNullOrWhiteSpace(videoExtensions))
        {
            lines.Add($"视频类型：{videoExtensions}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatExtensionCounts(IEnumerable<string> paths)
    {
        return string.Join(
            "，",
            paths
                .Select(static path => Path.GetExtension(path).ToLowerInvariant())
                .Where(static extension => !string.IsNullOrWhiteSpace(extension))
                .GroupBy(static extension => extension, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static group => group.Count())
                .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => $"{group.Key} {group.Count():N0}"));
    }

    private static string WriteBatchDeleteLog(
        string folder,
        bool includeSubfolders,
        IReadOnlyList<BatchDeleteCandidate> candidates,
        BatchDeleteResult result)
    {
        var logFolder = Path.Combine(
            AppInfo.LocalDataFolder,
            "delete-logs");
        Directory.CreateDirectory(logFolder);

        var logPath = Path.Combine(logFolder, $"batch-delete_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        if (File.Exists(logPath))
        {
            logPath = Path.Combine(logFolder, $"batch-delete_{DateTime.Now:yyyyMMdd_HHmmssfff}.log");
        }

        var lines = new List<string>
        {
            $"{AppInfo.Name} 批量删除日志",
            $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"目录：{folder}",
            $"范围：{(includeSubfolders ? "当前目录及子目录" : "当前目录")}",
            CreateBatchDeleteSummary(candidates),
            $"结果：成功 {result.DeletedCount:N0} 个，失败 {result.FailedCount:N0} 个",
            string.Empty,
            "删除成功：",
        };

        foreach (var item in result.DeletedItems)
        {
            lines.Add($"[成功] {FormatBatchDeleteKind(item)}  {item.Path}");
        }

        lines.Add(string.Empty);
        lines.Add("删除失败：");
        foreach (var item in result.FailedItems)
        {
            lines.Add($"[失败] {FormatBatchDeleteKind(item.Candidate)}  {item.Candidate.Path}  原因：{item.Reason}");
        }

        File.WriteAllLines(logPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return logPath;
    }

    private static string FormatBatchDeleteKind(BatchDeleteCandidate candidate)
    {
        return candidate.IsVideo ? "视频" : "图片/动图";
    }

    private sealed record BatchDeleteFailure(BatchDeleteCandidate Candidate, string Reason);

    private sealed class BatchDeleteResult(
        IReadOnlyList<BatchDeleteCandidate> deletedItems,
        IReadOnlyList<BatchDeleteFailure> failedItems)
    {
        public IReadOnlyList<BatchDeleteCandidate> DeletedItems { get; } = deletedItems;

        public IReadOnlyList<BatchDeleteFailure> FailedItems { get; } = failedItems;

        public int DeletedCount => DeletedItems.Count;

        public int FailedCount => FailedItems.Count;
    }

    private async Task ToggleCurrentFavoriteAsync()
    {
        var path = _catalog.CurrentPath;
        if (path is null)
        {
            return;
        }

        var wasFavorite = _favorites.IsFavorite(path);
        var changed = wasFavorite ? _favorites.Remove(path) : _favorites.Add(path);
        if (!changed)
        {
            return;
        }

        SaveFavorites();
        UpdateThumbnailFavorite(path, !wasFavorite);
        UpdateInfoPanel();
        UpdateCommands();

        if (wasFavorite && _isFavoritesView)
        {
            await LoadFavoritesViewAsync(_catalog.CurrentPath, showEmptyMessage: false);
        }

        ShowBoundaryToast(wasFavorite ? "已取消收藏" : "已加入收藏");
    }

    private async Task ToggleFavoritesViewAsync()
    {
        if (_isFavoritesView)
        {
            await ExitFavoritesViewAsync();
            return;
        }

        await LoadFavoritesViewAsync(_catalog.CurrentPath, showEmptyMessage: true);
    }

    private async Task LoadFavoritesViewAsync(string? preferredPath, bool showEmptyMessage)
    {
        StopFolderWatcher();
        CancelCatalogCompletion();
        if (_favorites.RemoveMissingOrUnsupported())
        {
            SaveFavorites();
        }

        var paths = _favorites.GetExistingMediaPaths();
        if (paths.Count == 0)
        {
            _isFavoritesView = false;
            _catalog.LoadFromPaths([]);
            RefreshThumbnailItems();
            ClearImage();
            if (showEmptyMessage)
            {
                ShowUserError("还没有收藏文件。打开图片或视频后，可以先用“加入收藏”。");
            }
            else
            {
                ShowBoundaryToast("收藏列表已空");
            }

            return;
        }

        _isFavoritesView = true;
        _catalog.LoadFromPaths(paths, preferredPath);
        RefreshThumbnailItems();
        EnterDefaultFitMode();
        await LoadCurrentAsync();
        ShowBoundaryToast($"收藏：{_catalog.Count:N0} 个文件");
    }

    private async Task ExitFavoritesViewAsync()
    {
        var currentPath = _catalog.CurrentPath;
        _isFavoritesView = false;

        if (currentPath is not null && File.Exists(currentPath) && ImageCatalog.IsSupportedMediaPath(currentPath))
        {
            await OpenSingleFileAsync(currentPath, rememberFolder: false);
        }
        else
        {
            CancelCatalogCompletion();
            ClearImage();
        }

        ShowBoundaryToast("已退出收藏视图");
    }

    private void RotateCurrent(int deltaDegrees)
    {
        if (_currentDocument is null)
        {
            return;
        }

        ExitCropMode();
        _rotationDegrees = NormalizeRotation(_rotationDegrees + deltaDegrees);
        if (_isPreviewDisplay && _previewDisplayBitmap is not null && _isFitMode)
        {
            SetDisplayPreviewSource(_currentDocument.Path, _previewDisplayBitmap);
        }
        else
        {
            StartAnimation(_currentDocument);
        }

        if (_isFitMode)
        {
            FitToWindow();
        }
        else
        {
            CenterAtScale(_scale);
        }

        UpdateInfoPanel();
        UpdateCommands();
        ShowBoundaryToast(_rotationDegrees == 0 ? "已恢复原方向" : $"已旋转 {_rotationDegrees}°");
    }

    private async Task CycleSortModeAsync()
    {
        var currentIndex = Array.IndexOf(SortModeCycle, _catalog.SortMode);
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % SortModeCycle.Length;
        await SetSortModeAsync(SortModeCycle[nextIndex]);
    }

    private async Task SetSortModeAsync(ImageSortMode sortMode)
    {
        var currentPath = _catalog.CurrentPath;
        var shouldRestartCatalogCompletion = !_isFavoritesView && _catalog.IsSingleFileCatalog && currentPath is not null;
        _catalog.SortMode = sortMode;
        SaveViewerSettings();

        if (_isFavoritesView)
        {
            await LoadFavoritesViewAsync(currentPath, showEmptyMessage: false);
        }
        else
        {
            CancelCatalogCompletion();
            _catalog.ResortKeepingCurrent();
            RefreshThumbnailItems();
            UpdateInfoPanel();
            UpdateWindowTitle(_catalog.CurrentPath);
            UpdateCommands();
            if (shouldRestartCatalogCompletion && currentPath is not null)
            {
                StartCatalogCompletion(currentPath);
            }
        }

        ShowBoundaryToast($"排序：{GetSortModeDisplayName(sortMode)}");
    }

    private static string GetSortModeDisplayName(ImageSortMode sortMode)
    {
        return sortMode switch
        {
            ImageSortMode.NameNaturalDescending => "按名称倒序",
            ImageSortMode.LastWriteTimeNewest => "按修改时间（新到旧）",
            ImageSortMode.LastWriteTimeOldest => "按修改时间（旧到新）",
            ImageSortMode.FileSizeLargest => "按文件大小（大到小）",
            ImageSortMode.FileSizeSmallest => "按文件大小（小到大）",
            _ => "按名称",
        };
    }

    private void OpenBatchCompressTools()
    {
        try
        {
            var dialog = new BatchCompressWindow(GetBatchCompressInitialPath())
            {
                Owner = this,
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowUserError($"打开批量压缩窗口失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private string? GetBatchCompressInitialPath()
    {
        if (!_isFavoritesView
            && !string.IsNullOrWhiteSpace(_catalog.SourceFolder)
            && Directory.Exists(_catalog.SourceFolder))
        {
            return _catalog.SourceFolder;
        }

        var currentPath = _catalog.CurrentPath;
        if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
        {
            return currentPath;
        }

        var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return !string.IsNullOrWhiteSpace(picturesFolder) && Directory.Exists(picturesFolder)
            ? picturesFolder
            : null;
    }

    private static string GetBatchCompressScriptPath()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "tools", "batch-compress.ps1");
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "batch-compress.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return outputPath;
    }

    private void SaveFavorites()
    {
        try
        {
            _favorites.Save();
        }
        catch (Exception ex)
        {
            ShowUserError($"保存收藏失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private bool IsCurrentFavorite()
    {
        return _catalog.CurrentPath is { } path && _favorites.IsFavorite(path);
    }

    private void UpdateThumbnailFavorite(string path, bool isFavorite)
    {
        if (_thumbnailItemByPath.TryGetValue(path, out var item))
        {
            item.IsFavorite = isFavorite;
        }
    }

    private async Task CompressCurrentAsync()
    {
        if (_currentDocument is null || BitmapView.Source is null)
        {
            return;
        }

        if (_currentDocument.IsVideo)
        {
            ShowUserError("视频只显示封面预览，不能作为图片压缩。");
            return;
        }

        if (_currentDocument.IsAnimated)
        {
            ShowUserError("暂不支持压缩动图。请先将动图导出为静态图片后再压缩。");
            return;
        }

        ExitCropMode();
        if (!await EnsureFullResolutionDocumentAsync())
        {
            return;
        }

        var dialog = new CompressImageWindow(
            _currentDocument.Path,
            _currentDocument.FileSize,
            _currentDocument.Bitmap)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || dialog.Options is null)
        {
            return;
        }

        try
        {
            var originalBytes = _currentDocument.FileSize;
            var result = ImageCompressor.Save(_currentDocument.Bitmap, dialog.Options);
            RemoveCachedMedia(result.OutputPath);
            await ApplySavedFileOpenBehaviorAsync(result.OutputPath);
            ShowBoundaryToast($"压缩完成：{FormatFileSize(originalBytes)} → {FormatFileSize(result.OutputBytes)}");
        }
        catch (Exception ex)
        {
            ShowUserError($"压缩图片失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private void ToggleInfoPanel()
    {
        _showInfo = !_showInfo;
        UpdateInfoPanel();
    }

    private async Task ApplySavedFileOpenBehaviorAsync(string path)
    {
        try
        {
            if (_viewerSettings.SavedFileOpenBehavior != SavedFileOpenBehavior.CurrentWindow)
            {
                await RefreshCurrentCatalogForSavedFileAsync(path);
            }

            switch (_viewerSettings.SavedFileOpenBehavior)
            {
                case SavedFileOpenBehavior.CurrentWindow:
                    _isFavoritesView = false;
                    await OpenSingleFileAsync(path, rememberFolder: false);
                    break;
                case SavedFileOpenBehavior.NewWindow:
                    OpenPathInNewWindow(path);
                    break;
                case SavedFileOpenBehavior.None:
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowUserError($"文件已保存，但打开保存文件失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private async Task RefreshCurrentCatalogForSavedFileAsync(string path)
    {
        if (_isFavoritesView
            || string.IsNullOrWhiteSpace(_catalog.SourceFolder)
            || string.IsNullOrWhiteSpace(_catalog.CurrentPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var outputFolder = Path.GetDirectoryName(fullPath);
        if (!string.Equals(outputFolder, _catalog.SourceFolder, StringComparison.OrdinalIgnoreCase)
            || !_catalog.AddOrUpdateExistingMediaPath(fullPath))
        {
            return;
        }

        var shouldReloadCurrent = string.Equals(_catalog.CurrentPath, fullPath, StringComparison.OrdinalIgnoreCase);
        RefreshThumbnailItems();
        UpdateInfoPanel();
        UpdateWindowTitle(_catalog.CurrentPath);
        UpdateCommands();

        if (shouldReloadCurrent)
        {
            EnterDefaultFitMode();
            await LoadCurrentAsync();
        }
    }

    private static void OpenPathInNewWindow(string path)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("无法确定程序路径。");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = processPath,
            Arguments = $"--new-window {QuoteProcessArgument(path)}",
            UseShellExecute = false,
        });
    }

    private static string QuoteProcessArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private void ToggleThumbnailSidebar()
    {
        _showThumbnailSidebar = !_showThumbnailSidebar;
        UpdateThumbnailSidebarVisibility();
        if (!_showThumbnailSidebar)
        {
            RefreshThumbnailItems();
        }
        else if (_thumbnailItemsNeedRefresh)
        {
            RefreshThumbnailItems();
        }
        else if (ShouldShowQuickSearchThumbnailResults())
        {
            ApplyQuickSearchThumbnailFilter();
        }
        else if (_thumbnailRowsNeedRefresh)
        {
            RebuildThumbnailRows();
            UpdateThumbnailSelection();
            QueueVisibleThumbnails();
        }

        UpdateCommands();
        SaveViewerSettings();

        if (_isFitMode)
        {
            Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Loaded);
        }
    }

    private void UpdateThumbnailSidebarVisibility()
    {
        ThumbnailSidebar.Visibility = _showThumbnailSidebar ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailSidebarColumn.Width = _showThumbnailSidebar
            ? new GridLength(196)
            : new GridLength(0);
    }

    private void SaveViewerSettings()
    {
        try
        {
            _viewerSettings.ShowThumbnailSidebar = _showThumbnailSidebar;
            _viewerSettings.UseDoubleThumbnailColumns = _useDoubleThumbnailColumns;
            _viewerSettings.SortMode = _catalog.SortMode;
            _viewerSettings.Save();
        }
        catch (Exception ex)
        {
            ShowUserError($"保存界面设置失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private void ToggleThumbnailColumns()
    {
        _useDoubleThumbnailColumns = !_useDoubleThumbnailColumns;
        OnPropertyChanged(nameof(ThumbnailImageHeight));
        OnPropertyChanged(nameof(ThumbnailItemWidth));
        OnPropertyChanged(nameof(ThumbnailItemPadding));
        OnPropertyChanged(nameof(ThumbnailItemMargin));
        OnPropertyChanged(nameof(ThumbnailColumnCount));
        RebuildThumbnailRows();
        UpdateThumbnailSidebarVisibility();
        UpdateThumbnailSelection();
        QueueVisibleThumbnails();
        UpdateCommands();
        SaveViewerSettings();

        if (_isFitMode)
        {
            Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Loaded);
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private async Task NavigateToThumbnailAsync(ThumbnailItem item)
    {
        var previousIndex = _catalog.Index;
        if (!_catalog.MoveTo(item.Path))
        {
            return;
        }

        _navigationDirection = Math.Sign(item.Index - previousIndex);

        PrepareForNavigation();
        await LoadCurrentAsync();
    }

    private void CancelFolderLoadButton_Click(object sender, RoutedEventArgs e)
    {
        CancelFolderLoad();
        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    private void CancelCatalogCompletionButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCatalogCompletion();
        ShowBoundaryToast("已取消目录补齐");
    }

    private void RefreshThumbnailItems()
    {
        _navigationDirection = 0;
        CancelQuickSearchQuery();
        _quickSearchResult = null;
        _thumbnailScrollIdleTimer.Stop();
        _isThumbnailScrollActive = false;
        _thumbnailViewportCts?.Cancel();
        _thumbnailViewportCts?.Dispose();
        _thumbnailViewportCts = null;
        _thumbnailViewportGeneration++;
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailViewportCts?.Cancel();
        _thumbnailViewportCts?.Dispose();
        _thumbnailViewportCts = null;
        _thumbnailCts = null;
        _thumbnailGeneration++;

        lock (_thumbnailQueueLock)
        {
            _queuedThumbnailPaths.Clear();
        }

        if (!_showThumbnailSidebar)
        {
            _thumbnailItemsNeedRefresh = true;
            _thumbnailRowsNeedRefresh = false;
            _thumbnailSearchItems = null;
            _thumbnailItems = LazyIndexedList<ThumbnailItem>.Empty;
            _thumbnailItemByPath.Clear();
            _thumbnailRows = VirtualizedRowCollection<ThumbnailItem>.Empty;
            ThumbnailList.ItemsSource = _thumbnailRows;
            _selectedThumbnailItem = null;
            _thumbnailVisibleStartIndex = 0;
            _thumbnailVisibleEndIndex = -1;
            UpdateThumbnailSidebarTitle();
            return;
        }

        _thumbnailCts = new CancellationTokenSource();
        _thumbnailViewportCts = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailCts.Token);
        _thumbnailItemsNeedRefresh = false;
        _thumbnailScrollViewer = null;
        _thumbnailSearchItems = null;
        _thumbnailRowsNeedRefresh = false;
        _selectedThumbnailItem = null;
        _thumbnailVisibleStartIndex = 0;
        _thumbnailVisibleEndIndex = -1;
        _thumbnailItemByPath.Clear();
        _thumbnailItems = new LazyIndexedList<ThumbnailItem>(
            _catalog.Paths.Count,
            index =>
            {
                var path = _catalog.Paths[index];
                var item = new ThumbnailItem(path, index, _favorites.IsFavorite(path));
                _thumbnailItemByPath[path] = item;
                return item;
            },
            maxCachedItems: 2048,
            canEvict: static item => !item.IsSelected && !item.IsLoading,
            onEvicted: item =>
            {
                if (_thumbnailItemByPath.TryGetValue(item.Path, out var current)
                    && ReferenceEquals(current, item))
                {
                    _thumbnailItemByPath.Remove(item.Path);
                }
            });

        if (_catalog.Index >= 0 && _catalog.Index < _thumbnailItems.Count)
        {
            _ = _thumbnailItems[_catalog.Index];
        }

        if (ShouldShowQuickSearchThumbnailResults())
        {
            RebuildThumbnailRows();
            UpdateThumbnailSidebarTitle();
            UpdateThumbnailSelection();
            QueueVisibleThumbnails();
            _quickSearchDebounceTimer.Stop();
            _quickSearchDebounceTimer.Start();
        }
        else
        {
            RebuildThumbnailRows();
            UpdateThumbnailSidebarTitle();
            UpdateThumbnailSelection();
            QueueVisibleThumbnails();
        }
    }

    private void RebuildThumbnailRows()
    {
        var displayItems = GetDisplayedThumbnailItems();
        var columns = GetThumbnailColumnCount();
        _thumbnailRows = new VirtualizedRowCollection<ThumbnailItem>(displayItems, columns);
        ThumbnailList.ItemsSource = _thumbnailRows;
        _thumbnailRowsNeedRefresh = false;
    }

    private IReadOnlyList<ThumbnailItem> GetDisplayedThumbnailItems()
    {
        return _thumbnailSearchItems is { } searchItems
            ? searchItems
            : _thumbnailItems;
    }

    private int GetThumbnailColumnCount()
    {
        return ThumbnailColumnCount;
    }

    private void QueueVisibleThumbnails()
    {
        UpdateVisibleThumbnailRange(queueLoads: true, ThumbnailVisibleBufferRows);
    }

    private void UpdateVisibleThumbnailRange(bool queueLoads, int bufferRows)
    {
        var displayItems = GetDisplayedThumbnailItems();
        if (displayItems.Count == 0 || _thumbnailViewportCts is null || !_showThumbnailSidebar)
        {
            _thumbnailVisibleStartIndex = 0;
            _thumbnailVisibleEndIndex = -1;
            return;
        }

        var columns = GetThumbnailColumnCount();
        var firstRow = 0;
        var visibleRows = 18;
        var scrollViewer = GetThumbnailScrollViewer();
        if (scrollViewer is not null)
        {
            firstRow = Math.Max(0, (int)Math.Floor(scrollViewer.VerticalOffset));
            if (!double.IsNaN(scrollViewer.ViewportHeight) && scrollViewer.ViewportHeight > 0)
            {
                visibleRows = Math.Max(1, (int)Math.Ceiling(scrollViewer.ViewportHeight));
            }
        }

        var startRow = Math.Max(0, firstRow - bufferRows);
        var endRow = Math.Min(_thumbnailRows.Count - 1, firstRow + visibleRows + bufferRows);
        if (endRow < startRow)
        {
            return;
        }

        var startIndex = startRow * columns;
        var endIndex = Math.Min(displayItems.Count - 1, ((endRow + 1) * columns) - 1);
        _thumbnailVisibleStartIndex = int.MaxValue;
        _thumbnailVisibleEndIndex = -1;
        for (var index = startIndex; index <= endIndex; index++)
        {
            _thumbnailVisibleStartIndex = Math.Min(_thumbnailVisibleStartIndex, displayItems[index].Index);
            _thumbnailVisibleEndIndex = Math.Max(_thumbnailVisibleEndIndex, displayItems[index].Index);
        }

        PrunePendingThumbnailLoadsOutsideActiveRange();

        if (!queueLoads)
        {
            return;
        }

        for (var index = startIndex; index <= endIndex; index++)
        {
            QueueThumbnailLoad(displayItems[index]);
        }
    }

    private void QueueThumbnailLoadsAroundIndex(IReadOnlyList<ThumbnailItem> items, int centerIndex, int radius)
    {
        if (centerIndex < 0 || centerIndex >= items.Count || _thumbnailViewportCts is null)
        {
            return;
        }

        QueueThumbnailLoad(items[centerIndex]);
        for (var distance = 1; distance <= radius; distance++)
        {
            var previous = centerIndex - distance;
            if (previous >= 0)
            {
                QueueThumbnailLoad(items[previous]);
            }

            var next = centerIndex + distance;
            if (next < items.Count)
            {
                QueueThumbnailLoad(items[next]);
            }
        }
    }

    private void QueueThumbnailLoad(ThumbnailItem item)
    {
        if (item.Thumbnail is not null || item.HasLoadFailed)
        {
            return;
        }

        if (TryGetCachedThumbnail(item.Path, out var cachedThumbnail))
        {
            item.Thumbnail = cachedThumbnail;
            item.StatusText = string.Empty;
            return;
        }

        if (_thumbnailViewportCts is null)
        {
            return;
        }

        lock (_thumbnailQueueLock)
        {
            if (!_queuedThumbnailPaths.Add(item.Path))
            {
                return;
            }
        }

        item.IsLoading = true;
        item.StatusText = "加载中";
        var generation = _thumbnailGeneration;
        var viewportGeneration = _thumbnailViewportGeneration;
        var token = _thumbnailViewportCts.Token;
        _ = LoadThumbnailQueuedAsync(item, generation, viewportGeneration, token);
    }

    private async Task LoadThumbnailQueuedAsync(
        ThumbnailItem item,
        int generation,
        int viewportGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ShouldContinueThumbnailLoad(item, generation, viewportGeneration, cancellationToken))
            {
                return;
            }

            var thumbnailLoadSemaphore = _thumbnailLoadSemaphore;
            await thumbnailLoadSemaphore.WaitAsync(cancellationToken);
            BitmapSource thumbnail;
            try
            {
                if (!ShouldContinueThumbnailLoad(item, generation, viewportGeneration, cancellationToken))
                {
                    return;
                }

                thumbnail = await Task.Run(() => LoadThumbnail(item.Path, cancellationToken), cancellationToken);
            }
            finally
            {
                thumbnailLoadSemaphore.Release();
            }

            if (cancellationToken.IsCancellationRequested
                || generation != _thumbnailGeneration
                || viewportGeneration != _thumbnailViewportGeneration)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (generation != _thumbnailGeneration
                    || viewportGeneration != _thumbnailViewportGeneration
                    || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                AddThumbnailToCache(item.Path, thumbnail);
                item.Thumbnail = thumbnail;
                item.HasLoadFailed = false;
                item.StatusText = string.Empty;
                item.IsLoading = false;
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested
                && generation == _thumbnailGeneration
                && viewportGeneration == _thumbnailViewportGeneration)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    item.HasLoadFailed = true;
                    item.StatusText = "无法预览";
                    item.IsLoading = false;
                }, DispatcherPriority.Background);
            }
        }
        finally
        {
            if (viewportGeneration == _thumbnailViewportGeneration)
            {
                lock (_thumbnailQueueLock)
                {
                    _queuedThumbnailPaths.Remove(item.Path);
                }
            }

            if (!cancellationToken.IsCancellationRequested
                && generation == _thumbnailGeneration
                && viewportGeneration == _thumbnailViewportGeneration)
            {
                await Dispatcher.InvokeAsync(() => item.IsLoading = false, DispatcherPriority.Background);
            }
        }
    }

    private void PrunePendingThumbnailLoadsOutsideActiveRange()
    {
        List<ThumbnailItem> pendingItems;
        lock (_thumbnailQueueLock)
        {
            pendingItems = _queuedThumbnailPaths
                .Select(path => _thumbnailItemByPath.TryGetValue(path, out var item) ? item : null)
                .Where(item => item is not null && !ShouldPrioritizeThumbnailLoad(item))
                .Cast<ThumbnailItem>()
                .ToList();

            foreach (var item in pendingItems)
            {
                _queuedThumbnailPaths.Remove(item.Path);
            }
        }

        foreach (var item in pendingItems)
        {
            if (item.Thumbnail is null && !item.HasLoadFailed)
            {
                item.IsLoading = false;
                item.StatusText = string.Empty;
            }
        }
    }

    private bool ShouldContinueThumbnailLoad(
        ThumbnailItem item,
        int generation,
        int viewportGeneration,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || generation != _thumbnailGeneration
            || viewportGeneration != _thumbnailViewportGeneration
            || item.Thumbnail is not null
            || item.HasLoadFailed)
        {
            return false;
        }

        return ShouldPrioritizeThumbnailLoad(item);
    }

    private bool ShouldPrioritizeThumbnailLoad(ThumbnailItem item)
    {
        return item.IsSelected || IsThumbnailInActiveRange(item);
    }

    private BitmapSource LoadThumbnail(string path, CancellationToken cancellationToken)
    {
        return _thumbnailImageLoader.Load(
            path,
            ThumbnailDecodeWidth,
            ThumbnailDecodeHeight,
            _viewerSettings.UseThumbnailDiskCache,
            cancellationToken);
    }

    private void UpdateThumbnailSelection()
    {
        if (_selectedThumbnailItem is not null)
        {
            _selectedThumbnailItem.IsSelected = false;
            _selectedThumbnailItem = null;
        }

        if (_catalog.Index < 0 || _catalog.Index >= _thumbnailItems.Count)
        {
            return;
        }

        var item = _thumbnailItems[_catalog.Index];
        item.IsSelected = true;
        _selectedThumbnailItem = item;

        var displayItems = GetDisplayedThumbnailItems();
        var displayIndex = _thumbnailSearchItems is null
            ? _catalog.Index
            : _thumbnailSearchItems.IndexOfSourceIndex(_catalog.Index);
        if (displayIndex < 0)
        {
            return;
        }

        var rowIndex = displayIndex / GetThumbnailColumnCount();
        if (rowIndex >= 0 && rowIndex < _thumbnailRows.Count)
        {
            ThumbnailList.ScrollIntoView(_thumbnailRows[rowIndex]);
        }

        QueueThumbnailLoadsAroundIndex(displayItems, displayIndex, ThumbnailPreloadRadius);
    }

    private ScrollViewer? GetThumbnailScrollViewer()
    {
        if (_thumbnailScrollViewer is not null)
        {
            return _thumbnailScrollViewer;
        }

        _thumbnailScrollViewer = FindVisualChild<ScrollViewer>(ThumbnailList);
        return _thumbnailScrollViewer;
    }

    private bool TryGetCachedThumbnail(string path, out BitmapSource? thumbnail)
    {
        return _thumbnailCache.TryGetValue(path, out thumbnail);
    }

    private void AddThumbnailToCache(string path, BitmapSource thumbnail)
    {
        _thumbnailCache[path] = thumbnail;
        _thumbnailCacheOrder.Enqueue(path);
        TrimThumbnailCache();
    }

    private void TrimThumbnailCache(int targetCount = MaxThumbnailCacheItems)
    {
        var checkedCount = 0;
        var maxCheckedCount = _thumbnailCacheOrder.Count;
        while (_thumbnailCache.Count > Math.Max(0, targetCount)
            && _thumbnailCacheOrder.Count > 0
            && checkedCount < maxCheckedCount)
        {
            checkedCount++;
            var path = _thumbnailCacheOrder.Dequeue();
            if (!_thumbnailCache.ContainsKey(path))
            {
                continue;
            }

            if (!_thumbnailItemByPath.TryGetValue(path, out var item))
            {
                _thumbnailCache.Remove(path);
                continue;
            }

            if (ShouldKeepThumbnailInMemory(item))
            {
                _thumbnailCacheOrder.Enqueue(path);
                continue;
            }

            _thumbnailCache.Remove(path);
            item.Thumbnail = null;
            item.StatusText = string.Empty;
        }
    }

    private bool ShouldKeepThumbnailInMemory(ThumbnailItem item)
    {
        if (item.IsSelected || item.IsLoading)
        {
            return true;
        }

        return IsThumbnailInActiveRange(item);
    }

    private bool IsThumbnailInActiveRange(ThumbnailItem item)
    {
        if (_thumbnailVisibleEndIndex >= _thumbnailVisibleStartIndex
            && item.Index >= _thumbnailVisibleStartIndex
            && item.Index <= _thumbnailVisibleEndIndex)
        {
            return true;
        }

        if (_isThumbnailScrollActive)
        {
            return false;
        }

        return Math.Abs(item.Index - _catalog.Index) <= ThumbnailPreloadRadius;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void UpdateThumbnailSidebarTitle()
    {
        if (_thumbnailSearchItems is not null)
        {
            ThumbnailSidebarTitle.Text = $"搜索结果  {_thumbnailSearchItems.Count:N0}";
            return;
        }

        ThumbnailSidebarTitle.Text = _catalog.Count > 0 ? $"图片预览  {_catalog.Count}" : "图片预览";
    }

    private void ToggleLargeScreen()
    {
        if (_isFullScreen)
        {
            ExitFullScreen();
            return;
        }

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateCommands();
    }

    private void ShowShortcutSettingsDialog()
    {
        var dialog = new ShortcutSettingsWindow(_shortcutSettings, _viewerSettings)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true)
        {
            _shortcutSettings.ReplaceWith(ShortcutSettings.Load());
            ApplyViewerSettings();
        }
    }

    private void ApplyViewerSettings()
    {
        ThemeManager.Apply(_viewerSettings.Theme);
        ApplyRuntimeViewerSettings();
        var wasThumbnailSidebarVisible = _showThumbnailSidebar;
        _showThumbnailSidebar = _viewerSettings.ShowThumbnailSidebar;
        _useDoubleThumbnailColumns = _viewerSettings.UseDoubleThumbnailColumns;
        OnPropertyChanged(nameof(ThumbnailImageHeight));
        OnPropertyChanged(nameof(ThumbnailItemWidth));
        OnPropertyChanged(nameof(ThumbnailItemPadding));
        OnPropertyChanged(nameof(ThumbnailItemMargin));
        OnPropertyChanged(nameof(ThumbnailColumnCount));
        UpdateThumbnailSidebarVisibility();
        if (!_showThumbnailSidebar || !wasThumbnailSidebarVisible || _thumbnailItemsNeedRefresh)
        {
            RefreshThumbnailItems();
        }
        else
        {
            RebuildThumbnailRows();
            UpdateThumbnailSelection();
            QueueVisibleThumbnails();
        }

        UpdateAnimationControls();
        UpdateCommands();
        ScheduleIdleFullResolutionLoad();

        if (_viewerSettings.WatchFolderChanges)
        {
            StartFolderWatcher(_catalog.SourceFolder);
        }
        else
        {
            StopFolderWatcher();
        }

        if (_isFitMode)
        {
            Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Loaded);
        }
    }

    private async Task BeginCropModeAsync(CropShape shape)
    {
        if (_currentDocument is null || BitmapView.Source is null)
        {
            return;
        }

        if (_currentDocument.IsVideo)
        {
            ShowUserError("视频只显示封面预览，不能作为图片裁剪。");
            return;
        }

        if (_currentDocument.IsAnimated)
        {
            PauseAnimation();
        }

        if (!await EnsureFullResolutionDocumentAsync())
        {
            return;
        }

        _cropShape = shape;
        _isCropMode = true;
        _isCropDragging = false;
        ClearCropSelection();
        HideBoundaryToast();
        CropOverlay.Visibility = Visibility.Visible;
        ViewerSurface.Cursor = Cursors.Cross;
        UpdateCommands();
    }

    private void ExitCropMode()
    {
        if (!_isCropMode && !_isCropDragging)
        {
            return;
        }

        if (_isCropDragging)
        {
            _isCropDragging = false;
            ViewerSurface.ReleaseMouseCapture();
        }

        _isCropMode = false;
        ClearCropSelection();
        CropOverlay.Visibility = Visibility.Collapsed;
        ViewerSurface.ClearValue(CursorProperty);
        UpdateCommands();
    }

    private void BeginCropSelection(Point start)
    {
        if (!_isCropMode || BitmapView.Source is null)
        {
            return;
        }

        _cropStartPoint = ClampToViewer(start);
        _cropStartRect = _cropSelectionRect;
        _cropDragMode = GetCropDragMode(_cropStartPoint);
        if (_cropDragMode == CropDragMode.None)
        {
            _cropDragMode = CropDragMode.Create;
            _cropStartRect = Rect.Empty;
            ApplyCropSelection(new Rect(_cropStartPoint, _cropStartPoint));
        }

        _isCropDragging = true;
        UpdateCropCursor(_cropStartPoint);
        ViewerSurface.CaptureMouse();
    }

    private void UpdateCropSelection(Point current)
    {
        if (!_isCropMode)
        {
            return;
        }

        current = ClampToViewer(current);
        var rect = _cropDragMode switch
        {
            CropDragMode.Create => CreateCropSelectionRect(_cropStartPoint, current),
            CropDragMode.Move => MoveCropSelection(current),
            CropDragMode.None => _cropSelectionRect,
            _ => ResizeCropSelection(current),
        };

        ApplyCropSelection(rect);
    }

    private Rect CreateCropSelectionRect(Point start, Point current)
    {
        if (_cropShape != CropShape.Circle)
        {
            return new Rect(start, current);
        }

        var deltaX = current.X - start.X;
        var deltaY = current.Y - start.Y;
        var size = Math.Min(Math.Abs(deltaX), Math.Abs(deltaY));
        var left = deltaX >= 0 ? start.X : start.X - size;
        var top = deltaY >= 0 ? start.Y : start.Y - size;
        return new Rect(left, top, size, size);
    }

    private Rect MoveCropSelection(Point current)
    {
        if (_cropStartRect.IsEmpty)
        {
            return _cropSelectionRect;
        }

        var delta = current - _cropStartPoint;
        var viewportWidth = Math.Max(0, ViewerSurface.ActualWidth);
        var viewportHeight = Math.Max(0, ViewerSurface.ActualHeight);
        var maxLeft = Math.Max(0, viewportWidth - _cropStartRect.Width);
        var maxTop = Math.Max(0, viewportHeight - _cropStartRect.Height);
        var left = Math.Clamp(_cropStartRect.Left + delta.X, 0, maxLeft);
        var top = Math.Clamp(_cropStartRect.Top + delta.Y, 0, maxTop);
        return new Rect(left, top, _cropStartRect.Width, _cropStartRect.Height);
    }

    private Rect ResizeCropSelection(Point current)
    {
        if (_cropStartRect.IsEmpty)
        {
            return CreateCropSelectionRect(_cropStartPoint, current);
        }

        if (_cropShape == CropShape.Circle)
        {
            return ResizeCircleCropSelection(current);
        }

        var left = _cropStartRect.Left;
        var top = _cropStartRect.Top;
        var right = _cropStartRect.Right;
        var bottom = _cropStartRect.Bottom;

        if (HasCropDragMode(_cropDragMode, CropDragMode.Left))
        {
            left = current.X;
        }

        if (HasCropDragMode(_cropDragMode, CropDragMode.Right))
        {
            right = current.X;
        }

        if (HasCropDragMode(_cropDragMode, CropDragMode.Top))
        {
            top = current.Y;
        }

        if (HasCropDragMode(_cropDragMode, CropDragMode.Bottom))
        {
            bottom = current.Y;
        }

        return CreateNormalizedRect(left, top, right, bottom);
    }

    private Rect ResizeCircleCropSelection(Point current)
    {
        var rect = _cropStartRect;

        if (HasAnyCropDragMode(_cropDragMode, CropDragMode.Left | CropDragMode.Right)
            && HasAnyCropDragMode(_cropDragMode, CropDragMode.Top | CropDragMode.Bottom))
        {
            var anchorX = HasCropDragMode(_cropDragMode, CropDragMode.Left) ? rect.Right : rect.Left;
            var anchorY = HasCropDragMode(_cropDragMode, CropDragMode.Top) ? rect.Bottom : rect.Top;
            var size = Math.Min(Math.Abs(current.X - anchorX), Math.Abs(current.Y - anchorY));
            var left = HasCropDragMode(_cropDragMode, CropDragMode.Left) ? anchorX - size : anchorX;
            var top = HasCropDragMode(_cropDragMode, CropDragMode.Top) ? anchorY - size : anchorY;
            return new Rect(left, top, size, size);
        }

        if (HasAnyCropDragMode(_cropDragMode, CropDragMode.Left | CropDragMode.Right))
        {
            var anchorX = HasCropDragMode(_cropDragMode, CropDragMode.Left) ? rect.Right : rect.Left;
            var size = Math.Abs(current.X - anchorX);
            var left = HasCropDragMode(_cropDragMode, CropDragMode.Left) ? anchorX - size : anchorX;
            var top = rect.Top + rect.Height / 2.0 - size / 2.0;
            return new Rect(left, top, size, size);
        }

        if (HasAnyCropDragMode(_cropDragMode, CropDragMode.Top | CropDragMode.Bottom))
        {
            var anchorY = HasCropDragMode(_cropDragMode, CropDragMode.Top) ? rect.Bottom : rect.Top;
            var size = Math.Abs(current.Y - anchorY);
            var left = rect.Left + rect.Width / 2.0 - size / 2.0;
            var top = HasCropDragMode(_cropDragMode, CropDragMode.Top) ? anchorY - size : anchorY;
            return new Rect(left, top, size, size);
        }

        return rect;
    }

    private void ApplyCropSelection(Rect rect)
    {
        _cropSelectionRect = ClampCropRect(rect);
        if (_cropSelectionRect.IsEmpty)
        {
            CropSelection.Visibility = Visibility.Collapsed;
            CropCircleSelection.Visibility = Visibility.Collapsed;
            CropSelection.Width = 0;
            CropSelection.Height = 0;
            CropCircleSelection.Width = 0;
            CropCircleSelection.Height = 0;
            SetCropHandlesVisibility(Visibility.Collapsed);
            return;
        }

        Canvas.SetLeft(CropSelection, _cropSelectionRect.Left);
        Canvas.SetTop(CropSelection, _cropSelectionRect.Top);
        CropSelection.Width = _cropSelectionRect.Width;
        CropSelection.Height = _cropSelectionRect.Height;
        Canvas.SetLeft(CropCircleSelection, _cropSelectionRect.Left);
        Canvas.SetTop(CropCircleSelection, _cropSelectionRect.Top);
        CropCircleSelection.Width = _cropSelectionRect.Width;
        CropCircleSelection.Height = _cropSelectionRect.Height;

        var visibility = _cropSelectionRect.Width >= MinCropSelectionSize && _cropSelectionRect.Height >= MinCropSelectionSize
            ? Visibility.Visible
            : Visibility.Collapsed;
        CropSelection.Visibility = _cropShape == CropShape.Rectangle ? visibility : Visibility.Collapsed;
        CropCircleSelection.Visibility = _cropShape == CropShape.Circle ? visibility : Visibility.Collapsed;
        SetCropHandlesVisibility(visibility);
        if (visibility == Visibility.Visible)
        {
            UpdateCropHandles();
        }
    }

    private void ClearCropSelection()
    {
        _cropDragMode = CropDragMode.None;
        _cropStartRect = Rect.Empty;
        _cropSelectionRect = Rect.Empty;
        CropSelection.Visibility = Visibility.Collapsed;
        CropCircleSelection.Visibility = Visibility.Collapsed;
        CropSelection.Width = 0;
        CropSelection.Height = 0;
        CropCircleSelection.Width = 0;
        CropCircleSelection.Height = 0;
        SetCropHandlesVisibility(Visibility.Collapsed);
    }

    private Rect ClampCropRect(Rect rect)
    {
        if (rect.IsEmpty)
        {
            return Rect.Empty;
        }

        var viewportWidth = Math.Max(0, ViewerSurface.ActualWidth);
        var viewportHeight = Math.Max(0, ViewerSurface.ActualHeight);
        rect = NormalizeCropRect(rect);

        if (_cropShape == CropShape.Circle)
        {
            var size = Math.Min(Math.Min(rect.Width, rect.Height), Math.Min(viewportWidth, viewportHeight));
            if (size <= 0)
            {
                return Rect.Empty;
            }

            var circleLeft = Math.Clamp(rect.Left, 0, Math.Max(0, viewportWidth - size));
            var circleTop = Math.Clamp(rect.Top, 0, Math.Max(0, viewportHeight - size));
            return new Rect(circleLeft, circleTop, size, size);
        }

        var left = Math.Clamp(rect.Left, 0, viewportWidth);
        var top = Math.Clamp(rect.Top, 0, viewportHeight);
        var right = Math.Clamp(rect.Right, 0, viewportWidth);
        var bottom = Math.Clamp(rect.Bottom, 0, viewportHeight);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static Rect NormalizeCropRect(Rect rect)
    {
        return rect.IsEmpty
            ? Rect.Empty
            : CreateNormalizedRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static Rect CreateNormalizedRect(double left, double top, double right, double bottom)
    {
        return new Rect(
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Abs(right - left),
            Math.Abs(bottom - top));
    }

    private CropDragMode GetCropDragMode(Point point)
    {
        if (_cropSelectionRect.IsEmpty
            || _cropSelectionRect.Width < MinCropSelectionSize
            || _cropSelectionRect.Height < MinCropSelectionSize)
        {
            return CropDragMode.None;
        }

        var rect = _cropSelectionRect;
        var withinHorizontalBand = point.Y >= rect.Top - CropHandleHitRadius
            && point.Y <= rect.Bottom + CropHandleHitRadius;
        var withinVerticalBand = point.X >= rect.Left - CropHandleHitRadius
            && point.X <= rect.Right + CropHandleHitRadius;
        var nearLeft = Math.Abs(point.X - rect.Left) <= CropHandleHitRadius && withinHorizontalBand;
        var nearRight = Math.Abs(point.X - rect.Right) <= CropHandleHitRadius && withinHorizontalBand;
        var nearTop = Math.Abs(point.Y - rect.Top) <= CropHandleHitRadius && withinVerticalBand;
        var nearBottom = Math.Abs(point.Y - rect.Bottom) <= CropHandleHitRadius && withinVerticalBand;

        if (nearLeft && nearTop)
        {
            return CropDragMode.Left | CropDragMode.Top;
        }

        if (nearRight && nearTop)
        {
            return CropDragMode.Right | CropDragMode.Top;
        }

        if (nearRight && nearBottom)
        {
            return CropDragMode.Right | CropDragMode.Bottom;
        }

        if (nearLeft && nearBottom)
        {
            return CropDragMode.Left | CropDragMode.Bottom;
        }

        if (nearLeft)
        {
            return CropDragMode.Left;
        }

        if (nearRight)
        {
            return CropDragMode.Right;
        }

        if (nearTop)
        {
            return CropDragMode.Top;
        }

        if (nearBottom)
        {
            return CropDragMode.Bottom;
        }

        return rect.Contains(point) ? CropDragMode.Move : CropDragMode.None;
    }

    private void UpdateCropCursor(Point point)
    {
        if (!_isCropMode)
        {
            return;
        }

        var mode = _isCropDragging ? _cropDragMode : GetCropDragMode(point);
        ViewerSurface.Cursor = GetCropCursor(mode);
    }

    private static Cursor GetCropCursor(CropDragMode mode)
    {
        if (mode == CropDragMode.Move)
        {
            return Cursors.SizeAll;
        }

        var horizontal = HasAnyCropDragMode(mode, CropDragMode.Left | CropDragMode.Right);
        var vertical = HasAnyCropDragMode(mode, CropDragMode.Top | CropDragMode.Bottom);
        if (horizontal && vertical)
        {
            var northwestSoutheast = HasCropDragMode(mode, CropDragMode.Left | CropDragMode.Top)
                || HasCropDragMode(mode, CropDragMode.Right | CropDragMode.Bottom);
            return northwestSoutheast ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }

        if (horizontal)
        {
            return Cursors.SizeWE;
        }

        if (vertical)
        {
            return Cursors.SizeNS;
        }

        return Cursors.Cross;
    }

    private void UpdateCropHandles()
    {
        if (_cropSelectionRect.IsEmpty)
        {
            return;
        }

        var left = _cropSelectionRect.Left;
        var centerX = _cropSelectionRect.Left + _cropSelectionRect.Width / 2.0;
        var right = _cropSelectionRect.Right;
        var top = _cropSelectionRect.Top;
        var centerY = _cropSelectionRect.Top + _cropSelectionRect.Height / 2.0;
        var bottom = _cropSelectionRect.Bottom;

        PositionCropHandle(CropHandleTopLeft, left, top);
        PositionCropHandle(CropHandleTop, centerX, top);
        PositionCropHandle(CropHandleTopRight, right, top);
        PositionCropHandle(CropHandleRight, right, centerY);
        PositionCropHandle(CropHandleBottomRight, right, bottom);
        PositionCropHandle(CropHandleBottom, centerX, bottom);
        PositionCropHandle(CropHandleBottomLeft, left, bottom);
        PositionCropHandle(CropHandleLeft, left, centerY);
    }

    private static void PositionCropHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - CropHandleSize / 2.0);
        Canvas.SetTop(handle, y - CropHandleSize / 2.0);
    }

    private void SetCropHandlesVisibility(Visibility visibility)
    {
        CropHandleTopLeft.Visibility = visibility;
        CropHandleTop.Visibility = visibility;
        CropHandleTopRight.Visibility = visibility;
        CropHandleRight.Visibility = visibility;
        CropHandleBottomRight.Visibility = visibility;
        CropHandleBottom.Visibility = visibility;
        CropHandleBottomLeft.Visibility = visibility;
        CropHandleLeft.Visibility = visibility;
    }

    private static bool HasCropDragMode(CropDragMode mode, CropDragMode flag)
    {
        return (mode & flag) == flag;
    }

    private static bool HasAnyCropDragMode(CropDragMode mode, CropDragMode flags)
    {
        return (mode & flags) != 0;
    }

    private async Task SetCurrentAsWallpaperAsync()
    {
        if (_currentDocument is null || BitmapView.Source is null)
        {
            return;
        }

        if (_currentDocument.IsVideo)
        {
            ShowUserError("视频只显示封面预览，不能设置为桌面壁纸。");
            return;
        }

        if (_currentDocument.IsAnimated)
        {
            ShowUserError("动图不能设置为桌面壁纸。请先导出静态图片后再设置。");
            return;
        }

        ExitCropMode();
        if (!await EnsureFullResolutionDocumentAsync())
        {
            return;
        }

        var document = _currentDocument;
        if (document is null || document.IsVideo || document.IsAnimated)
        {
            return;
        }

        try
        {
            LoadingText.Text = "正在设置壁纸...";
            LoadingPanel.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;

            var rotation = _rotationDegrees;
            await Task.Run(() =>
            {
                var wallpaperPath = CreateWallpaperPath();
                Directory.CreateDirectory(Path.GetDirectoryName(wallpaperPath)!);
                var wallpaperBitmap = CreateWallpaperBitmap(document.Bitmap, rotation);
                SaveBitmap(wallpaperBitmap, wallpaperPath);
                SetDesktopWallpaper(wallpaperPath);
                PruneOldWallpaperFiles(wallpaperPath);
            });

            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
            ShowBoundaryToast("已设为桌面壁纸");
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
            ShowUserError($"设置壁纸失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private static BitmapSource CreateWallpaperBitmap(BitmapSource source, int rotationDegrees)
    {
        var rotation = NormalizeRotation(rotationDegrees);
        if (rotation == 0)
        {
            return source;
        }

        return CreateBitmapSnapshot(new TransformedBitmap(source, new RotateTransform(rotation)));
    }

    private static string CreateWallpaperPath()
    {
        return Path.Combine(
            AppInfo.LocalDataFolder,
            "wallpaper",
            $"{AppInfo.WallpaperFilePrefix}{DateTime.Now:yyyyMMddHHmmssfff}.jpg");
    }

    private static void SetDesktopWallpaper(string path)
    {
        if (!SystemParametersInfo(
                SystemParametersInfoSetDesktopWallpaper,
                0,
                path,
                SystemParametersInfoWallpaperFlags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void PruneOldWallpaperFiles(string currentPath)
    {
        var folder = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var oldFiles = Directory
                .EnumerateFiles(folder, $"{AppInfo.WallpaperFilePrefix}*.jpg")
                .Where(path => !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(5);

            foreach (var oldFile in oldFiles)
            {
                try
                {
                    File.Delete(oldFile);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private async Task SaveVideoCoverAsync()
    {
        var document = _currentDocument;
        if (document is null || !document.IsVideo)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存视频封面",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp|TIFF 图片|*.tif;*.tiff",
            FileName = GetDefaultVideoCoverFileName(document.Path),
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
        };

        var currentFolder = Path.GetDirectoryName(document.Path);
        if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
        {
            dialog.InitialDirectory = currentFolder;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var targetPath = EnsureCropFileExtension(dialog.FileName, dialog.FilterIndex);
        if (!string.Equals(targetPath, dialog.FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
        {
            var confirm = MessageBox.Show(
                this,
                $"文件已存在，是否覆盖？\n\n{targetPath}",
                "保存视频封面",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            LoadingText.Text = "正在保存...";
            LoadingPanel.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;

            var bitmap = document.Bitmap;
            await Task.Run(() => SaveBitmap(bitmap, targetPath));

            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
            RemoveCachedMedia(targetPath);
            await ApplySavedFileOpenBehaviorAsync(targetPath);
            ShowBoundaryToast("视频封面已保存");
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
            ShowUserError($"保存视频封面失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private static string GetDefaultVideoCoverFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? "视频封面.png" : $"{name}_封面.png";
    }

    private Point ClampToViewer(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, Math.Max(0, ViewerSurface.ActualWidth)),
            Math.Clamp(point.Y, 0, Math.Max(0, ViewerSurface.ActualHeight)));
    }

    private bool TryGetCropPixelRect(out Int32Rect pixelRect)
    {
        pixelRect = default;
        var source = BitmapView.Source;
        if (source is null || _cropSelectionRect.IsEmpty)
        {
            return false;
        }

        var transform = new ImageTransform(_scale, BitmapView.OffsetX, BitmapView.OffsetY);
        var rect = ImageViewportMath.CalculateCropPixelRect(
            _cropSelectionRect,
            source.PixelWidth,
            source.PixelHeight,
            transform);

        if (rect is null)
        {
            return false;
        }

        pixelRect = rect.Value;
        return pixelRect.Width > 0 && pixelRect.Height > 0;
    }

    private async Task SaveCropAsync()
    {
        if (_isSavingCrop)
        {
            return;
        }

        _isSavingCrop = true;
        try
        {
            await SaveCropCoreAsync();
        }
        finally
        {
            _isSavingCrop = false;
        }
    }

    private async Task SaveCropCoreAsync()
    {
        if (!_isCropMode || _currentDocument is null || BitmapView.Source is null)
        {
            return;
        }

        if (!TryGetCropPixelRect(out var cropRect))
        {
            ShowUserError("请先拖出有效的裁剪区域。");
            return;
        }

        var cropPixelCount = (long)cropRect.Width * cropRect.Height;
        if (cropPixelCount > MaxCropPixelCount)
        {
            ShowUserError($"裁剪区域过大，已超过保护阈值 {MaxCropPixelCount:N0} 像素。请缩小裁剪区域后再保存。");
            return;
        }

        var currentPath = _catalog.CurrentPath ?? _currentDocument.Path;
        var currentFolder = Path.GetDirectoryName(currentPath);
        var defaultCropFileName = GetDefaultCropFileName(currentPath, _cropShape);
        if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
        {
            defaultCropFileName = GetAvailableFileName(currentFolder, defaultCropFileName);
        }

        var dialog = new SaveFileDialog
        {
            Title = _cropShape == CropShape.Circle ? "保存圆形裁剪图片" : "保存裁剪图片",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp|TIFF 图片|*.tif;*.tiff",
            FileName = defaultCropFileName,
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
        {
            dialog.InitialDirectory = currentFolder;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var targetPath = EnsureCropFileExtension(dialog.FileName, dialog.FilterIndex);
        if (!string.Equals(targetPath, dialog.FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
        {
            var confirm = MessageBox.Show(
                this,
                $"文件已存在，是否覆盖？\n\n{targetPath}",
                "保存裁剪图片",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            LoadingText.Text = "正在保存...";
            LoadingPanel.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;

            var cropShape = _cropShape;
            var source = BitmapView.Source;
            if (source is null)
            {
                return;
            }

            var croppedSnapshot = CreateCropBitmapSnapshot(source, cropRect);
            await Task.Run(() => SaveCropBitmap(croppedSnapshot, targetPath, cropShape));

            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
            RemoveCachedMedia(targetPath);
            ExitCropMode();
            await ApplySavedFileOpenBehaviorAsync(targetPath);
            ShowBoundaryToast(cropShape == CropShape.Circle ? "圆形裁剪已保存" : "裁剪图片已保存");
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
            ShowUserError($"保存裁剪图片失败：\n{FriendlyException(ex)}", ex);
        }
    }

    private static void SaveCropBitmap(BitmapSource cropped, string targetPath, CropShape cropShape)
    {
        var outputBitmap = cropShape == CropShape.Circle
            ? CreateCircularCropBitmap(cropped)
            : cropped;
        SaveBitmap(outputBitmap, targetPath);
    }

    private static BitmapSource CreateCropBitmapSnapshot(BitmapSource source, Int32Rect cropRect)
    {
        var safeX = Math.Clamp(cropRect.X, 0, Math.Max(0, source.PixelWidth - 1));
        var safeY = Math.Clamp(cropRect.Y, 0, Math.Max(0, source.PixelHeight - 1));
        var safeRect = new Int32Rect(
            safeX,
            safeY,
            Math.Min(cropRect.Width, Math.Max(0, source.PixelWidth - safeX)),
            Math.Min(cropRect.Height, Math.Max(0, source.PixelHeight - safeY)));

        if (safeRect.Width <= 0 || safeRect.Height <= 0)
        {
            throw new InvalidDataException("裁剪区域尺寸无效。");
        }

        BitmapSource pixelSource = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = checked(safeRect.Width * 4);
        var pixels = new byte[checked(stride * safeRect.Height)];
        pixelSource.CopyPixels(safeRect, pixels, stride, 0);

        var bitmap = BitmapSource.Create(
            safeRect.Width,
            safeRect.Height,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static string GetDefaultCropFileName(string path, CropShape shape)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var suffix = shape == CropShape.Circle ? "圆形裁剪" : "裁剪";
        return string.IsNullOrWhiteSpace(name) ? $"{suffix}.png" : $"{name}_{suffix}.png";
    }

    private static string GetAvailableFileName(string folder, string fileName)
    {
        if (!File.Exists(Path.Combine(folder, fileName)))
        {
            return fileName;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = $"{name} ({index}){extension}";
            if (!File.Exists(Path.Combine(folder, candidate)))
            {
                return candidate;
            }
        }

        return $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
    }

    private static string EnsureCropFileExtension(string fileName, int filterIndex)
    {
        var extension = Path.GetExtension(fileName);
        if (IsWritableCropExtension(extension))
        {
            return fileName;
        }

        var preferredExtension = filterIndex switch
        {
            2 => ".jpg",
            3 => ".bmp",
            4 => ".tif",
            _ => ".png",
        };

        return Path.ChangeExtension(fileName, preferredExtension);
    }

    private static bool IsWritableCropExtension(string extension)
    {
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapSource CreateCircularCropBitmap(BitmapSource source)
    {
        var size = Math.Min(source.PixelWidth, source.PixelHeight);
        if (size <= 0)
        {
            throw new InvalidDataException("圆形裁剪区域尺寸无效。");
        }

        BitmapSource pixelSource = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = checked(size * 4);
        var pixels = new byte[checked(stride * size)];
        var sourceRect = new Int32Rect(
            Math.Max(0, (pixelSource.PixelWidth - size) / 2),
            Math.Max(0, (pixelSource.PixelHeight - size) / 2),
            size,
            size);
        pixelSource.CopyPixels(sourceRect, pixels, stride, 0);

        var center = (size - 1) / 2.0;
        var radius = size / 2.0;
        var radiusSquared = radius * radius;
        var featherStart = Math.Max(0, radius - 1.0);
        var featherStartSquared = featherStart * featherStart;

        for (var y = 0; y < size; y++)
        {
            var dy = y - center;
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var distanceSquared = dx * dx + dy * dy;
                if (distanceSquared <= featherStartSquared)
                {
                    continue;
                }

                var offset = y * stride + x * 4 + 3;
                if (distanceSquared >= radiusSquared)
                {
                    pixels[offset] = 0;
                    continue;
                }

                var distance = Math.Sqrt(distanceSquared);
                var alphaScale = Math.Clamp(radius - distance, 0, 1);
                pixels[offset] = (byte)Math.Round(pixels[offset] * alphaScale);
            }
        }

        var bitmap = BitmapSource.Create(
            size,
            size,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var extension = Path.GetExtension(path);
        var encoder = CreateBitmapEncoder(extension);
        var frameSource = PrepareBitmapForEncoder(bitmap, extension);
        encoder.Frames.Add(BitmapFrame.Create(frameSource));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
    }

    private static BitmapEncoder CreateBitmapEncoder(string extension)
    {
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return new JpegBitmapEncoder { QualityLevel = 95 };
        }

        if (extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            return new BmpBitmapEncoder();
        }

        if (extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            return new TiffBitmapEncoder();
        }

        return new PngBitmapEncoder();
    }

    private static BitmapSource PrepareBitmapForEncoder(BitmapSource bitmap, string extension)
    {
        var requiresOpaqueBackground = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
        if (!requiresOpaqueBackground)
        {
            return bitmap;
        }

        if (bitmap.Format == PixelFormats.Bgr24 || bitmap.Format == PixelFormats.Bgr32)
        {
            return bitmap;
        }

        BitmapSource pixelSource = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        var sourceStride = checked(pixelSource.PixelWidth * 4);
        var sourcePixels = new byte[checked(sourceStride * pixelSource.PixelHeight)];
        pixelSource.CopyPixels(sourcePixels, sourceStride, 0);

        var outputStride = checked(pixelSource.PixelWidth * 3);
        var outputPixels = new byte[checked(outputStride * pixelSource.PixelHeight)];
        for (var y = 0; y < pixelSource.PixelHeight; y++)
        {
            var sourceRow = y * sourceStride;
            var outputRow = y * outputStride;
            for (var x = 0; x < pixelSource.PixelWidth; x++)
            {
                var sourceOffset = sourceRow + x * 4;
                var outputOffset = outputRow + x * 3;
                var alpha = sourcePixels[sourceOffset + 3] / 255.0;
                outputPixels[outputOffset + 0] = CompositeOverWhite(sourcePixels[sourceOffset + 0], alpha);
                outputPixels[outputOffset + 1] = CompositeOverWhite(sourcePixels[sourceOffset + 1], alpha);
                outputPixels[outputOffset + 2] = CompositeOverWhite(sourcePixels[sourceOffset + 2], alpha);
            }
        }

        var flattened = BitmapSource.Create(
            pixelSource.PixelWidth,
            pixelSource.PixelHeight,
            bitmap.DpiX,
            bitmap.DpiY,
            PixelFormats.Bgr24,
            null,
            outputPixels,
            outputStride);
        if (flattened.CanFreeze)
        {
            flattened.Freeze();
        }

        return flattened;
    }

    private static byte CompositeOverWhite(byte value, double alpha)
    {
        return (byte)Math.Round(value * alpha + 255 * (1 - alpha));
    }

    private async Task ToggleFullScreenAsync()
    {
        if (_isFullScreenTransitioning)
        {
            return;
        }

        if (_isFullScreen)
        {
            await ExitFullScreenAsync();
            return;
        }

        await RunFullScreenTransitionAsync(EnterFullScreenCore);
    }

    private async Task ExitFullScreenAsync()
    {
        if (!_isFullScreen || _isFullScreenTransitioning)
        {
            return;
        }

        await RunFullScreenTransitionAsync(ExitFullScreenCore);
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            ExitFullScreen();
            return;
        }

        EnterFullScreenCore();
    }

    private void ExitFullScreen()
    {
        if (!_isFullScreen)
        {
            return;
        }

        ExitFullScreenCore();
    }

    private async Task RunFullScreenTransitionAsync(Action switchAction)
    {
        _isFullScreenTransitioning = true;
        var shield = CreateFullScreenTransitionShield();

        try
        {
            shield.Show();
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            await AnimateWindowOpacityAsync(shield, 1.0, FullScreenTransitionFadeInDuration);

            switchAction();
            shield.Topmost = false;
            shield.Topmost = true;

            await Dispatcher.InvokeAsync(() =>
            {
                if (_isFitMode)
                {
                    FitToWindow();
                }
            }, DispatcherPriority.Loaded);

            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            await Task.Delay(FullScreenTransitionHoldDuration);
            await AnimateWindowOpacityAsync(shield, 0.0, FullScreenTransitionFadeOutDuration);
        }
        finally
        {
            shield.BeginAnimation(OpacityProperty, null);
            shield.Close();
            _isFullScreenTransitioning = false;
        }
    }

    private Window CreateFullScreenTransitionShield()
    {
        var bounds = GetCurrentMonitorBounds();
        var tintColor = GetTransitionTintColor();
        var content = new Grid
        {
            Background = new SolidColorBrush(DarkenTransitionColor(tintColor, 0.62)),
            ClipToBounds = true,
        };

        if (BitmapView.Source is not null)
        {
            var preview = new Image
            {
                Source = BitmapView.Source,
                Stretch = Stretch.UniformToFill,
                Opacity = 0.86,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.08, 1.08),
                Effect = new BlurEffect
                {
                    Radius = 26,
                    RenderingBias = RenderingBias.Performance,
                },
            };

            RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.LowQuality);
            content.Children.Add(preview);
        }

        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x72, 0x10, 0x12, 0x16)),
        });

        return new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = Brushes.Transparent,
            Content = content,
            Left = bounds.Left - 3,
            Top = bounds.Top - 3,
            Width = bounds.Width + 6,
            Height = bounds.Height + 6,
            Topmost = true,
            Opacity = 0,
            Focusable = false,
            IsHitTestVisible = false,
        };
    }

    private Color GetTransitionTintColor()
    {
        var source = BitmapView.Source;
        if (source is null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return Color.FromRgb(0x10, 0x12, 0x16);
        }

        try
        {
            BitmapSource pixelSource = source;
            if (source.Format != PixelFormats.Bgra32)
            {
                pixelSource = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            }

            var pixels = new byte[4];
            var stride = 4;
            var rect = new Int32Rect(source.PixelWidth / 2, source.PixelHeight / 2, 1, 1);
            pixelSource.CopyPixels(rect, pixels, stride, 0);

            return Color.FromRgb(pixels[2], pixels[1], pixels[0]);
        }
        catch
        {
            return Color.FromRgb(0x10, 0x12, 0x16);
        }
    }

    private static Color DarkenTransitionColor(Color color, double amount)
    {
        var clamped = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(color.R * (1 - clamped) + 0x10 * clamped),
            (byte)(color.G * (1 - clamped) + 0x12 * clamped),
            (byte)(color.B * (1 - clamped) + 0x16 * clamped));
    }

    private Task AnimateWindowOpacityAsync(Window window, double to, TimeSpan duration)
    {
        var completion = new TaskCompletionSource();

        window.BeginAnimation(OpacityProperty, null);

        var animation = new DoubleAnimation
        {
            From = window.Opacity,
            To = to,
            Duration = duration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };

        animation.Completed += (_, _) =>
        {
            window.Opacity = to;
            completion.TrySetResult();
        };

        window.BeginAnimation(OpacityProperty, animation);
        return completion.Task;
    }

    private void EnterFullScreenCore()
    {
        _restoreWindowState = WindowState;
        _restoreWindowStyle = WindowStyle;
        _restoreResizeMode = ResizeMode;
        _restoreTopmost = Topmost;
        _restoreLeft = Left;
        _restoreTop = Top;
        _restoreWidth = Width;
        _restoreHeight = Height;

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ApplyFullScreenBounds();
        _isFullScreen = true;
        UpdateCommands();
    }

    private void ExitFullScreenCore()
    {
        _isFullScreen = false;
        WindowState = WindowState.Normal;
        WindowStyle = _restoreWindowStyle;
        ResizeMode = _restoreResizeMode;
        Topmost = _restoreTopmost;
        Left = _restoreLeft;
        Top = _restoreTop;
        Width = _restoreWidth;
        Height = _restoreHeight;
        WindowState = _restoreWindowState;
        UpdateCommands();
    }

    private void ApplyFullScreenBounds()
    {
        var bounds = GetCurrentMonitorBounds();
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private Rect GetCurrentMonitorBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = handle == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MonitorInfo
            {
                Size = Marshal.SizeOf<MonitorInfo>(),
            };

            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                return DeviceRectToWpfRect(monitorInfo.Monitor);
            }
        }

        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    private Rect DeviceRectToWpfRect(NativeRect rect)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(rect.Left, rect.Top));
        var bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void UpdateInfoPanel()
    {
        if (!_showInfo)
        {
            InfoPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var path = _catalog.CurrentPath;
        if (_currentDocument is not null)
        {
            var favoriteInfo = IsCurrentFavorite() ? "  ★ 收藏" : string.Empty;
            var rotationInfo = _rotationDegrees == 0 ? string.Empty : $"  旋转 {_rotationDegrees}°";
            if (_currentDocument.IsVideo)
            {
                InfoText.Text =
                    $"{_catalog.Index + 1}/{_catalog.Count}  {_currentDocument.FileName}{favoriteInfo}\n" +
                    $"视频  封面预览 {_currentDocument.PixelWidth} x {_currentDocument.PixelHeight}  " +
                    $"{FormatFileSize(_currentDocument.FileSize)}{rotationInfo}  " +
                    $"修改：{_currentDocument.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n" +
                    _currentDocument.Path;
                InfoPanel.Visibility = Visibility.Visible;
                return;
            }

            var animationInfo = _currentDocument.IsAnimated ? $"  {_currentDocument.AnimationFrames.Count} 帧" : string.Empty;
            var largeImagePreviewInfo = _currentDocument.IsLargeImagePreview
                ? $"  超大图安全预览 {_currentDocument.Bitmap.PixelWidth:N0} x {_currentDocument.Bitmap.PixelHeight:N0}"
                : string.Empty;
            var loadDurationInfo = _lastImageLoadDuration > TimeSpan.Zero
                ? $"  打开用时 {FormatElapsedDuration(_lastImageLoadDuration)}"
                : string.Empty;
            InfoText.Text =
                $"{_catalog.Index + 1}/{_catalog.Count}  {_currentDocument.FileName}{favoriteInfo}\n" +
                $"{_currentDocument.PixelWidth} x {_currentDocument.PixelHeight}  " +
                $"{_currentDocument.FormatName}{animationInfo}{largeImagePreviewInfo}  {FormatFileSize(_currentDocument.FileSize)}{rotationInfo}{loadDurationInfo}  " +
                $"修改：{_currentDocument.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n" +
                _currentDocument.Path;
            InfoPanel.Visibility = Visibility.Visible;
            return;
        }

        if (path is not null)
        {
            InfoText.Text = $"{_catalog.Index + 1}/{_catalog.Count}  {Path.GetFileName(path)}\n无法打开\n{path}";
            InfoPanel.Visibility = Visibility.Visible;
            return;
        }

        InfoText.Text = string.Empty;
        InfoPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateVideoBadge()
    {
        VideoBadge.Visibility = _currentDocument?.IsVideo == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateAnimationControls()
    {
        var hasAnimation = _currentDocument?.IsAnimated == true && _viewerSettings.ShowAnimationControls;
        AnimationControlPanel.Visibility = hasAnimation ? Visibility.Visible : Visibility.Collapsed;
        ToggleAnimationButton.IsEnabled = hasAnimation;
        RestartAnimationButton.IsEnabled = hasAnimation;
        ToggleAnimationButton.Content = _isAnimationPaused ? "继续" : "暂停";
    }

    private void UpdateCommands()
    {
        var hasPath = _catalog.CurrentPath is not null;
        var hasImage = _currentDocument is not null && !_currentDocument.IsVideo;
        var hasStaticImage = hasImage && _currentDocument?.IsAnimated != true;
        var hasVideo = _currentDocument?.IsVideo == true;
        var isFavorite = hasPath && _catalog.CurrentPath is { } currentPath && _favorites.IsFavorite(currentPath);

        FavoriteMenuItem.Header = isFavorite ? "取消收藏" : "加入收藏";
        FavoriteMenuItem.IsEnabled = hasPath;
        FavoritesViewMenuItem.Header = _isFavoritesView ? "退出收藏视图" : "只看收藏";
        FavoritesViewMenuItem.IsEnabled = _isFavoritesView || _favorites.Count > 0;
        UpdateSortMenuHeaders();
        SortByNameMenuItem.IsEnabled = _catalog.Count > 0;
        SortByNameDescendingMenuItem.IsEnabled = _catalog.Count > 0;
        SortByNewestMenuItem.IsEnabled = _catalog.Count > 0;
        SortByOldestMenuItem.IsEnabled = _catalog.Count > 0;
        SortByLargestMenuItem.IsEnabled = _catalog.Count > 0;
        SortBySmallestMenuItem.IsEnabled = _catalog.Count > 0;
        CropMenuItem.Header = _isCropMode && _cropShape == CropShape.Rectangle ? "取消裁剪" : "裁剪图片";
        CropMenuItem.IsEnabled = hasImage;
        CircleCropMenuItem.Header = _isCropMode && _cropShape == CropShape.Circle ? "取消圆形裁剪" : "圆形裁剪";
        CircleCropMenuItem.IsEnabled = hasImage;
        CompressMenuItem.IsEnabled = hasImage;
        SetAsWallpaperMenuItem.Visibility = hasStaticImage ? Visibility.Visible : Visibility.Collapsed;
        SetAsWallpaperMenuItem.IsEnabled = hasStaticImage;
        OpenBatchCompressToolsMenuItem.IsEnabled = true;
        SaveVideoCoverMenuItem.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
        SaveVideoCoverMenuItem.IsEnabled = hasVideo;
        BatchDeleteMenuItem.Header = _isFavoritesView ? "批量删除当前目录媒体（收藏视图不可用）" : "批量删除当前目录媒体";
        BatchDeleteMenuItem.IsEnabled = !_isFavoritesView && _catalog.SourceFolder is not null && _catalog.Count > 0;
        CopyFileMenuItem.Header = hasVideo ? "复制视频文件" : "复制图片文件";
        CopyFileMenuItem.IsEnabled = hasPath;
        CopyNameMenuItem.IsEnabled = hasPath;
        CopyPathMenuItem.IsEnabled = hasPath;
        var showDiagnostics = ShouldShowDiagnosticsMenuItem();
        CopyDiagnosticsMenuItem.Visibility = showDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        CopyDiagnosticsMenuItem.IsEnabled = showDiagnostics;
        OpenContainingFolderMenuItem.IsEnabled = hasPath;
        ToggleInfoMenuItem.IsEnabled = hasPath || hasImage;
        ToggleThumbnailSidebarMenuItem.Header = _showThumbnailSidebar ? "隐藏缩略图栏" : "显示缩略图栏";
        ToggleThumbnailSidebarMenuItem.IsEnabled = true;
        ShortcutSettingsMenuItem.IsEnabled = true;
        UpdateAnimationControls();
        ThumbnailColumnToggleButton.Content = _useDoubleThumbnailColumns ? "双列" : "单列";
        ThumbnailColumnToggleButton.ToolTip = _useDoubleThumbnailColumns ? "点击切换为单列" : "点击切换为双列";
        ThumbnailColumnToggleButton.IsEnabled = _showThumbnailSidebar;
    }

    private bool ShouldShowDiagnosticsMenuItem()
    {
        return ErrorPanel.Visibility == Visibility.Visible
            && _currentDocument is null
            && !string.IsNullOrWhiteSpace(_lastInlineErrorMessage);
    }

    private void UpdateSortMenuHeaders()
    {
        SortByNameMenuItem.Header = GetSortMenuHeader(ImageSortMode.NameNatural);
        SortByNameDescendingMenuItem.Header = GetSortMenuHeader(ImageSortMode.NameNaturalDescending);
        SortByNewestMenuItem.Header = GetSortMenuHeader(ImageSortMode.LastWriteTimeNewest);
        SortByOldestMenuItem.Header = GetSortMenuHeader(ImageSortMode.LastWriteTimeOldest);
        SortByLargestMenuItem.Header = GetSortMenuHeader(ImageSortMode.FileSizeLargest);
        SortBySmallestMenuItem.Header = GetSortMenuHeader(ImageSortMode.FileSizeSmallest);
    }

    private string GetSortMenuHeader(ImageSortMode sortMode)
    {
        var prefix = _catalog.SortMode == sortMode ? "✓ " : string.Empty;
        return $"{prefix}排序：{GetSortModeDisplayName(sortMode)}";
    }

    private void UpdateWindowTitle(string? path = null)
    {
        if (path is null)
        {
            Title = AppInfo.Name;
            return;
        }

        var index = _catalog.Count > 0 ? $" ({_catalog.Index + 1}/{_catalog.Count})" : string.Empty;
        Title = AppInfo.FormatTitle($"{Path.GetFileName(path)}{index}");
    }

    private void ShowInlineError(string message, Exception? exception = null)
    {
        _lastInlineErrorMessage = message;

        if (exception is null)
        {
            ErrorLog.WriteMessage("InlineError", message);
        }
        else
        {
            ErrorLog.WriteException("InlineError", message, exception);
        }

        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        LoadingPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        UpdateInfoPanel();
        UpdateWindowTitle(_catalog.CurrentPath);
        UpdateCommands();
    }

    private void ShowUserError(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            ErrorLog.WriteMessage("UserError", message);
        }
        else
        {
            ErrorLog.WriteException("UserError", message, exception);
        }

        MessageBox.Show(this, message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ClearImage()
    {
        CancelIdleFullResolutionLoad();
        CancelPreload();
        ExitCropMode();
        _currentDocument = null;
        _rotationDegrees = 0;
        StopAnimation();
        ClearDisplayPreviewState();
        _lastInlineErrorMessage = string.Empty;
        HideBoundaryToast();
        UpdateVideoBadge();
        BitmapView.BeginAnimation(OpacityProperty, null);
        BitmapView.Opacity = 1;
        BitmapView.Source = null;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        UpdateInfoPanel();
        UpdateCommands();
        UpdateWindowTitle();
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private static string FriendlyException(Exception ex)
    {
        return ex switch
        {
            FileNotFoundException => "文件不存在，可能已被删除或移动。",
            DirectoryNotFoundException => "文件夹不存在，可能已被删除或移动。",
            UnauthorizedAccessException => "没有权限访问该文件或文件夹。",
            NotSupportedException => "当前系统缺少该格式的解码器，或文件内容不是受支持的图片。",
            InvalidDataException => string.IsNullOrWhiteSpace(ex.Message) ? "图片数据无效或已损坏。" : ex.Message,
            IOException => string.IsNullOrWhiteSpace(ex.Message) ? "文件正在被占用，或所在设备暂时不可用。" : ex.Message,
            OutOfMemoryException => "图片过大或内存不足，已停止加载以避免程序卡死。",
            _ => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message,
        };
    }

    private sealed class ThumbnailItem : INotifyPropertyChanged
    {
        private BitmapSource? _thumbnail;
        private string _statusText = string.Empty;
        private bool _isLoading;
        private bool _isSelected;
        private bool _isFavorite;

        public ThumbnailItem(string path, int index, bool isFavorite)
        {
            Index = index;
            Path = path;
            FileName = System.IO.Path.GetFileName(path);
            IsVideo = ImageCatalog.IsSupportedVideoPath(path);
            _isFavorite = isFavorite;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path { get; }

        public int Index { get; }

        public string FileName { get; }

        public bool IsVideo { get; }

        public bool HasLoadFailed { get; set; }

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite == value)
                {
                    return;
                }

                _isFavorite = value;
                OnPropertyChanged(nameof(IsFavorite));
            }
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value))
                {
                    return;
                }

                _thumbnail = value;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText == value)
                {
                    return;
                }

                _statusText = value;
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading == value)
                {
                    return;
                }

                _isLoading = value;
                OnPropertyChanged(nameof(IsLoading));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

}
