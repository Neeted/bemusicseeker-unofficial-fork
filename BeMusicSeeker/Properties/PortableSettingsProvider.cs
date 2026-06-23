using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Ribbit.Logging;

namespace BeMusicSeeker.Properties;

public sealed class PortableSettingsProvider : SettingsProvider, IApplicationSettingsProvider
{
    internal const string SettingsSectionName = "BeMusicSeeker.Properties.Settings";

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
        "SkipInitFileCheck"
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
            RemoveObsoleteSettings(xElement);
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
            xDocument.Save(PortableSettingsPath.UserConfigPath);
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

}
