using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pixora.Services;

public static class ThemeManager
{
    private const string ThemeDictionaryPrefix = "Themes/Theme.";
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            theme = AppTheme.Dark;
        }

        CurrentTheme = theme;
        if (Application.Current is not { } application)
        {
            return;
        }

        foreach (Window window in application.Windows)
        {
            UpdateTitleBar(window);
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var targetFile = $"{ThemeDictionaryPrefix}{theme}.xaml";
        var targetSource = new Uri(
            $"/{typeof(ThemeManager).Assembly.GetName().Name};component/{targetFile}",
            UriKind.Relative);

        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source is null
                || !source.Contains(ThemeDictionaryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (source.EndsWith(targetFile, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            dictionaries[index] = new ResourceDictionary
            {
                Source = targetSource,
            };
            return;
        }

        dictionaries.Insert(
            0,
            new ResourceDictionary
            {
                Source = targetSource,
            });
    }

    public static void ApplyTo(Window window)
    {
        if (window is null)
        {
            return;
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            UpdateTitleBar(window);
            return;
        }

        window.SourceInitialized += static (sender, _) =>
        {
            if (sender is Window initializedWindow)
            {
                UpdateTitleBar(initializedWindow);
            }
        };
    }

    private static void UpdateTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDarkTitleBar = CurrentTheme == AppTheme.Dark ? 1 : 0;
        try
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDarkTitleBar, sizeof(int));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // 标题栏颜色属于渐进增强，DWM 不可用时保持系统默认。
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
