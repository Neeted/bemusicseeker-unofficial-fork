using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using BeMusicSeeker;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Tests;

internal static class TestResourceInitializer
{
    public static IDisposable UseJapaneseCulture()
    {
        return new CultureScope(CultureInfo.GetCultureInfo("ja-JP"));
    }

    public static void EnsureJapaneseResources()
    {
        PropertyInfo availableCulturesProperty = typeof(App).GetProperty("AvailableCultures", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var availableCultures = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            { "Default(日本語)", "ja-JP" }
        });
        availableCulturesProperty.SetValue(null, availableCultures);
        Resources.Culture = CultureInfo.GetCultureInfo("ja-JP");
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previousCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        internal CultureScope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
