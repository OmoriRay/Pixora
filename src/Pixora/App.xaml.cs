using Pixora.Services;
using System.Windows;
using System.Windows.Threading;

namespace Pixora;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstanceCoordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        AppInfo.EnsureLocalDataMigrated();
        FileAssociationService.TryRepairRegistrationForCurrentExecutable();

        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        var forceNewWindow = e.Args.Any(static arg => string.Equals(arg, "--new-window", StringComparison.OrdinalIgnoreCase));
        var startupPath = e.Args.FirstOrDefault(static arg =>
            !string.IsNullOrWhiteSpace(arg)
            && !string.Equals(arg, "--new-window", StringComparison.OrdinalIgnoreCase));
        var settings = ViewerSettings.Load();
        ThemeManager.Apply(settings.Theme);
        if (settings.ReuseExistingWindow && !forceNewWindow)
        {
            try
            {
                _singleInstanceCoordinator = SingleInstanceCoordinator.Create();
                if (!_singleInstanceCoordinator.IsPrimary)
                {
                    if (await _singleInstanceCoordinator.TryForwardAsync(startupPath))
                    {
                        Shutdown();
                        return;
                    }

                    _singleInstanceCoordinator.Dispose();
                    _singleInstanceCoordinator = null;
                }
                else
                {
                    _singleInstanceCoordinator.StartServer(HandleForwardedPathAsync);
                }
            }
            catch (Exception ex)
            {
                _singleInstanceCoordinator?.Dispose();
                _singleInstanceCoordinator = null;
                ErrorLog.WriteException("InitializeSingleInstance", "初始化单实例复用失败，已改为独立窗口启动。", ex);
            }
        }

        var window = new MainWindow(startupPath);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceCoordinator?.Dispose();
        base.OnExit(e);
    }

    private Task HandleForwardedPathAsync(string? path)
    {
        return Dispatcher.InvokeAsync(async () =>
        {
            if (MainWindow is not MainWindow window)
            {
                return;
            }

            window.ActivateFromExternalRequest();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await window.OpenExternalPathAsync(path);
            }
        }).Task.Unwrap();
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.WriteException("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"{AppInfo.Name} 遇到异常：\n{FriendlyException(e.Exception)}\n\n日志已写入：\n{ErrorLog.LogPath}",
            AppInfo.Name,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ErrorLog.WriteException("UnhandledException", e.ExceptionObject as Exception);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ErrorLog.WriteException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static string FriendlyException(Exception exception)
    {
        return string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
    }

}
