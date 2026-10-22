using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BeMusicSeeker.Models;

internal static class AppThemeService
{
    internal const string Light = "Light";
    internal const string Dark = "Dark";
    internal const string Sepia = "Sepia";
    internal const string Frost = "Frost";
    internal const string NeonViolet = "NeonViolet";
    internal const string GreenTerminal = "GreenTerminal";
    internal const string AmberTerminal = "AmberTerminal";
    internal const string OledBlack = "OledBlack";
    internal const string DeepSea = "DeepSea";
    internal const string DosBlue = "DosBlue";

    private const string ComponentThemeDictionaryPrefix = "/BeMusicSeeker;component/Themes/";
    private static readonly IReadOnlyList<string> themes = Array.AsReadOnly(new[]
    {
        Light, Dark, Sepia, Frost, NeonViolet, GreenTerminal, AmberTerminal, OledBlack, DeepSea, DosBlue
    });
    private static int version;

    internal static event EventHandler ThemeChanged;

    internal static int Version => version;

    /// <summary>設定値として保存するテーマ識別子を選択順に返します。</summary>
    internal static IReadOnlyList<string> AvailableThemes => themes;

    internal static string NormalizeTheme(string theme)
    {
        return themes.FirstOrDefault(candidate => string.Equals(candidate, theme, StringComparison.OrdinalIgnoreCase)) ?? Light;
    }

    internal static bool IsDarkTheme(string theme)
    {
        string normalizedTheme = NormalizeTheme(theme);
        return normalizedTheme is not (Light or Sepia or Frost);
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
        string targetSource = ComponentThemeDictionaryPrefix + normalizedTheme + ".xaml";
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

    /// <summary>
    /// component形式の登録済みテーマの完全な指定URIだけをテーマ辞書として判定します。
    /// 共通辞書はテーマ切替時にも維持します。
    /// </summary>
    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        string source = dictionary?.Source?.OriginalString;
        return themes.Any(theme => string.Equals(source, ComponentThemeDictionaryPrefix + theme + ".xaml", StringComparison.OrdinalIgnoreCase));
    }
}
