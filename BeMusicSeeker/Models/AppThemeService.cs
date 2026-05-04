using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BeMusicSeeker.Models;

internal static class AppThemeService
{
    internal const string Light = "Light";
    internal const string Dark = "Dark";

    private const string ThemeDictionaryPrefix = "/Themes/";
    private static int version;

    internal static event EventHandler ThemeChanged;

    internal static int Version => version;

    internal static string NormalizeTheme(string theme)
    {
        return string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;
    }

    internal static bool IsDarkTheme(string theme)
    {
        return string.Equals(NormalizeTheme(theme), Dark, StringComparison.Ordinal);
    }

    internal static void ApplyTheme(string theme)
    {
        string normalizedTheme = NormalizeTheme(theme);
        Application application = Application.Current;
        if (application == null)
        {
            return;
        }
        Collection<ResourceDictionary> dictionaries = application.Resources.MergedDictionaries;
        string targetSource = ThemeDictionaryPrefix + normalizedTheme + ".xaml";
        ResourceDictionary current = dictionaries.FirstOrDefault(IsThemeDictionary);
        if (current != null && string.Equals(current.Source?.OriginalString, targetSource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (IsThemeDictionary(dictionaries[i]))
            {
                dictionaries.RemoveAt(i);
            }
        }
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(targetSource, UriKind.Relative)
        });
        version++;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    internal static Brush FindBrush(string resourceKey, Brush fallback)
    {
        object candidate = Application.Current?.TryFindResource(resourceKey);
        return candidate as Brush ?? fallback;
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        string source = dictionary?.Source?.OriginalString;
        return !string.IsNullOrEmpty(source)
            && source.StartsWith(ThemeDictionaryPrefix, StringComparison.OrdinalIgnoreCase)
            && source.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
    }
}
