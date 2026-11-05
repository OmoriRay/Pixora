using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Pixora.Services;

/// <summary>
/// A read-only snapshot of an open Explorer MTP folder. Paths refer to a private local
/// session; every file is transferred only on demand, including the initial item.
/// </summary>
public sealed class PortableMediaSource : IPortableMediaSource
{
    private readonly ShellApartment _apartment = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
    private string? _sessionDirectory;
    private object? _shell;
    private string? _folderIdentity;
    private readonly List<TransferAttempt> _attempts = [];
    private int _disposed;

    private PortableMediaSource() { }

    public IReadOnlyList<string> Paths { get; private set; } = Array.Empty<string>();
    public string InitialPath { get; private set; } = string.Empty;

    /// <summary>Ordinary local files never trigger Shell folder discovery.</summary>
    public static bool IsShellTransferPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var cache = Environment.GetFolderPath(Environment.SpecialFolder.InternetCache);
            return !string.IsNullOrWhiteSpace(cache)
                && fullPath.StartsWith(Path.TrimEndingDirectorySeparator(cache) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static async Task<PortableMediaSource?> TryCreateAsync(string localPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsShellTransferPath(localPath) || !File.Exists(localPath)) return null;
        var source = new PortableMediaSource();
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(token, source._lifetime.Token);
        discovery.CancelAfter(TimeSpan.FromSeconds(15));
        var discoveryToken = discovery.Token;
        try
        {
            var initialized = await source._apartment.InvokeAsync(() => source.Initialize(localPath, discoveryToken))
                .WaitAsync(discoveryToken).ConfigureAwait(false);
            if (initialized) return source;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (OperationCanceledException) { source.Dispose(); throw; }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
            or TimeoutException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Discovery is best-effort: keep displaying the file Explorer supplied.
        }
        source.Dispose();
        return null;
    }

    public bool ContainsPath(string path) => _entries.ContainsKey(path);

    public Task<string> EnsureLocalFileAsync(string path, CancellationToken token) =>
        GetLocalFileAsync(path, retry: false, token);

    /// <summary>Retries into a fresh destination; a previous Shell copy is never overwritten.</summary>
    public Task<string> RetryLocalFileAsync(string path, CancellationToken token) =>
        GetLocalFileAsync(path, retry: true, token);

    private async Task<string> GetLocalFileAsync(string path, bool retry, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_entries.TryGetValue(path, out var entry))
            throw new ArgumentException("文件不属于当前手机目录。", nameof(path));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(2));
        var cancellationToken = linked.Token;
        var ownsGate = false;
        try
        {
            await _transferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsGate = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (_completed.Contains(path) && IsCompleteReadableFile(path, entry.Size)) return path;
            if (_completed.Contains(path))
            {
                // Temporary-file cleanup can remove the bytes while the decoded
                // image is still visible. An explicit retry may fetch them again.
                if (!retry || File.Exists(path))
                    throw new IOException("已下载的手机缓存暂时无法读取，请重试或重新打开手机照片。");
                _completed.Remove(path);
            }
            // Cancellation only abandons the waiter. Explicit retries isolate the old
            // non-cancellable CopyHere operation in its own destination directory.
            var attempt = entry.Attempt;
            if (attempt is null || retry)
            {
                attempt = await _apartment.InvokeAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A reconnect invalidates other entries' old COM objects too.
                    // Resolve every new copy against the current matching folder.
                    var item = ReconnectItem(entry);
                    cancellationToken.ThrowIfCancellationRequested();
                    var transfer = new TransferAttempt(Path.Combine(_sessionDirectory!, Guid.NewGuid().ToString("N")), entry.Name, entry.Size);
                    Directory.CreateDirectory(transfer.Directory);
                    _attempts.Add(transfer);
                    entry.Attempt = transfer;
                    dynamic shell = _shell!;
                    dynamic destination = _apartment.Keep((object)shell.NameSpace(transfer.Directory));
                    cancellationToken.ThrowIfCancellationRequested();
                    transfer.Started = true;
                    destination.CopyHere(item, 4 | 16 | 512 | 1024);
                    return transfer;
                }).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            var stableChecks = 0;
            while (stableChecks < 3)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stableChecks = IsCompleteReadableFile(attempt.Path, entry.Size) ? stableChecks + 1 : 0;
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            PublishCompletedFile(attempt.Path, path, entry.Size);
            attempt.Published = true;
            _completed.Add(path);
            return path;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("从手机读取文件超时，请检查手机连接并保持设备解锁，然后重试。");
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new IOException("手机连接已中断，请重新连接并解锁设备，然后重试。", ex);
        }
        finally { if (ownsGate) _transferGate.Release(); }
    }

    private object ReconnectItem(Entry entry)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new IOException("无法连接手机目录，请重新打开手机照片。");
        _shell = _apartment.Keep(Activator.CreateInstance(shellType)!);
        dynamic shell = _shell;
        object? folderObject = FindOpenFolder(shell);
        folderObject ??= shell.NameSpace(_folderIdentity!);
        if (folderObject is null)
            throw new IOException("手机目录暂不可用。请连接并解锁手机，在资源管理器中重新打开原手机目录，再点击“重试读取”；仍失败时请重新双击照片。");
        dynamic folder = _apartment.Keep(folderObject);
        dynamic self = _apartment.Keep((object)folder.Self);
        if ((bool)self.IsFileSystem || !string.Equals((string)self.Path, _folderIdentity, StringComparison.OrdinalIgnoreCase))
            throw new IOException("手机目录身份已改变，请重新打开手机照片。");
        object? itemObject = folder.ParseName(entry.Name);
        if (itemObject is null) throw new IOException("手机上的原文件暂不可用，请检查连接后重试。");
        dynamic item = _apartment.Keep(itemObject);
        string name = Convert.ToString(item.ExtendedProperty("System.FileName")) ?? string.Empty;
        if (string.IsNullOrEmpty(name)) name = (string)item.Name;
        if ((bool)item.IsFolder || !MatchesSourceIdentity(entry.Identity, entry.Name, entry.Size,
            (string)item.Path, name, Convert.ToString(item.ExtendedProperty("System.Size"))))
            throw new IOException("手机上的原文件已改变，请重新打开该照片。");
        return itemObject;
    }

    private object? FindOpenFolder(dynamic shell)
    {
        // MTP Self.Path values are not necessarily accepted by NameSpace(string).
        // Resolve the same open Explorer folder afresh, as during discovery.
        dynamic windows = _apartment.Keep((object)shell.Windows());
        for (var i = 0; i < (int)windows.Count; i++)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                dynamic window = _apartment.Keep((object)windows.Item(i));
                dynamic document = _apartment.Keep((object)window.Document);
                dynamic folder = _apartment.Keep((object)document.Folder);
                dynamic self = _apartment.Keep((object)folder.Self);
                if (!(bool)self.IsFileSystem
                    && string.Equals((string)self.Path, _folderIdentity, StringComparison.OrdinalIgnoreCase))
                    return (object)folder;
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // An Explorer window may close or navigate during the lookup.
            }
        }
        return null;
    }

    internal static bool MatchesSourceIdentity(string identity, string name, long size,
        string actualIdentity, string actualName, string? actualSize) =>
        !string.IsNullOrWhiteSpace(identity)
        && string.Equals(identity, actualIdentity, StringComparison.OrdinalIgnoreCase)
        && string.Equals(name, actualName, StringComparison.OrdinalIgnoreCase)
        && long.TryParse(actualSize, out var parsedSize) && parsedSize == size;

    internal static void PublishCompletedFile(string stagingPath, string finalPath, long size)
    {
        // Deny concurrent writers through the rename, and never overwrite an existing
        // cache. Shell never receives finalPath as its copy destination.
        using var stream = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (size <= 0 || stream.Length != size) throw new IOException("手机文件尚未完整传输。");
        File.Move(stagingPath, finalPath, overwrite: false);
    }

    private bool Initialize(string localPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        var localInfo = new FileInfo(localPath);
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return false;
        _shell = _apartment.Keep(Activator.CreateInstance(shellType)!);
        dynamic shell = _shell;
        dynamic windows = _apartment.Keep((object)shell.Windows());
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<Entry>? selectedEntries = null;
        Entry? selectedItem = null;
        var matches = 0;
        string? selectedFolderIdentity = null;
        for (var i = 0; i < (int)windows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            _lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                dynamic window = _apartment.Keep((object)windows.Item(i));
                dynamic document = _apartment.Keep((object)window.Document);
                dynamic folder = _apartment.Keep((object)document.Folder);
                dynamic self = _apartment.Keep((object)folder.Self);
                string folderPath = self.Path;
                // Limit enumeration to portable-device namespaces, never other open folders.
                if ((bool)self.IsFileSystem || !folderPath.Contains("usb#", StringComparison.OrdinalIgnoreCase)
                    || !visited.Add(folderPath)) continue;
                var entries = ReadEntries(folder, token);
                foreach (var item in entries)
                {
                    if (item.Size != localInfo.Length || !MatchesTransferredName(localInfo.Name, item.Name)) continue;
                    matches++;
                    selectedItem = item;
                    selectedEntries = entries;
                    selectedFolderIdentity = folderPath;
                }
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // A window can close or navigate while it is being inspected.
            }
        }
        if (matches != 1 || selectedItem is null || selectedEntries is null) return false;
        token.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        _sessionDirectory = Path.Combine(Path.GetTempPath(), "Pixora", "portable-media", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionDirectory);
        _folderIdentity = selectedFolderIdentity;
        foreach (var item in selectedEntries)
            _entries.Add(Path.Combine(_sessionDirectory, item.Name), item);
        Paths = Array.AsReadOnly(_entries.Keys.ToArray());
        InitialPath = Path.Combine(_sessionDirectory, selectedItem.Name);
        // Explorer may still be writing its INetCache copy. Transfer the initial item
        // through the same completeness checks as every later navigation request.
        return true;
    }

    private List<Entry> ReadEntries(dynamic folder, CancellationToken token)
    {
        dynamic items = _apartment.Keep((object)folder.Items());
        var result = new List<Entry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < (int)items.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            _lifetime.Token.ThrowIfCancellationRequested();
            dynamic item = _apartment.Keep((object)items.Item(i));
            if ((bool)item.IsFolder) continue;
            string name = Convert.ToString(item.ExtendedProperty("System.FileName")) ?? string.Empty;
            if (string.IsNullOrEmpty(name)) name = (string)item.Name;
            if (!IsSafeFileName(name) || !ImageCatalog.IsSupportedMediaPath(name)) continue;
            object? sizeValue = item.ExtendedProperty("System.Size");
            if (sizeValue is null || !long.TryParse(Convert.ToString(sizeValue), out var size) || size <= 0) continue;
            // Windows paths cannot represent two names differing only in case safely.
            if (!names.Add(name)) return [];
            result.Add(new Entry(name, size, (string)item.Path));
        }
        return result;
    }

    internal static bool MatchesTransferredName(string transferredName, string originalName)
    {
        if (string.Equals(transferredName, originalName, StringComparison.OrdinalIgnoreCase)) return true;
        var stem = Path.GetFileNameWithoutExtension(transferredName);
        var normalized = Regex.Replace(stem, @"\[\d+\]$", string.Empty, RegexOptions.CultureInvariant);
        return !string.Equals(stem, normalized, StringComparison.Ordinal)
            && string.Equals(normalized + Path.GetExtension(transferredName), originalName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeFileName(string name) => !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.EndsWith('.') && !name.EndsWith(' ')
        && !Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);

    internal static bool IsCompleteReadableFile(string path, long expectedSize)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length == expectedSize;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        // Do not synchronously join a thread which might be inside a device COM call.
        _apartment.Dispose();
        _ = CleanupSessionAsync();
    }

    private async Task CleanupSessionAsync()
    {
        try
        {
            // Release follows all queued COM calls, so the snapshot can no longer
            // change and no transfer can start after the checks below.
            await _apartment.ShutdownTask.ConfigureAwait(false);
            var directory = _sessionDirectory;
            if (directory is null) return;
            await _transferGate.WaitAsync().ConfigureAwait(false);
            try
            {
                for (var attempt = 0; attempt < 120; attempt++)
                {
                    var cleaned = true;
                    foreach (var transfer in _attempts)
                    {
                        transfer.CleanupReady |= !transfer.Started || transfer.Published
                            || IsCompleteReadableFile(transfer.Path, transfer.Size);
                        if (!transfer.CleanupReady) { cleaned = false; continue; }
                        try
                        {
                            File.Delete(transfer.Path);
                            if (Directory.Exists(transfer.Directory)) Directory.Delete(transfer.Directory, recursive: false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleaned = false; }
                    }
                    // Published files are never Shell destinations and can be cleaned even
                    // when a disconnected device left an unfinished attempt behind.
                    foreach (var path in _entries.Keys)
                    {
                        try { File.Delete(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleaned = false; }
                    }
                    if (cleaned)
                    {
                        try { Directory.Delete(directory, recursive: false); return; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            finally { _transferGate.Release(); }
            // A disconnected device or external file lock can outlive this grace
            // period. Leave that session to OS temporary-file cleanup.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { }
    }

    private sealed class Entry(string name, long size, string identity)
    {
        public string Name { get; } = name;
        public long Size { get; } = size;
        public string Identity { get; } = identity;
        public TransferAttempt? Attempt { get; set; }
    }

    private sealed class TransferAttempt(string directory, string name, long size)
    {
        public string Directory { get; } = directory;
        public string Path { get; } = System.IO.Path.Combine(directory, name);
        public long Size { get; } = size;
        public bool Started { get; set; }
        public bool Published { get; set; }
        public bool CleanupReady { get; set; }
    }

    private sealed class ShellApartment : IDisposable
    {
        private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<object> _objects = [];
        public Task ShutdownTask { get; private set; } = Task.CompletedTask;

        public ShellApartment()
        {
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ready.SetResult(dispatcher);
                Dispatcher.Run();
            }) { IsBackground = true, Name = "Portable media Shell" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public object Keep(object value)
        {
            if (Marshal.IsComObject(value) && !_objects.Any(existing => ReferenceEquals(existing, value))) _objects.Add(value);
            return value;
        }

        public async Task<T> InvokeAsync<T>(Func<T> action)
        {
            var dispatcher = await _ready.Task.ConfigureAwait(false);
            if (dispatcher.HasShutdownStarted) throw new OperationCanceledException("手机目录会话已结束。");
            return await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            ShutdownTask = ReleaseAsync();
        }

        private async Task ReleaseAsync()
        {
            var dispatcher = await _ready.Task.ConfigureAwait(false);
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    for (var i = _objects.Count - 1; i >= 0; i--)
                    {
                        try { Marshal.ReleaseComObject(_objects[i]); }
                        catch (InvalidComObjectException) { }
                    }
                    _objects.Clear();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }).Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException) { }
        }
    }
}
