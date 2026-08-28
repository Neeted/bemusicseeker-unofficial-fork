using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Ribbit.Logging;

namespace BeMusicSeeker.Properties;

public sealed class PortableSettingsProvider : SettingsProvider, IApplicationSettingsProvider
{
    internal const string SettingsSectionName = "BeMusicSeeker.Properties.Settings";
    private const int LegacyMoviePlayerBit = 4;
    private const int BmsPlayerBit = 2;

    private static readonly HashSet<string> ObsoleteSettingNames = new(StringComparer.Ordinal)
    {
        "StandardColumnsSettings",
        "ZeroNoteColumnsSettings",
        "PlaylistColumnsSettings",
        "FullScanColumnsSettings",
        "DuplicateColumnsSettings",
        "EncodingColumnsSettings",
        "InstallColumnsSettings",
        "ChartInfoParseErrorColumnsSettings",
        "BmsonColumnSettingsMigrationVersion",
        "PublishVersion",
        "StartupExpandPlaylistTree",
        "UseFastSortInMainViewExperimental",
        "UseFastSortInDataGridExperimental",
        "UseDataGridColumnVirtualizationExperimental",
        "UseCustomTableView",
        "SkipEstimateOfflineScoreRanking",
        "UseEverythingForPendingPackageSourceScan",
        "SkipInitFileCheck",
        "UseExternalWebBrowser"
    };

    public override void Initialize(string name, NameValueCollection config)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = nameof(PortableSettingsProvider);
        }
        config ??= [];
        base.Initialize(name, config);
    }

    public override string ApplicationName
    {
        get
        {
            return AppDomain.CurrentDomain.FriendlyName;
        }
        set
        {
        }
    }

    public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection collection)
    {
        SettingsPropertyValueCollection settingsPropertyValueCollection = [];
        Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)> dictionary = LoadSettingMap();
        foreach (SettingsProperty item in collection)
        {
            var settingsPropertyValue = new SettingsPropertyValue(item);
            if (dictionary.TryGetValue(item.Name, out (SettingsSerializeAs serializeAs, string serializedValue) value))
            {
                settingsPropertyValue.SerializedValue = value.serializedValue;
            }
            else
            {
                settingsPropertyValue.SerializedValue = item.DefaultValue;
            }
            settingsPropertyValue.IsDirty = false;
            settingsPropertyValueCollection.Add(settingsPropertyValue);
        }
        return settingsPropertyValueCollection;
    }

    public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection collection)
    {
        try
        {
            Directory.CreateDirectory(PortableSettingsPath.ConfigDirectoryPath);
            if (File.Exists(PortableSettingsPath.UserConfigPath))
            {
                var fileInfo = new FileInfo(PortableSettingsPath.UserConfigPath);
                if (fileInfo.IsReadOnly)
                {
                    var ex = new UnauthorizedAccessException("Portable settings file is read-only.");
                    NLogWrapper.TraceLogger?.Error(ex, "portable_settings_save blocked_readonly path=" + PortableSettingsPath.UserConfigPath);
                    return;
                }
            }
            XDocument xDocument = LoadDocumentForUpdate();
            XElement xElement = xDocument.Root?.Element("userSettings")?.Element(SettingsSectionName);
            if (xElement == null)
            {
                return;
            }
            NormalizeSettingsSection(xElement);
            foreach (SettingsPropertyValue item in collection)
            {
                string serialized = GetSerializedValue(item);
                string value = item.Property.SerializeAs.ToString();
                XElement xElement2 = xElement.Elements("setting").FirstOrDefault(e => string.Equals((string)e.Attribute("name"), item.Name, StringComparison.Ordinal));
                if (xElement2 == null)
                {
                    xElement2 = new XElement("setting");
                    xElement2.SetAttributeValue("name", item.Name);
                    xElement.Add(xElement2);
                }
                xElement2.SetAttributeValue("serializeAs", value);
                XElement xElement3 = xElement2.Element("value");
                if (xElement3 == null)
                {
                    xElement3 = new XElement("value");
                    xElement2.Add(xElement3);
                }
                xElement3.RemoveNodes();
                if (item.Property.SerializeAs == SettingsSerializeAs.Xml)
                {
                    if (!TrySetXmlValue(xElement3, serialized))
                    {
                        xElement3.Value = serialized;
                    }
                }
                else
                {
                    xElement3.Value = serialized;
                }
            }
            SaveDocumentAtomically(xDocument, PortableSettingsPath.UserConfigPath);
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Error(ex, "portable_settings_save failed path=" + PortableSettingsPath.UserConfigPath);
        }
    }

    public SettingsPropertyValue GetPreviousVersion(SettingsContext context, SettingsProperty property)
    {
        return new SettingsPropertyValue(property);
    }

    public void Reset(SettingsContext context)
    {
        try
        {
            if (File.Exists(PortableSettingsPath.UserConfigPath))
            {
                File.Delete(PortableSettingsPath.UserConfigPath);
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "portable_settings_reset failed path=" + PortableSettingsPath.UserConfigPath);
        }
    }

    public void Upgrade(SettingsContext context, SettingsPropertyCollection properties)
    {
    }

    private static string GetSerializedValue(SettingsPropertyValue propertyValue)
    {
        object obj = propertyValue.SerializedValue ?? propertyValue.PropertyValue;
        return obj?.ToString() ?? string.Empty;
    }

    private static bool TrySetXmlValue(XElement valueElement, string serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return true;
        }
        try
        {
            string text = serialized;
            if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                int num = text.IndexOf("?>", StringComparison.OrdinalIgnoreCase);
                if (num > 0)
                {
                    text = text.Substring(num + 2);
                }
            }
            var xElement = XElement.Parse("<root>" + text + "</root>");
            valueElement.Add(xElement.Nodes());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)> LoadSettingMap()
    {
        var dictionary = new Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)>(StringComparer.Ordinal);
        if (!File.Exists(PortableSettingsPath.UserConfigPath))
        {
            return dictionary;
        }
        try
        {
            var xDocument = XDocument.Load(PortableSettingsPath.UserConfigPath);
            XElement xElement = xDocument.Root?.Element("userSettings")?.Element(SettingsSectionName);
            if (xElement == null)
            {
                return dictionary;
            }
            // The startup normalizer persists this migration when possible, but a read-only
            // config must still expose canonical values to the generated Settings wrapper.
            NormalizeSettingsSection(xElement);
            foreach (XElement item in xElement.Elements("setting"))
            {
                string attributeValue = (string)item.Attribute("name");
                if (string.IsNullOrWhiteSpace(attributeValue))
                {
                    continue;
                }
                string text = (string)item.Attribute("serializeAs");
                if (!Enum.TryParse<SettingsSerializeAs>(text, out SettingsSerializeAs result))
                {
                    result = SettingsSerializeAs.String;
                }
                XElement xElement2 = item.Element("value");
                string item2 = string.Empty;
                if (xElement2 != null)
                {
                    item2 = ((result == SettingsSerializeAs.Xml) ? string.Concat(xElement2.Nodes()) : xElement2.Value);
                }
                dictionary[attributeValue] = (result, item2);
            }
        }
        catch
        {
        }
        return dictionary;
    }

    private static XDocument LoadDocumentForUpdate()
    {
        if (File.Exists(PortableSettingsPath.UserConfigPath))
        {
            try
            {
                return XDocument.Load(PortableSettingsPath.UserConfigPath);
            }
            catch
            {
            }
        }
        var xDocument = new XDocument(new XElement("configuration", new XElement("userSettings", new XElement(SettingsSectionName))));
        return xDocument;
    }

    private static void SaveDocumentAtomically(XDocument document, string targetPath)
    {
        string directoryPath = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("The settings target must have a directory.", nameof(targetPath));
        string tempPath = Path.Combine(
            directoryPath,
            $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        bool replacementCompleted = false;
        try
        {
            document.Save(tempPath);
            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, null);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
            replacementCompleted = true;
        }
        finally
        {
            if (!replacementCompleted && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    NLogWrapper.TraceLogger?.Warn(
                        ex,
                        "portable_settings_temp_cleanup failed path=" + tempPath + " target=" + targetPath);
                }
            }
        }
    }

    internal static int RemoveObsoleteSettings(XElement settingsSection)
    {
        if (settingsSection == null)
        {
            return 0;
        }
        List<XElement> obsoleteSettings = [.. settingsSection.Elements("setting").Where(e => ObsoleteSettingNames.Contains((string)e.Attribute("name") ?? string.Empty))];
        foreach (XElement setting in obsoleteSettings)
        {
            setting.Remove();
        }
        return obsoleteSettings.Count;
    }

    /// <summary>
    /// Applies the persisted-settings migrations that must run before the generated settings
    /// wrapper materializes a value. The operation is idempotent so startup and save paths can
    /// share this owner without changing an already-normalized document.
    /// </summary>
    internal static int NormalizeSettingsSection(XElement settingsSection)
    {
        if (settingsSection == null)
        {
            return 0;
        }

        int normalizedSettings = RemoveObsoleteSettings(settingsSection);
        XElement playerPanelState = settingsSection.Elements("setting")
            .FirstOrDefault(e => string.Equals((string)e.Attribute("name"), "PlayerPanelState", StringComparison.Ordinal));
        XElement valueElement = playerPanelState?.Element("value");
        if (valueElement == null)
        {
            return normalizedSettings;
        }

        string normalizedValue = NormalizePlayerPanelStateValue(valueElement.Value);
        if (string.Equals(valueElement.Value, normalizedValue, StringComparison.Ordinal))
        {
            return normalizedSettings;
        }

        valueElement.Value = normalizedValue;
        return normalizedSettings + 1;
    }

    /// <summary>
    /// Normalizes an existing portable configuration before any generated Settings getter reads it.
    /// </summary>
    internal static int NormalizeCurrentPortableConfig()
    {
        if (!File.Exists(PortableSettingsPath.UserConfigPath))
        {
            return 0;
        }

        XDocument document = XDocument.Load(PortableSettingsPath.UserConfigPath);
        XElement settingsSection = document.Root?.Element("userSettings")?.Element(SettingsSectionName);
        int normalizedSettings = NormalizeSettingsSection(settingsSection);
        if (normalizedSettings > 0)
        {
            SaveDocumentAtomically(document, PortableSettingsPath.UserConfigPath);
        }

        return normalizedSettings;
    }

    private static string NormalizePlayerPanelStateValue(string value)
    {
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericValue)
            && (numericValue & LegacyMoviePlayerBit) != 0)
        {
            return ((numericValue & ~LegacyMoviePlayerBit) | BmsPlayerBit)
                .ToString(CultureInfo.InvariantCulture);
        }

        string[] symbolicValues = value.Split(',');
        bool changed = false;
        for (int index = 0; index < symbolicValues.Length; index++)
        {
            string token = symbolicValues[index].Trim();
            if (string.Equals(token, "MOVIE_PLAYER", StringComparison.OrdinalIgnoreCase))
            {
                int start = symbolicValues[index].IndexOf(token, StringComparison.Ordinal);
                symbolicValues[index] = start >= 0
                    ? symbolicValues[index].Remove(start, token.Length).Insert(start, "BMS_PLAYER")
                    : "BMS_PLAYER";
                changed = true;
            }
        }

        return changed ? string.Join(",", symbolicValues) : value;
    }

}
