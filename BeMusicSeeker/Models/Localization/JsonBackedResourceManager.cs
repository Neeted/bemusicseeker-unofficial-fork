using System.Globalization;
using System.Reflection;
using System.Resources;

namespace BeMusicSeeker.Models.Localization;

/// <summary>指定カルチャのJSON、埋込み資源、日本語埋込み資源の順で文字列を解決します。</summary>
public sealed class JsonBackedResourceManager(string baseName, Assembly assembly) : ResourceManager(baseName, assembly)
{
    private static readonly CultureInfo JaCulture = CultureInfo.GetCultureInfo("ja-JP");

    private readonly ResourceManager fallbackResourceManager = new(baseName, assembly);

    /// <summary>JSONを優先し、埋込み資源の欠落だけを日本語fallbackへ渡します。内部故障は隠しません。</summary>
    public override string GetString(string name, CultureInfo culture)
    {
        CultureInfo cultureInfo = culture ?? CultureInfo.CurrentUICulture;
        string cultureName = cultureInfo.Name;

        // First try to resolve from JSON languages (including user-customized ja-JP.json)
        if (JsonLanguageCatalog.ContainsCulture(cultureName))
        {
            if (JsonLanguageCatalog.TryGetString(cultureName, name, out string value))
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
        catch (MissingManifestResourceException)
        {
            // 指定カルチャの埋込み資源がないときだけ日本語へ進みます。
        }
        catch (MissingSatelliteAssemblyException)
        {
            // fallback用の資源欠落も、下の最終取得で呼出元へ伝えます。
        }

        return fallbackResourceManager.GetString(name, JaCulture);
    }

    /// <summary>文字列以外は既存の埋込み資源管理器へ委ねます。</summary>
    public override object GetObject(string name, CultureInfo culture)
    {
        return fallbackResourceManager.GetObject(name, culture ?? CultureInfo.CurrentUICulture);
    }
}
