using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlayerPanelStateSettingsCompatibilityTests
{
    [TestMethod]
    public void PlayerPanelState_PreservesPersistedKeyDefaultNamesAndValues()
    {
        Assert.AreEqual(0, (int)PlayerPanelState.TITLE_LARGE);
        Assert.AreEqual(1, (int)PlayerPanelState.TITLE_SMALL);
        Assert.AreEqual(2, (int)PlayerPanelState.BMS_PLAYER);

        SettingsProperty metadata = new Settings().Properties[nameof(Settings.PlayerPanelState)];
        Assert.IsNotNull(metadata);
        Assert.AreEqual("PlayerPanelState", metadata.Name);
        Assert.AreEqual("TITLE_SMALL", metadata.DefaultValue);

        using var files = new PortableSettingsPersistenceTests.SettingsFiles();
        string configPath = files.Path;
        AssertRoundTrips(configPath, "TITLE_LARGE", PlayerPanelState.TITLE_LARGE);
        AssertRoundTrips(configPath, "TITLE_SMALL", PlayerPanelState.TITLE_SMALL);
        AssertRoundTrips(configPath, "BMS_PLAYER", PlayerPanelState.BMS_PLAYER);
        AssertRoundTrips(
            configPath,
            "TITLE_SMALL, BMS_PLAYER",
            PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER);
    }
    [TestMethod]
    public void PlayerPanelState_AtomicNormalizationFailurePreservesOriginalAndMaterializesCanonicalValue()
    {
        using var files = new PortableSettingsPersistenceTests.SettingsFiles();
        string configPath = files.Path;
        CreateConfig("12").Save(configPath);
        byte[] legacyConfig = File.ReadAllBytes(configPath);

        Exception? saveFailure = null;
        using (var replaceBlocker = new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            try
            {
                PortableSettingsProvider.NormalizePortableConfig(configPath);
            }
            catch (Exception ex) when (ex is PortableSettingsException)
            {
                saveFailure = ex;
            }
        }

        Assert.IsNotNull(saveFailure, "The replace blocker must exercise the existing save failure contract.");
        CollectionAssert.AreEqual(legacyConfig, File.ReadAllBytes(configPath));
        Assert.AreEqual("12", GetPlayerPanelStateValue(XDocument.Load(configPath)));

        Settings loaded = PortableSettingsPersistenceTests.OpenSettings(configPath);
        loaded.Reload();
        Assert.AreEqual(
            (PlayerPanelState)10,
            loaded.PlayerPanelState,
            "The generated Settings wrapper must consume the in-memory normalized value even when persistence fails.");
    }
    private static string GetPlayerPanelStateValue(XDocument document)
    {
        return document.Root?
            .Element("userSettings")?
            .Element(PortableSettingsProvider.SettingsSectionName)?
            .Elements("setting")
            .Single(setting => string.Equals(
                (string?)setting.Attribute("name"),
                nameof(Settings.PlayerPanelState),
                StringComparison.Ordinal))
            .Element("value")?
            .Value ?? string.Empty;
    }

    private static void AssertRoundTrips(
        string configPath,
        string persistedValue,
        PlayerPanelState expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        CreateConfig(persistedValue).Save(configPath);

        Settings loaded = PortableSettingsPersistenceTests.OpenSettings(configPath);
        loaded.Reload();
        Assert.AreEqual(expected, loaded.PlayerPanelState);

        loaded.PlayerPanelState = expected == PlayerPanelState.TITLE_SMALL
            ? PlayerPanelState.TITLE_LARGE
            : PlayerPanelState.TITLE_SMALL;
        loaded.PlayerPanelState = expected;
        loaded.Save();
        Assert.AreEqual(
            persistedValue,
            XDocument.Load(configPath)
                .Root?
                .Element("userSettings")?
                .Element(PortableSettingsProvider.SettingsSectionName)?
                .Elements("setting")
                .Single(setting => string.Equals(
                    (string?)setting.Attribute("name"),
                    "PlayerPanelState",
                    StringComparison.Ordinal))
                .Element("value")?
                .Value);

        Settings reloaded = PortableSettingsPersistenceTests.OpenSettings(configPath);
        reloaded.Reload();
        Assert.AreEqual(expected, reloaded.PlayerPanelState);
    }

    private static XDocument CreateConfig(string persistedValue)
    {
        return new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "userSettings",
                    new XElement(
                        PortableSettingsProvider.SettingsSectionName,
                        new XElement(
                            "setting",
                            new XAttribute("name", "PlayerPanelState"),
                            new XAttribute("serializeAs", "String"),
                            new XElement("value", persistedValue))))));
    }
}
