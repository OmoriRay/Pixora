using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pixora.Models;
using Pixora.Services;

namespace Pixora.SmokeTests;

internal static class OpenSessionRegressionTests
{
    public static void Run(string root)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var folder = Path.Combine(Path.GetTempPath(), "Pixora-open-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "1.png");
        var second = Path.Combine(folder, "2.png");
        File.Copy(Path.Combine(root, "test-images", "1.png"), first);
        File.Copy(first, second);
        var window = new MainWindow();
        var settings = Field<ViewerSettings>(window, "_viewerSettings");
        settings.RememberMainWindowPlacement = false;
        settings.LastOpenedFolder = folder; // 测试不写入用户的上次目录。
        settings.WatchFolderChanges = true;
        settings.SortMode = ImageSortMode.NameNatural;
        var catalog = Field<ImageCatalog>(window, "_catalog");
        catalog.SortMode = ImageSortMode.NameNatural;
        var missing = Path.Combine(folder, "missing.jpg");
        var shellTransferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.InternetCache),
            "Pixora-open-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pendingCatalog = new ImageCatalog();
            pendingCatalog.LoadFromPaths([first, missing], missing, requireExistingFiles: false);
            Require(pendingCatalog.Count == 2 && pendingCatalog.CurrentPath == missing
                && pendingCatalog.SourceFolder is null,
                "手机目录必须保留尚未传输的媒体路径及初始选择。");
            pendingCatalog.LoadFromPaths([first, missing]);
            Require(pendingCatalog.Count == 1, "普通收藏路径仍必须过滤不存在的文件。");

            Pump(InvokeAsync(window, "OpenFolderAsync", folder, null));
            Require(catalog.Count == 2, "普通本地目录仍应加载全部媒体。");
            Pump(InvokeAsync(window, "NavigateNextAsync"));
            Require(catalog.CurrentPath == second, "普通本地下一张应继续可用。");

            // 模拟已发给 UI 队列、尚未处理的旧目录事件。
            Invoke(window, "FolderChangeMonitor_BatchReady", null,
                new FolderChangeBatch([new(FolderChangeKind.Created, first)]));
            Pump(InvokeAsync(window, "OpenPathAsync", missing));
            DrainDispatcher();
            Require(catalog.Count == 0 && catalog.SourceFolder is null,
                "外部路径失败后旧目录与已排队 watcher 事件不能保留导航目标。");
            Pump(InvokeAsync(window, "NavigateNextAsync"));
            Require(catalog.CurrentPath is null, "打开失败后下一张不能跳回旧本地目录。");

            // 不泵消息便立即打开下一会话，确保旧图加载 continuation 尚未执行。
            var oldFileOpen = InvokeAsync(window, "OpenSingleFileAsync", first, false, null);
            Pump(InvokeAsync(window, "OpenPathAsync", missing));
            Pump(oldFileOpen);
            DrainDispatcher();
            Require(catalog.Count == 0 && Field<object?>(window, "_catalogCompletionCts") is null
                && Field<FolderChangeMonitor>(window, "_folderChangeMonitor").Folder is null,
                "旧文件加载结束不能重新启动目录补齐或监视。");

            var oldFolderOpen = InvokeAsync(window, "OpenFolderAsync", folder, null);
            Pump(InvokeAsync(window, "OpenPathAsync", missing));
            Pump(oldFolderOpen);
            DrainDispatcher();
            Require(catalog.Count == 0, "旧目录扫描结束不能覆盖新的失败会话。");

            Pump(InvokeAsync(window, "OpenFolderAsync", folder, null));
            Require(catalog.Count == 2 && catalog.SourceFolder == folder,
                "失败后仍应能够重新打开普通本地目录。");

            Directory.CreateDirectory(shellTransferFolder);
            var temporaryFile = Path.Combine(shellTransferFolder, Guid.NewGuid().ToString("N") + ".png");
            File.Copy(first, temporaryFile);
            File.Copy(first, Path.Combine(shellTransferFolder, "unrelated.png"));
            var rememberedFolder = settings.LastOpenedFolder;
            Pump(InvokeAsync(window, "OpenPathAsync", temporaryFile));
            DrainDispatcher();
            Require(catalog.Count == 1 && catalog.CurrentPath == temporaryFile
                && Field<bool>(window, "_isDeviceTemporaryCopy")
                && Field<object?>(window, "_catalogCompletionCts") is null
                && Field<FolderChangeMonitor>(window, "_folderChangeMonitor").Folder is null
                && settings.LastOpenedFolder == rememberedFolder,
                "无法识别来源的 Shell 临时文件必须仅查看单图，不扫描缓存目录或记住临时目录。");
            catalog.SortMode = ImageSortMode.NameNaturalDescending;
            Invoke(window, "StartCatalogCompletion", temporaryFile);
            Invoke(window, "StartFolderWatcher", shellTransferFolder);
            Require(Field<object?>(window, "_catalogCompletionCts") is null
                && Field<FolderChangeMonitor>(window, "_folderChangeMonitor").Folder is null,
                "切换排序或应用设置不能重新扫描设备缓存目录。");
            var favorites = Field<FavoriteStore>(window, "_favorites");
            var favoriteCount = favorites.Count;
            Pump(InvokeAsync(window, "DeleteCurrentAsync"));
            Pump(InvokeAsync(window, "ToggleCurrentFavoriteAsync"));
            Pump(InvokeAsync(window, "CompressCurrentAsync"));
            Require(File.Exists(temporaryFile) && favorites.Count == favoriteCount,
                "设备副本的删除、收藏和压缩执行入口必须保护快捷键路径。");
            Pump(InvokeAsync(window, "OpenFolderAsync", folder, null));
            Require(!Field<bool>(window, "_isDeviceTemporaryCopy") && catalog.Count == 2,
                "重新打开本地目录必须清除设备副本状态。");

            RunPortableRecoveryCases(window, folder, first);
        }
        finally
        {
            window.Close();
            DrainDispatcher();
            Directory.Delete(folder, recursive: true);
            if (Directory.Exists(shellTransferFolder)) Directory.Delete(shellTransferFolder, recursive: true);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void RunPortableRecoveryCases(MainWindow window, string folder, string sample)
    {
        var deviceFolder = Path.Combine(folder, "device-session");
        Directory.CreateDirectory(deviceFolder);
        var first = Path.Combine(deviceFolder, "device-1.png");
        var second = Path.Combine(deviceFolder, "device-2.png");
        File.Copy(sample, first);
        File.Copy(sample, second);
        var catalog = Field<ImageCatalog>(window, "_catalog");
        var retryButton = (Button)window.FindName("RetryCurrentButton");
        var recoveryHint = (TextBlock)window.FindName("DeviceRecoveryHint");
        var errorPanel = (FrameworkElement)window.FindName("ErrorPanel");
        var loadingPanel = (FrameworkElement)window.FindName("LoadingPanel");

        var disconnectedTransfer = NewTransfer();
        var retryTransfer = NewTransfer();
        var recoverySource = new FakePortableMediaSource([first, second])
        {
            Ensure = (_, _) => disconnectedTransfer.Task,
            Retry = (_, _) => retryTransfer.Task
        };
        AttachPortableSource(window, recoverySource);
        var failedLoad = InvokeAsync(window, "LoadCurrentAsync");
        Require(recoverySource.EnsureCalls.Count == 1 && loadingPanel.Visibility == Visibility.Visible
            && !retryButton.IsEnabled,
            "手机读取尚未结束时必须显示加载状态且不能发起重试。");
        disconnectedTransfer.SetException(new IOException("设备已断开，请重新连接并解锁手机。"));
        Pump(failedLoad);
        Require(errorPanel.Visibility == Visibility.Visible && retryButton.Visibility == Visibility.Visible
            && retryButton.IsEnabled && recoveryHint.Visibility == Visibility.Visible
            && !string.IsNullOrWhiteSpace(recoveryHint.Text) && catalog.CurrentPath == first,
            "手机断线后必须保留当前项，并显示可执行的重试及设备恢复提示。");

        var recoveryLoad = InvokeAsync(window, "RetryCurrentAsync");
        Require(recoverySource.RetryCalls.Count == 1 && recoverySource.RetryCalls[0].Path == first
            && recoverySource.EnsureCalls.Count == 1 && !retryButton.IsEnabled,
            "错误页重试必须调用来源的重传入口，加载期间禁用重复重试。");
        Pump(InvokeAsync(window, "RetryCurrentAsync"));
        Require(recoverySource.RetryCalls.Count == 1, "加载期间重复重试不能启动第二次设备传输。");
        retryTransfer.SetResult(first);
        Pump(recoveryLoad);
        Require(Field<ImageDocument?>(window, "_currentDocument")?.Path == first
            && errorPanel.Visibility == Visibility.Collapsed && loadingPanel.Visibility == Visibility.Collapsed
            && recoveryHint.Visibility == Visibility.Collapsed && !retryButton.IsEnabled,
            "设备重新连接后重试应恢复当前图并清除错误、加载和恢复提示状态。");

        var saved = Path.Combine(folder, "saved-device-copy.png");
        var originalBytes = File.ReadAllBytes(first);
        Pump(InvokeAsync(window, "SaveDeviceCopyToAsync", saved, CancellationToken.None));
        Require(File.ReadAllBytes(saved).SequenceEqual(originalBytes)
            && File.ReadAllBytes(first).SequenceEqual(originalBytes)
            && catalog.CurrentPath == first && Field<bool>(window, "_isDeviceTemporaryCopy"),
            "保存手机副本必须保留原始字节及原文件，且不能切走当前设备目录。");
        File.Delete(saved);

        var removedCacheSource = new FakePortableMediaSource([first, second])
        {
            Retry = (path, _) =>
            {
                if (!File.Exists(path)) File.Copy(sample, path);
                return Task.FromResult(path);
            }
        };
        AttachPortableSource(window, removedCacheSource);
        Pump(InvokeAsync(window, "LoadCurrentAsync"));
        File.Delete(first); // Only this test's local cache copy; decoded pixels remain visible.
        Pump(InvokeAsync(window, "SaveDeviceCopyToAsync", saved, CancellationToken.None));
        Require(removedCacheSource.RetryCalls.Count == 1
            && File.ReadAllBytes(saved).SequenceEqual(originalBytes)
            && catalog.CurrentPath == first,
            "可见图片的临时缓存被清理后，保存必须重新获取原文件而非误报目标目录不存在。");
        File.Delete(saved);

        RunDeviceSaveSessionSwitchCases(window, folder, sample, first, second);

        // 替身故意忽略取消，模拟 Shell 复制已开始后仍会迟到的成功和失败。
        foreach (var failOldTransfer in new[] { false, true })
        {
            var oldTransfer = NewTransfer();
            var source = new FakePortableMediaSource([first, second])
            {
                Ensure = (path, _) => path == first ? oldTransfer.Task : Task.FromResult(path)
            };
            AttachPortableSource(window, source);
            var oldLoad = InvokeAsync(window, "LoadCurrentAsync");
            Pump(InvokeAsync(window, "NavigateNextAsync"));
            var currentDocument = Field<ImageDocument?>(window, "_currentDocument");
            Require(catalog.CurrentPath == second && currentDocument?.Path == second
                && source.EnsureCalls[0].Token.IsCancellationRequested,
                "快速切图必须取消旧手机读取，并允许下一张先完成。");
            CompleteOldTransfer(oldTransfer, first, failOldTransfer);
            Pump(oldLoad);
            DrainDispatcher();
            Require(catalog.CurrentPath == second
                && ReferenceEquals(currentDocument, Field<ImageDocument?>(window, "_currentDocument"))
                && errorPanel.Visibility == Visibility.Collapsed && loadingPanel.Visibility == Visibility.Collapsed,
                "已取消手机传输的迟到成功或失败都不能覆盖新图或显示过期错误。");
        }

        foreach (var openFolder in new[] { false, true })
        {
            var oldTransfer = NewTransfer();
            var source = new FakePortableMediaSource([first, second])
            {
                Ensure = (_, _) => oldTransfer.Task
            };
            AttachPortableSource(window, source);
            var oldLoad = InvokeAsync(window, "LoadCurrentAsync");
            Pump(openFolder
                ? InvokeAsync(window, "OpenFolderAsync", folder, null)
                : InvokeAsync(window, "OpenPathAsync", sample));
            var currentDocument = Field<ImageDocument?>(window, "_currentDocument");
            Require(currentDocument?.Path == sample && catalog.CurrentPath == sample
                && source.IsDisposed && source.EnsureCalls[0].Token.IsCancellationRequested
                && !Field<bool>(window, "_isDeviceTemporaryCopy"),
                "设备读取期间打开本地目录或路径必须释放旧来源、取消读取并显示新会话。");
            CompleteOldTransfer(oldTransfer, first, fail: openFolder);
            Pump(oldLoad);
            DrainDispatcher();
            Require(catalog.CurrentPath == sample
                && ReferenceEquals(currentDocument, Field<ImageDocument?>(window, "_currentDocument"))
                && catalog.SourceFolder == folder && errorPanel.Visibility == Visibility.Collapsed,
                "旧手机会话的迟到结果不能覆盖新本地路径、目录或文档。");
        }
    }

    private static void RunDeviceSaveSessionSwitchCases(
        MainWindow window, string folder, string sample, string first, string second)
    {
        var settings = Field<ViewerSettings>(window, "_viewerSettings");
        var rememberedFolder = settings.LastOpenedFolder;
        // A one-file local folder keeps catalog completion deterministic: either
        // the initial catalog or its completed replacement has exactly this item.
        var localFolder = Path.Combine(folder, "save-switch-local");
        Directory.CreateDirectory(localFolder);
        var local = Path.Combine(localFolder, "local.png");
        File.Copy(sample, local);
        var destination = Path.Combine(folder, "save-switch-existing.bin");
        byte[] previousBytes = [9, 8, 7, 6];
        var catalog = Field<ImageCatalog>(window, "_catalog");
        var errorPanel = (FrameworkElement)window.FindName("ErrorPanel");
        var loadingPanel = (FrameworkElement)window.FindName("LoadingPanel");
        var recoveryHint = (FrameworkElement)window.FindName("DeviceRecoveryHint");
        var retryButton = (Button)window.FindName("RetryCurrentButton");
        var saveMenu = (MenuItem)window.FindName("SaveDeviceCopyMenuItem");
        try
        {
            foreach (var failOldTransfer in new[] { false, true })
            {
                File.WriteAllBytes(destination, previousBytes);
                var oldTransfer = NewTransfer();
                var expectedFailure = new IOException("旧保存重取稍后失败。");
                var source = new FakePortableMediaSource([first, second])
                {
                    Retry = (_, _) => oldTransfer.Task
                };
                AttachPortableSource(window, source);
                Pump(InvokeAsync(window, "LoadCurrentAsync"));
                var oldSave = InvokeAsync(window, "SaveDeviceCopyToAsync", destination, CancellationToken.None);
                Require(!oldSave.IsCompleted && source.RetryCalls.Count == 1
                    && source.RetryCalls[0].Path == first && Field<bool>(window, "_isSavingDeviceCopy"),
                    "保存应等待受控的原文件重取，并保持保存中状态。");

                // Match the normal test setup: RememberOpenedFolder must return
                // before saving this temporary folder to the user's settings.
                settings.LastOpenedFolder = localFolder;
                Pump(InvokeAsync(window, "OpenPathAsync", local));
                var currentDocument = Field<ImageDocument?>(window, "_currentDocument");
                Require(source.IsDisposed && !oldSave.IsCompleted
                    && currentDocument?.Path == local && catalog.CurrentPath == local,
                    "原文件重取未结束时，打开本地路径必须释放旧来源并显示新图。");

                if (failOldTransfer) oldTransfer.SetException(expectedFailure);
                else oldTransfer.SetResult(first);
                Exception? actualFailure = null;
                try { Pump(oldSave); }
                catch (Exception ex) { actualFailure = ex; }
                DrainDispatcher();
                Require(failOldTransfer
                    ? ReferenceEquals(actualFailure, expectedFailure)
                    : actualFailure is OperationCanceledException,
                    "切换会话后旧保存重取成功必须取消导出，旧失败必须可观察且不能被误报为成功。");
                Require(File.ReadAllBytes(destination).SequenceEqual(previousBytes)
                    && !Directory.EnumerateFiles(folder, ".pixora-copy-*.tmp").Any(),
                    "旧保存的迟到结果不能替换已有目标或遗留导出暂存文件。");
                Require(ReferenceEquals(currentDocument, Field<ImageDocument?>(window, "_currentDocument"))
                    && catalog.CurrentPath == local && catalog.SourceFolder == localFolder
                    && catalog.Paths.SequenceEqual(new[] { local })
                    && !Field<bool>(window, "_isDeviceTemporaryCopy")
                    && Field<object?>(window, "_portableMediaSource") is null
                    && !Field<bool>(window, "_isSavingDeviceCopy")
                    && errorPanel.Visibility == Visibility.Collapsed
                    && loadingPanel.Visibility == Visibility.Collapsed
                    && recoveryHint.Visibility == Visibility.Collapsed
                    && retryButton.Visibility == Visibility.Collapsed && !retryButton.IsEnabled
                    && saveMenu.Visibility == Visibility.Collapsed,
                    "旧保存重取的迟到成功或失败不能污染新会话文档、目录或界面状态，且必须恢复保存标记。");
            }
        }
        finally
        {
            settings.LastOpenedFolder = rememberedFolder;
            // Stop the local watch before removing this test's temporary folder.
            Invoke(window, "BeginOpenSession");
            File.Delete(destination);
            File.Delete(local);
            Directory.Delete(localFolder);
        }
    }

    private static void AttachPortableSource(MainWindow window, FakePortableMediaSource source)
    {
        Invoke(window, "BeginOpenSession");
        typeof(MainWindow).GetField("_portableMediaSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, source);
        typeof(MainWindow).GetField("_isDeviceTemporaryCopy", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, true);
        var catalog = Field<ImageCatalog>(window, "_catalog");
        catalog.SortMode = ImageSortMode.NameNatural;
        catalog.LoadFromPaths(source.Paths, source.InitialPath, requireExistingFiles: false);
        Invoke(window, "RefreshThumbnailItems");
    }

    private static TaskCompletionSource<string> NewTransfer() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void CompleteOldTransfer(TaskCompletionSource<string> transfer, string path, bool fail)
    {
        if (fail) transfer.SetException(new IOException("旧设备传输稍后失败。"));
        else transfer.SetResult(path);
    }

    private sealed class FakePortableMediaSource(IReadOnlyList<string> paths) : IPortableMediaSource
    {
        public IReadOnlyList<string> Paths { get; } = paths;
        public string InitialPath => Paths[0];
        public bool IsDisposed { get; private set; }
        public List<(string Path, CancellationToken Token)> EnsureCalls { get; } = [];
        public List<(string Path, CancellationToken Token)> RetryCalls { get; } = [];
        public Func<string, CancellationToken, Task<string>> Ensure { get; init; } =
            (path, _) => Task.FromResult(path);
        public Func<string, CancellationToken, Task<string>> Retry { get; init; } =
            (path, _) => Task.FromResult(path);

        public bool ContainsPath(string path) => Paths.Contains(path, StringComparer.OrdinalIgnoreCase);

        public Task<string> EnsureLocalFileAsync(string path, CancellationToken cancellationToken)
        {
            EnsureCalls.Add((path, cancellationToken));
            return Ensure(path, cancellationToken);
        }

        public Task<string> RetryLocalFileAsync(string path, CancellationToken cancellationToken)
        {
            RetryCalls.Add((path, cancellationToken));
            return Retry(path, cancellationToken);
        }

        public void Dispose() => IsDisposed = true;
    }

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static Task InvokeAsync(MainWindow window, string name, params object?[] args) =>
        (Task)Invoke(window, name, args)!;

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timeout.Tick += (_, _) => frame.Continue = false;
        timeout.Start();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        if (!task.IsCompleted)
        {
            Dispatcher.PushFrame(frame);
        }

        timeout.Stop();
        Require(task.IsCompleted, "打开会话回归测试超时。");
        task.GetAwaiter().GetResult();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
