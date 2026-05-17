using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PortableSettingsMigrationTests
{
    [TestMethod]
    public void RemoveObsoleteSettings_RemovesOnlyKnownDeprecatedUserSettings()
    {
        XElement section = CreateSettingsSection(
            ("StandardColumnsSettings", "old"),
            ("BmsonColumnSettingsMigrationVersion", "1"),
            ("PublishVersion", "1.0.0"),
            ("StartupExpandPlaylistTree", "True"),
            ("UseFastSortInDataGridExperimental", "True"),
            ("StandardCustomTableColumnSettings", "current"),
            ("UnknownFutureSetting", "keep"));

        int removed = PortableSettingsProvider.RemoveObsoleteSettings(section);

        Assert.AreEqual(5, removed);
        Assert.IsNull(FindSetting(section, "StandardColumnsSettings"));
        Assert.IsNull(FindSetting(section, "BmsonColumnSettingsMigrationVersion"));
        Assert.IsNull(FindSetting(section, "PublishVersion"));
        Assert.IsNull(FindSetting(section, "StartupExpandPlaylistTree"));
        Assert.IsNull(FindSetting(section, "UseFastSortInDataGridExperimental"));
        Assert.IsNotNull(FindSetting(section, "StandardCustomTableColumnSettings"));
        Assert.IsNotNull(FindSetting(section, "UnknownFutureSetting"));
    }

    [TestMethod]
    public void NormalizeMigratedConfig_RewritesLegacyUrlAndRemovesDeprecatedColumnSettings()
    {
        XDocument doc = CreateConfigDocument(
            ("TableListURL", Settings.LegacyTableListUrl),
            ("StandardColumnsSettings", "old"),
            ("InstallColumnsSettings", "old"),
            ("StandardCustomTableColumnSettings", "current"));

        int normalized = LegacyUserConfigMigrator.NormalizeMigratedConfig(doc);
        XElement section = GetSettingsSection(doc);

        Assert.AreEqual(3, normalized);
        Assert.AreEqual(Settings.DefaultTableListUrl, GetSettingValue(section, "TableListURL"));
        Assert.IsNull(FindSetting(section, "StandardColumnsSettings"));
        Assert.IsNull(FindSetting(section, "InstallColumnsSettings"));
        Assert.IsNotNull(FindSetting(section, "StandardCustomTableColumnSettings"));
    }

    [TestMethod]
    public void NormalizeMigratedConfig_MovesLegacyCultureMigrationToCopyTime()
    {
        XDocument doc = CreateConfigDocument(
            ("AssemblyVersion", "0.1.6654.30787"),
            ("Lang", "ja-JP"));

        int normalized = LegacyUserConfigMigrator.NormalizeMigratedConfig(
            doc,
            new HashSet<string>(StringComparer.Ordinal) { "ja-JP", "ko-KR" },
            "ko-KR");

        Assert.AreEqual(1, normalized);
        Assert.AreEqual("ko-KR", GetSettingValue(GetSettingsSection(doc), "Lang"));
    }

    [TestMethod]
    public void NormalizeMigratedConfig_LegacyCultureFallsBackToEnglishWhenCurrentCultureIsUnavailable()
    {
        XDocument doc = CreateConfigDocument(
            ("AssemblyVersion", "0.1.6654.30787"),
            ("Lang", "ja-JP"));

        int normalized = LegacyUserConfigMigrator.NormalizeMigratedConfig(
            doc,
            new HashSet<string>(StringComparer.Ordinal) { "ja-JP" },
            "fr-FR");

        Assert.AreEqual(1, normalized);
        Assert.AreEqual("en-US", GetSettingValue(GetSettingsSection(doc), "Lang"));
    }

    [TestMethod]
    public void NormalizeMigratedConfig_DoesNotRewriteCultureForNewerLegacyVersions()
    {
        XDocument doc = CreateConfigDocument(
            ("AssemblyVersion", "0.1.6654.30788"),
            ("Lang", "ja-JP"));

        int normalized = LegacyUserConfigMigrator.NormalizeMigratedConfig(
            doc,
            new HashSet<string>(StringComparer.Ordinal) { "ja-JP", "ko-KR" },
            "ko-KR");

        Assert.AreEqual(0, normalized);
        Assert.AreEqual("ja-JP", GetSettingValue(GetSettingsSection(doc), "Lang"));
    }

    [TestMethod]
    public void NormalizeMigratedConfig_InferLR2RootPathFromLegacySongDbAndConfigPaths()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            string lr2Root = Path.Combine(tempRoot, "LR2beta3");
            string configDirectory = Path.Combine(lr2Root, "LR2files", "Config");
            string databaseDirectory = Path.Combine(lr2Root, "LR2files", "Database");
            Directory.CreateDirectory(configDirectory);
            Directory.CreateDirectory(databaseDirectory);
            string configXmlPath = Path.Combine(configDirectory, "config.xml");
            string songDbPath = Path.Combine(databaseDirectory, "song.db");
            File.WriteAllText(configXmlPath, "<config />");
            File.WriteAllText(songDbPath, string.Empty);
            File.WriteAllText(Path.Combine(lr2Root, "LR2body.exe"), string.Empty);

            XDocument doc = CreateConfigDocument(
                ("OperationModeLR2DB", "True"),
                ("LR2RootPath", string.Empty),
                ("LR2ConfigXmlPath", configXmlPath),
                ("LR2SongDBPath", songDbPath));

            int normalized = LegacyUserConfigMigrator.NormalizeMigratedConfig(doc);

            Assert.AreEqual(1, normalized);
            Assert.AreEqual(lr2Root, GetSettingValue(GetSettingsSection(doc), "LR2RootPath"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static XDocument CreateConfigDocument(params (string Name, string Value)[] settings)
    {
        return new XDocument(new XElement("configuration", new XElement("userSettings", CreateSettingsSection(settings))));
    }

    private static XElement CreateSettingsSection(params (string Name, string Value)[] settings)
    {
        return new XElement(
            PortableSettingsProvider.SettingsSectionName,
            settings.Select((setting) =>
                new XElement(
                    "setting",
                    new XAttribute("name", setting.Name),
                    new XAttribute("serializeAs", "String"),
                    new XElement("value", setting.Value))));
    }

    private static XElement GetSettingsSection(XDocument doc)
    {
        return doc.Root.Element("userSettings").Element(PortableSettingsProvider.SettingsSectionName);
    }

    private static XElement FindSetting(XElement section, string name)
    {
        return section.Elements("setting")
            .FirstOrDefault((setting) => string.Equals((string)setting.Attribute("name"), name, StringComparison.Ordinal));
    }

    private static string GetSettingValue(XElement section, string name)
    {
        return FindSetting(section, name)?.Element("value")?.Value ?? string.Empty;
    }
}
