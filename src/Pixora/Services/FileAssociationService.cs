using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pixora.Services;

public sealed record ExternalDefaultAssociationFailure(string Extension, int ExitCode, string Message);

public sealed record ExternalDefaultAssociationResult(
    int Total,
    int Succeeded,
    IReadOnlyList<ExternalDefaultAssociationFailure> Failures);

public static class FileAssociationService
{
    private const string ProgId = AppInfo.FileAssociationProgId;
    private const string AppName = AppInfo.Name;
    private const string SetUserFtaFileName = "SetUserFTA.exe";
    private const string FileTypeIconRelativePath = AppInfo.FileTypeIconRelativePath;
    private const string CapabilitiesPath = AppInfo.CapabilitiesPath;
    private const int ShellChangeNotifyAssociationChanged = 0x08000000;
    private const uint ShellChangeNotifyIdList = 0x0000;

    public static IReadOnlyList<string> SupportedExtensions => MediaFormatRegistry.SupportedStillImageExtensions;

    public static string ExpectedExternalDefaultToolPath =>
        Path.Combine(AppContext.BaseDirectory, "tools", SetUserFtaFileName);

    public static bool TryRepairRegistrationForCurrentExecutable()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return false;
            }

            var registeredCommand = GetRegisteredOpenCommand();
            if (string.IsNullOrWhiteSpace(registeredCommand)
                || !NeedsRegistrationRepair(registeredCommand, executablePath))
            {
                return false;
            }

            Register(executablePath);
            return true;
        }
        catch
        {
            // A stale association must never prevent the application itself from starting.
            return false;
        }
    }

    public static void Register(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new FileNotFoundException($"找不到 {AppInfo.Name} 程序文件。", executablePath);
        }

        using var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}");
        progIdKey?.SetValue(null, AppInfo.FileTypeDisplayName);
        using (var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
        {
            iconKey?.SetValue(null, GetFileTypeIconValue(executablePath));
        }

        using (var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        {
            commandKey?.SetValue(null, CreateOpenCommand(executablePath));
        }

        using (var applicationsCommandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{Path.GetFileName(executablePath)}\shell\open\command"))
        {
            applicationsCommandKey?.SetValue(null, CreateOpenCommand(executablePath));
        }

        using (var capabilitiesKey = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            capabilitiesKey?.SetValue("ApplicationName", AppName);
            capabilitiesKey?.SetValue("ApplicationDescription", AppInfo.Description);
        }

        using (var fileAssociationsKey = Registry.CurrentUser.CreateSubKey($@"{CapabilitiesPath}\FileAssociations"))
        {
            foreach (var extension in SupportedExtensions)
            {
                fileAssociationsKey?.SetValue(extension, ProgId);
            }
        }

        using (var registeredApplicationsKey = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registeredApplicationsKey?.SetValue(AppName, CapabilitiesPath);
        }

        foreach (var extension in SupportedExtensions)
        {
            using var openWithKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids");
            openWithKey?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyAssociationChanged();
    }

    public static bool TrySetDefaultAssociationsSilently(out string reason)
    {
        if (!SupportsSilentDefaultAssociations())
        {
            reason = "当前 Windows 不支持普通应用静默修改默认应用。";
            return false;
        }

        object? comObject = null;
        try
        {
            comObject = new ApplicationAssociationRegistration();
            var registration = (IApplicationAssociationRegistration)comObject;
            registration.SetAppAsDefaultAll(AppName);
            NotifyAssociationChanged();
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
    }

    public static bool TryGetExternalDefaultToolPath(out string toolPath)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "tools", SetUserFtaFileName),
            Path.Combine(baseDirectory, SetUserFtaFileName),
            Path.Combine(Environment.CurrentDirectory, "tools", SetUserFtaFileName),
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    toolPath = fullPath;
                    return true;
                }
            }
            catch
            {
            }
        }

        toolPath = Path.GetFullPath(candidates[0]);
        return false;
    }

    public static ExternalDefaultAssociationResult SetDefaultAssociationsWithExternalTool(string toolPath)
    {
        if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
        {
            throw new FileNotFoundException("找不到 SetUserFTA.exe。", toolPath);
        }

        var succeeded = 0;
        var failures = new List<ExternalDefaultAssociationFailure>();
        foreach (var extension in SupportedExtensions)
        {
            var result = RunSetUserFta(toolPath, extension);
            if (result.ExitCode == 0)
            {
                succeeded++;
                continue;
            }

            failures.Add(new ExternalDefaultAssociationFailure(extension, result.ExitCode, result.Message));
        }

        NotifyAssociationChanged();
        return new ExternalDefaultAssociationResult(SupportedExtensions.Count, succeeded, failures);
    }

    public static void OpenDefaultAppsSettings()
    {
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppName)}")
        {
            UseShellExecute = true,
        });
    }

    public static void Unregister()
    {
        foreach (var extension in SupportedExtensions)
        {
            using var openWithKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
            openWithKey?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        using (var registeredApplicationsKey = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
        {
            registeredApplicationsKey?.DeleteValue(AppName, throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        NotifyAssociationChanged();
    }

    private static (int ExitCode, string Message) RunSetUserFta(string toolPath, string extension)
    {
        var output = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(toolPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(extension);
        process.StartInfo.ArgumentList.Add(ProgId);
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                output.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(15_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return (-1, "执行超时。");
        }

        process.WaitForExit();
        return (process.ExitCode, output.ToString().Trim());
    }

    private static string? GetRegisteredOpenCommand()
    {
        using var commandKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        return commandKey?.GetValue(null) as string;
    }

    private static bool NeedsRegistrationRepair(string registeredCommand, string executablePath)
    {
        if (string.Equals(
            registeredCommand.Trim(),
            CreateOpenCommand(executablePath),
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var registeredExecutablePath = GetExecutablePathFromOpenCommand(registeredCommand);
        return string.IsNullOrWhiteSpace(registeredExecutablePath)
            || !File.Exists(registeredExecutablePath);
    }

    private static string? GetExecutablePathFromOpenCommand(string registeredCommand)
    {
        var command = registeredCommand.Trim();
        if (command.Length == 0)
        {
            return null;
        }

        if (command[0] == '"')
        {
            var closingQuoteIndex = command.IndexOf('"', 1);
            return closingQuoteIndex > 1
                ? command[1..closingQuoteIndex]
                : null;
        }

        var separatorIndex = command.IndexOfAny([' ', '\t']);
        return separatorIndex > 0
            ? command[..separatorIndex]
            : command;
    }

    private static string CreateOpenCommand(string executablePath)
    {
        return $"\"{executablePath}\" \"%1\"";
    }

    private static string GetFileTypeIconValue(string executablePath)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, FileTypeIconRelativePath);
        return File.Exists(iconPath)
            ? $"\"{iconPath}\",0"
            : $"\"{executablePath}\",0";
    }

    private static bool SupportsSilentDefaultAssociations()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(6, 0)
            && !OperatingSystem.IsWindowsVersionAtLeast(6, 2);
    }

    private static void NotifyAssociationChanged()
    {
        SHChangeNotify(ShellChangeNotifyAssociationChanged, ShellChangeNotifyIdList, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [ComImport]
    [Guid("591209C7-767B-42B2-9FBA-44EE4615F2C7")]
    private sealed class ApplicationAssociationRegistration
    {
    }

    [ComImport]
    [Guid("4E530B0A-E611-4C77-A3AC-9031D022281B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistration
    {
        void QueryCurrentDefault(
            [MarshalAs(UnmanagedType.LPWStr)] string query,
            AssociationType queryType,
            AssociationLevel queryLevel,
            out IntPtr association);

        void QueryAppIsDefault(
            [MarshalAs(UnmanagedType.LPWStr)] string query,
            AssociationType queryType,
            AssociationLevel queryLevel,
            [MarshalAs(UnmanagedType.LPWStr)] string appRegistryName,
            [MarshalAs(UnmanagedType.Bool)] out bool isDefault);

        void QueryAppIsDefaultAll(
            AssociationLevel queryLevel,
            [MarshalAs(UnmanagedType.LPWStr)] string appRegistryName,
            [MarshalAs(UnmanagedType.Bool)] out bool isDefault);

        void SetAppAsDefault(
            [MarshalAs(UnmanagedType.LPWStr)] string appRegistryName,
            [MarshalAs(UnmanagedType.LPWStr)] string set,
            AssociationType setType);

        void SetAppAsDefaultAll([MarshalAs(UnmanagedType.LPWStr)] string appRegistryName);

        void ClearUserAssociations();
    }

    private enum AssociationType
    {
        FileExtension = 0,
        UrlProtocol = 1,
        StartMenuClient = 2,
        MimeType = 3,
    }

    private enum AssociationLevel
    {
        Machine = 0,
        Effective = 1,
        User = 2,
    }
}
