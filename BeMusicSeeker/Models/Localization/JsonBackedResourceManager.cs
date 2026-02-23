using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;

namespace BeMusicSeeker.Models.Localization;

public sealed class JsonBackedResourceManager : ResourceManager
{
    private static readonly CultureInfo JaCulture = CultureInfo.GetCultureInfo("ja-JP");

    private readonly ResourceManager fallbackResourceManager;

    public JsonBackedResourceManager(string baseName, Assembly assembly)
        : base(baseName, assembly)
    {
        fallbackResourceManager = new ResourceManager(baseName, assembly);
    }

    public override string GetString(string name, CultureInfo culture)
    {
        CultureInfo cultureInfo = culture ?? CultureInfo.CurrentUICulture;
        string cultureName = cultureInfo.Name;

        // First try to resolve from JSON languages (including user-customized ja-JP.json)
        if (App.AvailableCultures.Values.Contains(cultureName))
        {
            if (JsonLanguageCatalog.TryGetString(cultureName, name, out var value))
            {
                return value;
            }
        }

        // Fallback 1: Direct resx match for the requested culture (if available)
        // Fallback 2: Fallback to ja-JP inside resx (default embedded language)
        try
        {
            string resxValue = fallbackResourceManager.GetString(name, cultureInfo);
            if (!string.IsNullOrEmpty(resxValue))
            {
                return resxValue;
            }
        }
        catch { }

        return fallbackResourceManager.GetString(name, JaCulture);
    }

    public override object GetObject(string name, CultureInfo culture)
    {
        return fallbackResourceManager.GetObject(name, culture ?? CultureInfo.CurrentUICulture);
    }
}
