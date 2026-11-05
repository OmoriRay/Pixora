using System.IO;
using System.Reflection;
using Pixora.Services;

namespace Pixora.SmokeTests;

internal static class PortableMediaRegressionTests
{
    public static void Run()
    {
        Require(!PortableMediaSource.IsShellTransferPath(@"C:\Pictures\sample.jpg"), "本地图片不能触发手机目录发现。");
        Require(!PortableMediaSource.IsShellTransferPath(string.Empty), "空路径不能触发目录发现。");
        var cache = Environment.GetFolderPath(Environment.SpecialFolder.InternetCache);
        Require(PortableMediaSource.IsShellTransferPath(Path.Combine(cache, "test", "photo[1].jpg")), "INetCache 子文件应被识别。");
        Require(!PortableMediaSource.IsShellTransferPath(cache + "-other\\photo.jpg"), "目录前缀相似不能被误识别。");

        Require(Call<bool>("MatchesTransferredName", "photo[12].JPG", "photo.jpg"), "Shell 数字后缀应匹配原文件名。");
        Require(Call<bool>("MatchesTransferredName", "photo[1].jpg", "photo[1].jpg"), "原名自带方括号仍应精确匹配。");
        Require(!Call<bool>("MatchesTransferredName", "photo[1].png", "photo.jpg"), "扩展名不同不能匹配。");
        Require(!Call<bool>("MatchesTransferredName", "photo-other.jpg", "photo.jpg"), "普通文件名不能模糊匹配。");
        foreach (var name in new[] { "..", "../photo.jpg", "C:\\photo.jpg", "photo.jpg.", "photo.jpg ", "CON.jpg", "NUL.extra.jpg" })
            Require(!Call<bool>("IsSafeFileName", name), "应拒绝危险或无法安全映射的名称。");
        Require(Call<bool>("IsSafeFileName", "示例[1].jpg"), "合法中文文件名应可映射。");

        var file = Path.Combine(Path.GetTempPath(), "Pixora-portable-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(file, [1, 2, 3]);
            Require(!Call<bool>("IsCompleteReadableFile", file, 4L), "部分文件不能视为完成。");
            Require(Call<bool>("IsCompleteReadableFile", file, 3L), "完整且可读文件应通过。");
            using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                Require(!Call<bool>("IsCompleteReadableFile", file, 3L), "大小相同但仍被写入的文件不能视为完成。");
            Require(PortableMediaSource.TryCreateAsync(file, CancellationToken.None).GetAwaiter().GetResult() is null,
                "普通本地文件不应创建 Shell 会话。");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                PortableMediaSource.TryCreateAsync(file, canceled.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("已取消的发现必须抛出取消。");
            }
            catch (OperationCanceledException) { }

            VerifySessionLifetimeAsync(file).GetAwaiter().GetResult();
            VerifyStagedTransfers();
        }
        finally { File.Delete(file); }
        Console.WriteLine("手机目录无设备回归通过。");
    }

    private static async Task VerifySessionLifetimeAsync(string file)
    {
        using var source = (PortableMediaSource)Activator.CreateInstance(typeof(PortableMediaSource), nonPublic: true)!;
        var entryType = typeof(PortableMediaSource).GetNestedType("Entry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, file, 3L, "device/photo.jpg")!;
        var entries = (System.Collections.IDictionary)typeof(PortableMediaSource)
            .GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source)!;
        entries.Add(file, entry);
        var completed = (HashSet<string>)typeof(PortableMediaSource)
            .GetField("_completed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source)!;
        completed.Add(file);
        Require(await source.EnsureLocalFileAsync(file, CancellationToken.None) == file,
            "完整的已下载文件应直接复用且不调用设备。");
        Require(await source.RetryLocalFileAsync(file, CancellationToken.None) == file,
            "显式重试应保留完整缓存，不连接设备或覆盖文件。");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await source.RetryLocalFileAsync(file, canceled.Token);
            throw new InvalidOperationException("已取消的文件请求不能复用会话文件。");
        }
        catch (OperationCanceledException) { }
        try
        {
            await source.EnsureLocalFileAsync(file + "-foreign", CancellationToken.None);
            throw new InvalidOperationException("会话必须拒绝快照以外的文件。");
        }
        catch (ArgumentException) { }
        using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        // Prevent an actual copy: this simulates a CopyHere still in progress, with
        // a file that has reached its final size but is still open for writing.
        var attemptType = typeof(PortableMediaSource).GetNestedType("TransferAttempt", BindingFlags.NonPublic)!;
        var attempt = Activator.CreateInstance(attemptType, Path.GetDirectoryName(file)!, Path.GetFileName(file), 3L)!;
        attemptType.GetProperty("Started")!.SetValue(attempt, true);
        entryType.GetProperty("Attempt")!.SetValue(entry, attempt);
        completed.Clear();
        var pending = source.EnsureLocalFileAsync(file, CancellationToken.None);
        await Task.Delay(50);
        Require(!pending.IsCompleted, "尚在写入的完整大小文件必须继续等待。");
        source.Dispose();
        source.Dispose();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("释放会话必须取消等待中的传输。");
        }
        catch (OperationCanceledException) { }
        try
        {
            await source.RetryLocalFileAsync(file, CancellationToken.None);
            throw new InvalidOperationException("释放后的会话必须拒绝重试。");
        }
        catch (ObjectDisposedException) { }
    }

    private static void VerifyStagedTransfers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Pixora-transfer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldCopy = Path.Combine(directory, "old.jpg");
        var newCopy = Path.Combine(directory, "retry.jpg");
        var final = Path.Combine(directory, "final.jpg");
        try
        {
            File.WriteAllBytes(oldCopy, [1]);
            File.WriteAllBytes(newCopy, [4, 5, 6]);
            try
            {
                Publish(oldCopy, final, 3);
                throw new InvalidOperationException("不完整的旧传输不能发布。");
            }
            catch (IOException) { }
            using (var writer = new FileStream(oldCopy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                Publish(newCopy, final, 3);
                Require(File.ReadAllBytes(final).SequenceEqual(new byte[] { 4, 5, 6 }),
                    "重试完成的副本应可独立发布，不接触仍在写入的旧目标。");
                Require(writer.Length == 1, "发布重试不能覆盖或删除旧传输目标。");
            }
            File.WriteAllBytes(newCopy, [7, 8, 9]);
            try
            {
                Publish(newCopy, final, 3);
                throw new InvalidOperationException("稳定缓存不可被后到的旧传输覆盖。");
            }
            catch (IOException) { }
            Require(File.ReadAllBytes(final).SequenceEqual(new byte[] { 4, 5, 6 }), "稳定缓存内容必须保留。");
            Require(Call<bool>("MatchesSourceIdentity", "device/folder/photo.jpg", "photo.jpg", 3L,
                "device/folder/photo.jpg", "photo.jpg", "3"), "同身份名称大小应允许重连。");
            Require(!Call<bool>("MatchesSourceIdentity", "device/folder/photo.jpg", "photo.jpg", 3L,
                "other/folder/photo.jpg", "photo.jpg", "3"), "同名同大小的其他来源不能用于重试。");
            Require(!Call<bool>("MatchesSourceIdentity", "device/folder/photo.jpg", "photo.jpg", 3L,
                "device/folder/photo.jpg", "photo.jpg", "4"), "原文件大小改变不能用于重试。");
            Require(!Call<bool>("MatchesSourceIdentity", "device/folder/photo.jpg", "photo.jpg", 3L,
                "device/folder/photo.jpg", "renamed.jpg", "3"), "原文件名称改变不能用于重试。");
        }
        finally
        {
            foreach (var path in new[] { oldCopy, newCopy, final }) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void Publish(string staging, string destination, long size)
    {
        try { Call<object?>("PublishCompletedFile", staging, destination, size); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException io) { throw io; }
    }

    // Only invokes an already open Explorer folder. No device tree traversal or writes.
    public static async Task RunDeviceAsync(string transferredPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var source = await PortableMediaSource.TryCreateAsync(transferredPath, timeout.Token)
            ?? throw new InvalidOperationException("未能从当前资源管理器窗口唯一匹配手机目录。");
        Require(source.Paths.Count > 0 && source.ContainsPath(source.InitialPath), "手机快照缺少初始项。");
        Require(!File.Exists(source.InitialPath), "创建快照时不应提前下载初始项。");
        Console.WriteLine($"手机目录媒体项：{source.Paths.Count}；初始化本地下载：0");
        var local = await source.EnsureLocalFileAsync(source.InitialPath, timeout.Token);
        var image = await Task.Run(() => ImageLoader.Load(local, timeout.Token), timeout.Token);
        Console.WriteLine($"初始项下载解码：{image.Bitmap.PixelWidth}x{image.Bitmap.PixelHeight}；字节：{new FileInfo(local).Length}");
        Require(source.Paths.Count(File.Exists) == 1, "初次加载只应下载指定项。");
        var downloaded = 1;
        foreach (var next in source.Paths.Where(path => !string.Equals(path, source.InitialPath, StringComparison.OrdinalIgnoreCase)))
        {
            local = await source.EnsureLocalFileAsync(next, timeout.Token);
            image = await Task.Run(() => ImageLoader.Load(local, timeout.Token), timeout.Token);
            downloaded++;
            Console.WriteLine($"第{downloaded}项下载解码：{image.Bitmap.PixelWidth}x{image.Bitmap.PixelHeight}；字节：{new FileInfo(local).Length}");
            Require(source.Paths.Count(File.Exists) == downloaded, "每次导航只应额外下载指定项。");
        }
        Require(await source.EnsureLocalFileAsync(source.InitialPath, timeout.Token) == source.InitialPath,
            "重复导航必须复用已下载文件。");
        Require(source.Paths.Count(File.Exists) == downloaded, "重复访问不能新增下载。");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await source.EnsureLocalFileAsync(source.InitialPath, canceled.Token);
            throw new InvalidOperationException("已取消的设备请求必须停止。");
        }
        catch (OperationCanceledException) { }
        await source.EnsureLocalFileAsync(source.InitialPath, timeout.Token);
        var sessionDirectory = Path.GetDirectoryName(source.InitialPath)!;
        source.Dispose();
        source.Dispose();
        try
        {
            await source.EnsureLocalFileAsync(source.InitialPath, timeout.Token);
            throw new InvalidOperationException("已释放会话必须拒绝下载。");
        }
        catch (ObjectDisposedException) { }
        for (var attempt = 0; attempt < 50 && Directory.Exists(sessionDirectory); attempt++) await Task.Delay(100);
        Require(!Directory.Exists(sessionDirectory), "完整传输会话释放后应清理自身临时文件。");
        Console.WriteLine("全部媒体按需解码、重复访问、取消恢复及会话清理通过。");
    }

    private static T Call<T>(string name, params object[] args) => (T)typeof(PortableMediaSource)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
