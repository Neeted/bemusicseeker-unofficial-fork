using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using BeMusicSeeker;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Tests;

internal static class TestResourceInitializer
{
    public static void EnsureJapaneseResources()
    {
        PropertyInfo availableCulturesProperty = typeof(App).GetProperty("AvailableCultures", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var availableCultures = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            { "Default(日本語)", "ja-JP" }
        });
        availableCulturesProperty.SetValue(null, availableCultures);
        Resources.Culture = CultureInfo.GetCultureInfo("ja-JP");
    }
}
