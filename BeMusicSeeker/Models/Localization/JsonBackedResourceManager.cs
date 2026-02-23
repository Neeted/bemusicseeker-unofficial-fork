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
        if (cultureName != "ja-JP" && App.AvailableCultures.Values.Contains(cultureName))
        {
            if (JsonLanguageCatalog.TryGetString(cultureName, name, out var value))
            {
                return value;
            }
            return fallbackResourceManager.GetString(name, JaCulture);
        }
        return fallbackResourceManager.GetString(name, cultureInfo);
    }

    public override object GetObject(string name, CultureInfo culture)
    {
        return fallbackResourceManager.GetObject(name, culture ?? CultureInfo.CurrentUICulture);
    }
}
