using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using Pixora.Services;

internal static class SingleInstanceCoordinatorRegressionTests
{
    // Uses private constructor and unique pipe names so a running viewer is untouched.
    public static async Task RunAsync()
    {
        await WaitsForHandlerAsync();
        await ReportsHandlerFailureAsync();
        await CancellationAfterDeliveryDoesNotRetryAsync();
        await MissingServerDoesNotWaitForeverAsync();
        await LegacyServerWithoutAcknowledgementAsync();
        await LegacyReadOnlyServerAllowsSafeFallbackAsync();
        await LegacyWriteOnlyClientAsync();
        await NewRequestCompletesWhileOlderHandlerWaitsAsync();
        await IdleClientDoesNotBlockRequestsAsync();
        await LateOlderRequestDoesNotReplaceNewSelectionAsync();
        await ShutdownReleasesPendingConnectionsAsync();
        Console.WriteLine("SingleInstanceCoordinator：11 项管道确认回归通过。");
    }

    private static async Task LateOlderRequestDoesNotReplaceNewSelectionAsync()
    {
        var name = NewPipeName();
        using var primary = Create(true, name);
        using var newerClient = Create(false, name);
        var paths = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        primary.StartServer(path => { paths.Enqueue(path); return Task.CompletedTask; });
        await using var olderClient = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await olderClient.ConnectAsync(2_000);
        Require(await newerClient.TryForwardAsync("最新选择.jpg").WaitAsync(TimeSpan.FromSeconds(5)),
            "后连接的有效请求应能越过未发送路径的旧连接。");
        await using var writer = new StreamWriter(olderClient, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(olderClient, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("迟到旧选择.jpg");
        Require(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "Pixora.Forward.OK",
            "已经被新选择替代的请求应正常释放发送方，不触发本地回退。");
        Require(paths.SequenceEqual(new[] { "最新选择.jpg" }), "迟到的旧连接不能再次调用处理器覆盖最新选择。");
    }

    private static async Task NewRequestCompletesWhileOlderHandlerWaitsAsync()
    {
        var name = NewPipeName();
        using var primary = Create(true, name);
        using var olderClient = Create(false, name);
        using var newerClient = Create(false, name);
        var olderEntered = NewSignal();
        var newerEntered = NewSignal();
        var releaseOlder = NewSignal();
        primary.StartServer(async path =>
        {
            if (path == "慢手机照片.jpg")
            {
                olderEntered.TrySetResult();
                await releaseOlder.Task;
                throw new IOException("预期的旧设备请求失败。");
            }

            Require(path == "新照片.jpg", "后续连接应保留自己的路径。");
            newerEntered.TrySetResult();
        });

        var older = olderClient.TryForwardAsync("慢手机照片.jpg");
        try
        {
            await olderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newer = newerClient.TryForwardAsync("新照片.jpg");
            await newerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(await newer.WaitAsync(TimeSpan.FromSeconds(5)), "旧请求未结束时，新请求应独立完成并收到确认。");
            Require(!older.IsCompleted, "旧请求应继续保活，不能被新请求的确认提前释放。");
        }
        finally
        {
            releaseOlder.TrySetResult();
        }

        Require(await older.WaitAsync(TimeSpan.FromSeconds(5)), "被新选择替代的旧请求失败不能触发独立窗口回退。");
        Require(await newerClient.TryForwardAsync("新照片.jpg").WaitAsync(TimeSpan.FromSeconds(5)),
            "旧请求失败后，接收循环应仍可处理下一次请求。");
    }

    private static async Task IdleClientDoesNotBlockRequestsAsync()
    {
        var name = NewPipeName();
        using var primary = Create(true, name);
        using var secondary = Create(false, name);
        var calls = 0;
        primary.StartServer(path =>
        {
            Require(path == "有效请求.jpg", "未发送首行的连接不能触发空路径打开。");
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        await using (var idle = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await idle.ConnectAsync(2_000);
            Require(await secondary.TryForwardAsync("有效请求.jpg").WaitAsync(TimeSpan.FromSeconds(5)),
                "已经连接但未发送路径的客户端不能阻塞后续请求。");
        }

        Require(await secondary.TryForwardAsync("有效请求.jpg").WaitAsync(TimeSpan.FromSeconds(5)),
            "空连接断开后服务应继续受理请求。");
        Require(Volatile.Read(ref calls) == 2, "每个有效请求只能调用一次处理器，空连接不能调用处理器。");
    }

    private static async Task ShutdownReleasesPendingConnectionsAsync()
    {
        var name = NewPipeName();
        var primary = Create(true, name);
        using var secondary = Create(false, name);
        await using var idle = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var entered = NewSignal();
        var release = NewSignal();
        var disposed = false;
        primary.StartServer(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var forwarding = secondary.TryForwardAsync("关停等待.jpg");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await idle.ConnectAsync(2_000);
            var serverTask = (Task)typeof(SingleInstanceCoordinator)
                .GetField("_serverTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(primary)!;
            primary.Dispose();
            disposed = true;
            Require(await forwarding.WaitAsync(TimeSpan.FromSeconds(5)),
                "关停时已送达请求应释放确认等待，并避免结果不明时重复打开。");
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(idle, Encoding.UTF8, leaveOpen: true);
            Require(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null,
                "关停应关闭尚未发送路径的连接。");
            Require(!release.Task.IsCompleted, "关停不能依赖不可取消的处理器完成，否则可能卡住 UI 退出。");
        }
        finally
        {
            release.TrySetResult();
            if (!disposed) primary.Dispose();
        }
    }

    private static async Task WaitsForHandlerAsync()
    {
        var name = NewPipeName();
        using var primary = Create(true, name);
        using var secondary = Create(false, name);
        var entered = NewSignal();
        var release = NewSignal();
        primary.StartServer(async path =>
        {
            Require(path == "手机照片.jpg", "转发路径应保持不变。");
            entered.SetResult();
            await release.Task;
        });
        var forwarding = secondary.TryForwardAsync("手机照片.jpg");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Require(!forwarding.IsCompleted, "路径处理尚未结束时不能退出启动进程。");
        }
        finally
        {
            release.TrySetResult();
        }

        Require(await forwarding.WaitAsync(TimeSpan.FromSeconds(5)), "处理结束后应收到成功确认。");
    }

    private static async Task ReportsHandlerFailureAsync()
    {
        var name = NewPipeName();
        using var primary = Create(true, name);
        using var secondary = Create(false, name);
        var calls = 0;
        primary.StartServer(_ => ++calls == 1
            ? Task.FromException(new IOException("预期的路径处理失败。"))
            : Task.CompletedTask);
        Require(!await secondary.TryForwardAsync("失败.jpg").WaitAsync(TimeSpan.FromSeconds(5)),
            "明确处理失败应允许本地回退。");
        Require(await secondary.TryForwardAsync("恢复.jpg").WaitAsync(TimeSpan.FromSeconds(5)),
            "处理失败后服务应继续接收请求。");
    }

    private static async Task CancellationAfterDeliveryDoesNotRetryAsync()
    {
        var name = NewPipeName();
        using var primary = Create(true, name);
        using var secondary = Create(false, name);
        using var cancellation = new CancellationTokenSource();
        var entered = NewSignal();
        var release = NewSignal();
        primary.StartServer(async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        var forwarding = secondary.TryForwardAsync("等待.jpg", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Require(await forwarding.WaitAsync(TimeSpan.FromSeconds(5)),
                "已送达但确认等待取消时不能重复打开，也不能无限等待。");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static async Task MissingServerDoesNotWaitForeverAsync()
    {
        using var secondary = Create(false, NewPipeName());
        Require(!await secondary.TryForwardAsync("无服务.jpg").WaitAsync(TimeSpan.FromSeconds(5)),
            "未连接到服务时应在有限时间内允许本地打开。");
    }

    private static async Task LegacyServerWithoutAcknowledgementAsync()
    {
        var name = NewPipeName();
        using var secondary = Create(false, name);
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var forwarding = secondary.TryForwardAsync("旧协议.jpg");
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        Require(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "旧协议.jpg", "旧协议首行仍应为路径。");
        server.Dispose();
        Require(await forwarding.WaitAsync(TimeSpan.FromSeconds(5)), "无确认且已送达时不能重复打开。");
    }

    private static async Task LegacyWriteOnlyClientAsync()
    {
        var name = NewPipeName();
        using var primary = Create(true, name);
        var handled = NewSignal();
        primary.StartServer(_ => { handled.TrySetResult(); return Task.CompletedTask; });
        await using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(2_000);
            await using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            await writer.WriteLineAsync("旧客户端.jpg");
        }

        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task LegacyReadOnlyServerAllowsSafeFallbackAsync()
    {
        var name = NewPipeName();
        using var secondary = Create(false, name);
        await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Require(!await secondary.TryForwardAsync("旧服务.jpg").WaitAsync(TimeSpan.FromSeconds(5)),
            "只读旧服务拒绝双向连接时，应在未发送前允许安全本地回退。");
        Require(!server.IsConnected, "安全回退前不能向旧服务发送路径。");
    }

    private static SingleInstanceCoordinator Create(bool primary, string pipeName) =>
        (SingleInstanceCoordinator)Activator.CreateInstance(typeof(SingleInstanceCoordinator),
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: [new Mutex(initiallyOwned: false), primary, pipeName], culture: null)!;

    private static string NewPipeName() => $"Pixora.Regression.{Guid.NewGuid():N}";
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
