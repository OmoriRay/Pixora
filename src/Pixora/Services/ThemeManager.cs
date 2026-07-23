using System.Windows;

namespace Pixora.Services;

public static class ThemeManager
{
    private const string ThemeDictionaryPrefix = "Themes/Theme.";

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
}
