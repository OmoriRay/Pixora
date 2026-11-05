using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Pixora.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string ForwardSucceeded = "Pixora.Forward.OK";
    private const string ForwardFailed = "Pixora.Forward.ERROR";
    private static readonly TimeSpan ForwardCompletionTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);
    private const int MaxPendingRequests = 64;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _pipeName;
    private readonly object _dispatchLock = new();
    private long _lastDispatchedRequest;
    private int _disposed;
    private Task? _serverTask;
    private bool _ownsMutex;

    private SingleInstanceCoordinator(Mutex mutex, bool ownsMutex, string pipeName)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _pipeName = pipeName;
    }

    public bool IsPrimary => _ownsMutex;

    public static SingleInstanceCoordinator Create()
    {
        var identity = GetIdentityToken();
        var mutexName = $"Local\\Pixora.SingleInstance.{identity}";
        var pipeName = $"Pixora.SingleInstance.{identity}";
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        return new SingleInstanceCoordinator(mutex, createdNew, pipeName);
    }

    public void StartServer(Func<string?, Task> pathHandler)
    {
        ArgumentNullException.ThrowIfNull(pathHandler);
        if (!IsPrimary || _serverTask is not null)
        {
            return;
        }

        var cancellationToken = _cancellation.Token;
        _serverTask = Task.Run(() => RunServerAsync(pathHandler, cancellationToken));
    }

    public async Task<bool> TryForwardAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (IsPrimary)
        {
            return false;
        }

        var requestSent = false;
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(2_000, cancellationToken);
            using var completion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completion.CancelAfter(ForwardCompletionTimeout);
            await using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            // A failed write may still have delivered the request. Do not retry
            // in a local window unless the primary explicitly rejects it.
            requestSent = true;
            await writer.WriteLineAsync((path ?? string.Empty).AsMemory(), completion.Token);
            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            // Shell may remove a device's INetCache file when this process exits.
            // Keep it alive until the primary has finished consuming the path.
            var response = await reader.ReadLineAsync(completion.Token);
            // Older servers may close without an acknowledgement. Once delivered,
            // an unknown result must not open the same request in another window.
            return response != ForwardFailed;
        }
        catch
        {
            return requestSent;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
        if (_serverTask is { } serverTask)
        {
            _ = serverTask.ContinueWith(_ => _cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else _cancellation.Dispose();
    }

    private async Task RunServerAsync(Func<string?, Task> pathHandler, CancellationToken cancellationToken)
    {
        var requests = new HashSet<Task>();
        long sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                requests.RemoveWhere(task => task.IsCompleted);
                if (requests.Count >= MaxPendingRequests)
                {
                    await Task.WhenAny(requests).WaitAsync(cancellationToken);
                    continue;
                }

                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cancellationToken);
                    // Each connection owns its acknowledgement lifetime. A slow
                    // device request must not block newer requests reaching the UI.
                    requests.Add(HandleConnectionAsync(server, ++sequence, pathHandler, cancellationToken));
                    server = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ErrorLog.WriteException("SingleInstanceServer", "接收新窗口打开请求失败。", ex);
                    await Task.Delay(250, cancellationToken);
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Cancellation ends connection waits without synchronously waiting
            // for a UI handler, which may itself be waiting for the UI thread.
            await Task.WhenAll(requests);
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream server, long sequence, Func<string?, Task> pathHandler, CancellationToken cancellationToken)
    {
        await using var connection = server;
        using var completion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        completion.CancelAfter(ForwardCompletionTimeout);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(completion.Token);
        reading.CancelAfter(RequestReadTimeout);
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var path = await reader.ReadLineAsync(reading.Token);
            if (path is null)
            {
                return;
            }

            completion.Token.ThrowIfCancellationRequested();
            // Also isolate a handler that does synchronous work before returning
            // its Task. Observe failures even if the connection times out first.
            var requestToken = completion.Token;
            var handling = Task.Run(() => InvokeHandlerAsync(sequence, pathHandler, path, requestToken));
            var succeeded = await handling.WaitAsync(completion.Token);
            using var writer = new StreamWriter(server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync((succeeded ? ForwardSucceeded : ForwardFailed).AsMemory(), completion.Token);
        }
        catch (OperationCanceledException) when (completion.IsCancellationRequested || reading.IsCancellationRequested)
        {
            // Unknown completion closes without ERROR: do not cause a duplicate
            // local open while an uncancellable handler may still be consuming it.
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("SingleInstanceServer", "接收新窗口打开请求失败。", ex);
        }
    }

    private async Task<bool> InvokeHandlerAsync(
        long sequence, Func<string?, Task> pathHandler, string path, CancellationToken cancellationToken)
    {
        try
        {
            Task handling;
            lock (_dispatchLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A delayed sender or worker must not replace a newer selection.
                // The handler queues its UI work synchronously; only its completion is awaited outside the lock.
                if (sequence < _lastDispatchedRequest) return true;
                _lastDispatchedRequest = sequence;
                handling = pathHandler(string.IsNullOrWhiteSpace(path) ? null : path);
            }
            await handling;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("SingleInstanceServer", "处理新窗口打开请求失败。", ex);
            // A superseded failure must not launch an old selection in another window.
            lock (_dispatchLock) return sequence < _lastDispatchedRequest;
        }
    }

    private static string GetIdentityToken()
    {
        string identity;
        try
        {
            identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch
        {
            identity = Environment.UserName;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
