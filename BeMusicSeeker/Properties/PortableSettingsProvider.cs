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

/// <summary>Reads and atomically persists the portable user settings document without presenting UI.</summary>
public sealed class PortableSettingsProvider : SettingsProvider, IApplicationSettingsProvider
{
    private readonly string settingsPath;

    /// <summary>Uses the application-owned portable configuration path.</summary>
    public PortableSettingsProvider() : this(PortableSettingsPath.UserConfigPath) { }

    /// <summary>Uses an explicit configuration path with the same persistence contract as application settings.</summary>
    internal PortableSettingsProvider(string settingsPath)
    {
        this.settingsPath = Path.GetFullPath(settingsPath ?? throw new ArgumentNullException(nameof(settingsPath)));
    }

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

    /// <inheritdoc/>
    public override void Initialize(string name, NameValueCollection config)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = nameof(PortableSettingsProvider);
        }
        config ??= [];
        base.Initialize(name, config);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection collection)
    {
        SettingsPropertyValueCollection values = [];
        Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)> settingsMap = LoadSettingMap();
        foreach (SettingsProperty item in collection)
        {
            var propertyValue = new SettingsPropertyValue(item);
            if (settingsMap.TryGetValue(item.Name, out (SettingsSerializeAs serializeAs, string serializedValue) value))
            {
                propertyValue.SerializedValue = value.serializedValue;
            }
            else
            {
                propertyValue.SerializedValue = item.DefaultValue;
            }
            propertyValue.IsDirty = false;
            values.Add(propertyValue);
        }
        return values;
    }

    /// <summary>Persists all supplied values or throws with the path and original cause; failed publication preserves the target.</summary>
    public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection collection)
    {
        try
        {
            XDocument document = ReadDocument(settingsPath) ?? CreateEmptyDocument();
            XElement settingsSection = GetRequiredSettingsSection(document);
            NormalizeSettingsSection(settingsSection);
            foreach (SettingsPropertyValue item in collection)
            {
                string serialized = GetSerializedValue(item);
                string value = item.Property.SerializeAs.ToString();
                XElement settingElement = settingsSection.Elements("setting").FirstOrDefault(e => string.Equals((string)e.Attribute("name"), item.Name, StringComparison.Ordinal));
                if (settingElement == null)
                {
                    settingElement = new XElement("setting");
                    settingElement.SetAttributeValue("name", item.Name);
                    settingsSection.Add(settingElement);
                }
                settingElement.SetAttributeValue("serializeAs", value);
                XElement valueElement = settingElement.Element("value");
                if (valueElement == null)
                {
                    valueElement = new XElement("value");
                    settingElement.Add(valueElement);
                }
                valueElement.RemoveNodes();
                if (item.Property.SerializeAs == SettingsSerializeAs.Xml)
                {
                    if (!TrySetXmlValue(valueElement, serialized))
                    {
                        valueElement.Value = serialized;
                    }
                }
                else
                {
                    valueElement.Value = serialized;
                }
            }
            SaveDocument(document, settingsPath);
        }
        catch (Exception ex)
        {
            if (ex is PortableSettingsException) throw;
            throw new PortableSettingsException(settingsPath, "Save", ex);
        }
    }

    /// <inheritdoc/>
    public SettingsPropertyValue GetPreviousVersion(SettingsContext context, SettingsProperty property)
    {
        return new SettingsPropertyValue(property);
    }

    /// <inheritdoc/>
    public void Reset(SettingsContext context)
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "portable_settings_reset failed path=" + settingsPath);
        }
    }

    /// <inheritdoc/>
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

    private Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)> LoadSettingMap()
    {
        var map = new Dictionary<string, (SettingsSerializeAs, string)>(StringComparer.Ordinal);
        XDocument document = ReadDocument(settingsPath);
        if (document == null) return map;
        XElement section = GetRequiredSettingsSection(document);
        // Read-only files still materialize compatible values even when normalization cannot be saved.
        NormalizeSettingsSection(section);
        foreach (XElement setting in section.Elements("setting"))
        {
            string name = (string)setting.Attribute("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!Enum.TryParse((string)setting.Attribute("serializeAs"), out SettingsSerializeAs format))
                format = SettingsSerializeAs.String;
            XElement value = setting.Element("value");
            map[name] = (format, value == null ? string.Empty :
                format == SettingsSerializeAs.Xml ? string.Concat(value.Nodes()) : value.Value);
        }
        return map;
    }

    /// <summary>Returns null only for a missing file; unreadable, malformed and structurally invalid documents fail explicitly.</summary>
    internal static XDocument ReadDocument(string path)
    {
        try
        {
            XDocument document;
            try { document = XDocument.Load(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            GetRequiredSettingsSection(document);
            return document;
        }
        catch (Exception exception)
        {
            throw new PortableSettingsException(path, "Read", exception);
        }
    }

    /// <summary>Creates the required structure with no user values, allowing first-run defaults.</summary>
    internal static XDocument CreateEmptyDocument() => new(new XElement("configuration",
        new XElement("userSettings", new XElement(SettingsSectionName))));

    private static XElement GetRequiredSettingsSection(XDocument document)
    {
        XElement section = document.Root?.Name == "configuration"
            ? document.Root.Element("userSettings")?.Element(SettingsSectionName) : null;
        return section ?? throw new ConfigurationErrorsException("Portable settings document is missing its required configuration/userSettings/application section.");
    }

    /// <summary>Publishes a document atomically and identifies persistence failures separately from reads.</summary>
    internal static void SaveDocument(XDocument document, string path, string operation = "Save")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            SaveDocumentAtomically(document, path, replaceExisting: operation is not ("Create" or "Migrate"));
        }
        catch (Exception exception)
        {
            throw new PortableSettingsException(path, operation, exception);
        }
    }

    private static void SaveDocumentAtomically(XDocument document, string targetPath, bool replaceExisting)
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
            if (replaceExisting && File.Exists(targetPath))
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
    internal static int NormalizeCurrentPortableConfig() => NormalizePortableConfig(PortableSettingsPath.UserConfigPath);

    /// <summary>Validates and normalizes an explicitly owned settings file before materialization.</summary>
    internal static int NormalizePortableConfig(string path)
    {
        XDocument document = ReadDocument(path);
        if (document == null) return 0;
        int changed = NormalizeSettingsSection(GetRequiredSettingsSection(document));
        if (changed > 0) SaveDocument(document, path);
        return changed;
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
