using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Pixora.Services;

public static class ErrorLog
{
    private const long MaximumLogBytes = 1L * 1024 * 1024;
    private static readonly object SyncRoot = new();
    private static readonly RollingTextFile LogFile = new(LogPath, MaximumLogBytes);
    private static bool _diagnosticsWritten;

    public static string LogPath =>
        Path.Combine(
            AppInfo.LocalDataFolder,
            "error.log");

    public static string LogFolder => Path.GetDirectoryName(LogPath) ?? string.Empty;

    public static void WriteMessage(string source, string message)
    {
        Write(source, message, null);
    }

    public static void WriteException(string source, Exception? exception)
    {
        Write(source, exception?.Message ?? "未知异常。", exception);
    }

    public static void WriteException(string source, string message, Exception exception)
    {
        Write(source, message, exception);
    }

    public static void Clear()
    {
        lock (SyncRoot)
        {
            LogFile.Clear();
            _diagnosticsWritten = false;
        }
    }

    public static string ReadRecent(int maximumCharacters = 12_000)
    {
        lock (SyncRoot)
        {
            return LogFile.ReadRecent(maximumCharacters);
        }
    }

    private static void Write(string source, string message, Exception? exception)
    {
        try
        {
            lock (SyncRoot)
            {
                if (!_diagnosticsWritten)
                {
                    LogFile.Append(CreateDiagnosticsBlock());
                    _diagnosticsWritten = true;
                }

                var content = exception is null
                    ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{message}\n\n"
                    : $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{message}\n{exception}\n\n";
                LogFile.Append(content);
            }
        }
        catch
        {
        }
    }

    private static string CreateDiagnosticsBlock()
    {
        var assembly = typeof(ErrorLog).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        var builder = new StringBuilder();
        builder.AppendLine($"==== {AppInfo.Name} Diagnostics ====");
        builder.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"AppVersion: {version}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"OSArchitecture: {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"EnvironmentVersion: {Environment.Version}");
        builder.AppendLine($"Is64BitProcess: {Environment.Is64BitProcess}");
        builder.AppendLine($"ProcessorCount: {Environment.ProcessorCount}");
        builder.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
        builder.AppendLine($"CurrentDirectory: {Environment.CurrentDirectory}");
        builder.AppendLine($"LogPath: {LogPath}");
        builder.AppendLine("================================");
        builder.AppendLine();
        return builder.ToString();
    }

}
