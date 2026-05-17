using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.Properties;

internal static class LegacyUserConfigMigrator
{
    private static readonly SerializableVersion LegacyCultureMigrationVersion = new SerializableVersion(0, 1, 6654, 30787);

    public static void MigrateIfNeeded(ISet<string> availableCultures = null, string currentCultureName = null)
    {
        try
        {
            if (File.Exists(PortableSettingsPath.UserConfigPath))
            {
                return;
            }
            string legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeMusicSeeker");
            if (!Directory.Exists(legacyRoot))
            {
                NLogWrapper.TraceLogger?.Info("portable_settings_migration skip reason=no_legacy_root");
                return;
            }
            List<FileInfo> list = (from path in Directory.EnumerateFiles(legacyRoot, "user.config", SearchOption.AllDirectories)
                                   where IsLegacyConfigPath(path)
                                   select new FileInfo(path)).ToList();
            if (list.Count == 0)
            {
                NLogWrapper.TraceLogger?.Info("portable_settings_migration skip reason=no_legacy_file");
                return;
            }
            list = list.OrderByDescending((FileInfo f) => f.LastWriteTimeUtc).ThenBy((FileInfo f) => f.FullName, StringComparer.Ordinal).ToList();
            NLogWrapper.TraceLogger?.Info("portable_settings_migration candidates count=" + list.Count);
            FileInfo fileInfo = null;
            foreach (FileInfo item in list)
            {
                if (TryValidateCandidate(item, out var reason))
                {
                    fileInfo = item;
                    break;
                }
                NLogWrapper.TraceLogger?.Info("portable_settings_migration candidate_skip source=" + item.FullName + " reason=" + reason);
            }
            if (fileInfo == null)
            {
                NLogWrapper.TraceLogger?.Info("portable_settings_migration skip reason=no_valid_legacy_file");
                return;
            }
            Directory.CreateDirectory(PortableSettingsPath.ConfigDirectoryPath);
            XDocument migratedConfig = XDocument.Load(fileInfo.FullName, LoadOptions.None);
            int normalizedSettings = NormalizeMigratedConfig(migratedConfig, availableCultures, currentCultureName);
            migratedConfig.Save(PortableSettingsPath.UserConfigPath);
            NLogWrapper.TraceLogger?.Info("portable_settings_migration success source=" + fileInfo.FullName + " target=" + PortableSettingsPath.UserConfigPath + " sourceWriteUtc=" + fileInfo.LastWriteTimeUtc.ToString("o") + " normalizedSettings=" + normalizedSettings);
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "portable_settings_migration failed");
        }
    }

    private static bool TryValidateCandidate(FileInfo file, out string reason)
    {
        reason = "unknown";
        try
        {
            XDocument xDocument = XDocument.Load(file.FullName, LoadOptions.None);
            XElement xElement = GetSettingsSection(xDocument);
            if (xElement == null)
            {
                reason = "missing_settings_section";
                return false;
            }
            if (IsDefaultEquivalent(xDocument, out reason))
            {
                return false;
            }
            reason = string.Empty;
            return true;
        }
        catch
        {
            reason = "invalid_xml";
            return false;
        }
    }

    private static bool IsDefaultEquivalent(XDocument doc, out string reason)
    {
        string settingValue = GetSettingValue(doc, "AssemblyVersion");
        if (string.IsNullOrWhiteSpace(settingValue))
        {
            reason = "default_equivalent:missing_AssemblyVersion";
            return true;
        }
        settingValue = GetSettingValue(doc, "TableListURL");
        if (string.IsNullOrWhiteSpace(settingValue))
        {
            reason = "default_equivalent:missing_TableListURL";
            return true;
        }
        settingValue = GetSettingValue(doc, "BMSInstallDir");
        if (string.IsNullOrWhiteSpace(settingValue))
        {
            reason = "default_equivalent:missing_BMSInstallDir";
            return true;
        }
        bool flag = string.Equals(GetSettingValue(doc, "OperationModeLR2DB"), "True", StringComparison.OrdinalIgnoreCase);
        if (flag)
        {
            string settingValue2 = GetSettingValue(doc, "LR2SongDBPath");
            if (string.IsNullOrWhiteSpace(settingValue2))
            {
                reason = "default_equivalent:missing_LR2SongDBPath";
                return true;
            }
            settingValue2 = GetSettingValue(doc, "LR2ConfigXmlPath");
            if (string.IsNullOrWhiteSpace(settingValue2))
            {
                reason = "default_equivalent:missing_LR2ConfigXmlPath";
                return true;
            }
        }
        else
        {
            string settingValue3 = GetSettingValue(doc, "BMSRootPath");
            if (string.IsNullOrWhiteSpace(settingValue3))
            {
                reason = "default_equivalent:missing_BMSRootPath";
                return true;
            }
        }
        reason = string.Empty;
        return false;
    }

    private static string GetSettingValue(XDocument doc, string name)
    {
        IEnumerable<XElement> source = GetSettingsSection(doc)?.Elements("setting");
        if (source == null)
        {
            return string.Empty;
        }
        XElement xElement = source.FirstOrDefault((XElement e) => string.Equals((string)e.Attribute("name"), name, StringComparison.Ordinal));
        if (xElement == null)
        {
            return string.Empty;
        }
        return xElement.Element("value")?.Value?.Trim() ?? string.Empty;
    }

    internal static int NormalizeMigratedConfig(XDocument doc, ISet<string> availableCultures = null, string currentCultureName = null)
    {
        XElement settingsSection = GetSettingsSection(doc);
        if (settingsSection == null)
        {
            return 0;
        }

        int normalizedSettings = PortableSettingsProvider.RemoveObsoleteSettings(settingsSection);
        if (string.Equals(GetSettingValue(doc, "TableListURL"), Settings.LegacyTableListUrl, StringComparison.OrdinalIgnoreCase))
        {
            normalizedSettings += SetSettingValue(settingsSection, "TableListURL", Settings.DefaultTableListUrl);
        }
        if (TryInferLR2RootPath(doc, out string lr2RootPath))
        {
            normalizedSettings += SetSettingValue(settingsSection, "LR2RootPath", lr2RootPath);
        }
        if (TryGetLegacyVersion(doc, out SerializableVersion version) && version <= LegacyCultureMigrationVersion)
        {
            string cultureName = ChooseLegacyCulture(availableCultures, currentCultureName);
            normalizedSettings += SetSettingValue(settingsSection, "Lang", cultureName);
        }
        return normalizedSettings;
    }

    private static XElement GetSettingsSection(XDocument doc)
    {
        return doc.Root?.Element("userSettings")?.Element(PortableSettingsProvider.SettingsSectionName);
    }

    private static int SetSettingValue(XElement settingsSection, string name, string value)
    {
        XElement setting = settingsSection.Elements("setting")
            .FirstOrDefault((XElement e) => string.Equals((string)e.Attribute("name"), name, StringComparison.Ordinal));
        if (setting == null)
        {
            setting = new XElement("setting");
            setting.SetAttributeValue("name", name);
            settingsSection.Add(setting);
        }
        setting.SetAttributeValue("serializeAs", "String");
        XElement valueElement = setting.Element("value");
        if (valueElement == null)
        {
            valueElement = new XElement("value");
            setting.Add(valueElement);
        }
        if (string.Equals(valueElement.Value, value, StringComparison.Ordinal))
        {
            return 0;
        }
        valueElement.Value = value;
        return 1;
    }

    private static bool TryInferLR2RootPath(XDocument doc, out string lr2RootPath)
    {
        lr2RootPath = null;
        try
        {
            if (!string.Equals(GetSettingValue(doc, "OperationModeLR2DB"), "True", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(GetSettingValue(doc, "LR2RootPath")))
            {
                return false;
            }
            string configXmlPath = GetSettingValue(doc, "LR2ConfigXmlPath");
            string songDbPath = GetSettingValue(doc, "LR2SongDBPath");
            if (string.IsNullOrWhiteSpace(configXmlPath) || !File.Exists(configXmlPath) || string.IsNullOrWhiteSpace(songDbPath) || !File.Exists(songDbPath))
            {
                return false;
            }
            string configRoot = GetGrandparentDirectory(configXmlPath);
            string songDbRoot = GetGrandparentDirectory(songDbPath);
            if (string.IsNullOrWhiteSpace(configRoot) || !string.Equals(configRoot, songDbRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(configRoot))
            {
                return false;
            }
            if (!File.Exists(Path.Combine(configRoot, "LR2body.exe")) && !File.Exists(Path.Combine(configRoot, "LRHbody.exe")))
            {
                return false;
            }
            lr2RootPath = configRoot;
            return true;
        }
        catch
        {
            lr2RootPath = null;
            return false;
        }
    }

    private static bool TryGetLegacyVersion(XDocument doc, out SerializableVersion version)
    {
        version = null;
        try
        {
            string value = GetSettingValue(doc, "AssemblyVersion");
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            version = new SerializableVersion(value);
            return true;
        }
        catch
        {
            version = null;
            return false;
        }
    }

    private static string ChooseLegacyCulture(ISet<string> availableCultures, string currentCultureName)
    {
        string cultureName = string.IsNullOrWhiteSpace(currentCultureName) ? CultureInfo.CurrentCulture.Name : currentCultureName;
        if (availableCultures != null && availableCultures.Contains(cultureName))
        {
            return cultureName;
        }
        return "en-US";
    }

    private static string GetGrandparentDirectory(string path)
    {
        string first = Path.GetDirectoryName(path);
        string second = string.IsNullOrEmpty(first) ? null : Path.GetDirectoryName(first);
        return string.IsNullOrEmpty(second) ? null : Path.GetDirectoryName(second);
    }

    private static bool IsLegacyConfigPath(string path)
    {
        try
        {
            string directoryName = Path.GetDirectoryName(path) ?? string.Empty;
            DirectoryInfo directoryInfo = new DirectoryInfo(directoryName);
            DirectoryInfo directoryInfo2 = directoryInfo.Parent;
            if (directoryInfo2 == null || directoryInfo2.Parent == null)
            {
                return false;
            }
            return directoryInfo2.Name.StartsWith("BeMusicSeeker.exe_Url_", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
