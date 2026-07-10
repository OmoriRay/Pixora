using Pixora.Services;
using System.Windows;
using System.Windows.Threading;

namespace Pixora;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppInfo.EnsureLocalDataMigrated();
        FileAssociationService.TryRepairRegistrationForCurrentExecutable();

        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
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
