using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationSettingsMetadataTests
{
    private const string SettingsGroupName = "userSettings";

    private const string SettingsSectionName = "BeMusicSeeker.Properties.Settings";

    private static readonly string[] RuntimeOnlySettingNames =
    [
        "BeatorajaRootPath",
        "BeatorajaPlayerId",
        "RegisterBeatorajaBmtUrls",
        "PlaylistDefaultIgnoreFolderOutput",
        "AssemblyVersion",
        "StandardCustomTableColumnSettings",
        "UnregisteredCustomTableColumnSettings",
        "ZeroNoteCustomTableColumnSettings",
        "PlaylistCustomTableColumnSettings",
        "FullScanCustomTableColumnSettings",
        "DuplicateCustomTableColumnSettings",
        "EncodingCustomTableColumnSettings",
        "InstallCustomTableColumnSettings",
        "ChartInfoParseErrorCustomTableColumnSettings",
        "PlayHistoryCustomTableColumnSettings",
        "LR2BackupTarget",
        "CustomTableFontSize",
        "CustomTableRowHeight",
        "CustomTableHeaderHeight",
        "ScanBmsFilesOnStartup",
        "PlaylistSummaryColumnsSettings",
        "LangDisplayName",
        "KeepInstallablePackagesPending",
        "PendingInstallEstimateMaxParallelPackages",
        "AutoApplyAmbiguousInstallDestination",
        "DeletePendingPackageSourceAfterInstall",
        "EnableSmartComponentOverwrite",
        "KeepSmartOverwriteProtectedFilesByRenaming"
    ];

    [TestMethod]
    public void AppConfigCompatibilitySubsetMatchesRuntimeSettingsMetadataAndTypedDefaults()
    {
        XDocument config = LoadRepositoryAppConfig();
        XElement configSections = RequireElement(config.Root?.Element("configSections"), "configSections");
        XElement settingsGroupDefinition = RequireElement(
            configSections.Elements("sectionGroup")
                .SingleOrDefault(group => string.Equals((string?)group.Attribute("name"), SettingsGroupName, StringComparison.Ordinal)),
            "userSettings section group definition");
        Type settingsGroupType = ResolveConfigType(settingsGroupDefinition, "type");
        Assert.AreEqual(typeof(UserSettingsGroup), settingsGroupType, "Unexpected userSettings section group type.");

        XElement settingsSectionDefinition = RequireElement(
            settingsGroupDefinition.Elements("section")
                .SingleOrDefault(section => string.Equals((string?)section.Attribute("name"), SettingsSectionName, StringComparison.Ordinal)),
            "settings section definition");
        Type settingsSectionType = ResolveConfigType(settingsSectionDefinition, "type");
        Assert.AreEqual(typeof(ClientSettingsSection), settingsSectionType, "Unexpected application settings section type.");

        XElement settingsSection = GetSettingsSection(config);
        List<XElement> configSettings = settingsSection.Elements("setting").ToList();
        Assert.IsTrue(configSettings.Count > 0, "The compatibility subset must not be empty.");

        string?[] configNames = configSettings
            .Select(setting => (string?)setting.Attribute("name"))
            .ToArray();
        Assert.IsFalse(configNames.Any(string.IsNullOrWhiteSpace), "Every config setting must have a non-empty name.");
        string[] duplicateNames = configNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.AreEqual(0, duplicateNames.Length, "Config setting names must be unique: " + string.Join(", ", duplicateNames));

        Dictionary<string, SettingsProperty> runtimeProperties = GetRuntimeProperties();
        SettingsProviderAttribute? settingsProviderAttribute = typeof(Settings).GetCustomAttribute<SettingsProviderAttribute>();
        Assert.IsNotNull(settingsProviderAttribute, "Settings must declare its runtime settings provider.");
        Type? providerType = Type.GetType(settingsProviderAttribute!.ProviderTypeName, throwOnError: true);

        foreach (XElement configSetting in configSettings)
        {
            string? name = (string?)configSetting.Attribute("name");
            string validName = name!;
            Assert.IsTrue(runtimeProperties.TryGetValue(validName, out SettingsProperty? property), "Config setting has no runtime property: " + name);
            SettingsProperty runtimeProperty = property!;
            Assert.IsNotNull(runtimeProperty.Attributes[typeof(UserScopedSettingAttribute)], "Config setting is not user-scoped at runtime: " + name);
            Assert.IsNotNull(runtimeProperty.Provider, "Config setting has no runtime provider: " + name);
            SettingsProvider runtimeProvider = runtimeProperty.Provider!;
            Assert.AreEqual(providerType, runtimeProvider.GetType(), "Config setting provider mismatch: " + name);

            SettingsSerializeAs configSerializeAs = ParseSerializeAs(configSetting, validName);
            Assert.AreEqual(runtimeProperty.SerializeAs, configSerializeAs, "Config serialization mismatch: " + name);
            ConfigSettingValue value = ReadConfigValue(configSetting, configSerializeAs, validName);
            AssertConfigValueMatchesRuntimeDefault(runtimeProperty, value, validName);
        }

        string[] runtimeOnlyNames = runtimeProperties.Keys
            .Except(configNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!), StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEquivalent(RuntimeOnlySettingNames, runtimeOnlyNames, "Runtime-only settings must remain outside app.config.");
    }

    [TestMethod]
    public void RequiredSettingsHaveExpectedTypedRuntimeAndCompatibilityContracts()
    {
        Dictionary<string, SettingsProperty> runtimeProperties = GetRuntimeProperties();
        Dictionary<string, ConfigSettingValue> configValues = GetConfigValues();

        AssertConfigSettingTypedValue(runtimeProperties, configValues, nameof(Settings.TableListURL), new Uri(Settings.DefaultTableListUrl));
        AssertConfigSettingTypedValue(runtimeProperties, configValues, nameof(Settings.EstimateOfflineScoreRanking), false);
        AssertConfigSettingTypedValue(runtimeProperties, configValues, nameof(Settings.UpdateLr2IrRankingCacheOnStartup), false);
        AssertConfigSettingTypedValue(runtimeProperties, configValues, nameof(Settings.PlayHistoryDisplayTargetSetsJson), string.Empty);
        AssertConfigSettingTypedValue(runtimeProperties, configValues, nameof(Settings.PlayHistorySelectedDisplayTargetIdentity), string.Empty);
        AssertConfigSettingTypedValue(
            runtimeProperties,
            configValues,
            nameof(Settings.RightClickActionsJson),
            RightClickActionSettingsDefaults.SerializedJson);
        AssertRuntimeSettingTypedValue(runtimeProperties, nameof(Settings.ScanBmsFilesOnStartup), true);
        Assert.IsFalse(configValues.ContainsKey(nameof(Settings.ScanBmsFilesOnStartup)), "ScanBmsFilesOnStartup is runtime-only.");

        Assert.IsFalse(runtimeProperties.ContainsKey("SkipEstimateOfflineScoreRanking"));
        Assert.IsFalse(configValues.ContainsKey("SkipEstimateOfflineScoreRanking"));
        Assert.IsFalse(runtimeProperties.ContainsKey("SkipInitFileCheck"));
        Assert.IsFalse(configValues.ContainsKey("SkipInitFileCheck"));
    }

    private static void AssertRuntimeSettingTypedValue(
        IReadOnlyDictionary<string, SettingsProperty> runtimeProperties,
        string name,
        object expectedValue)
    {
        Assert.IsTrue(runtimeProperties.TryGetValue(name, out SettingsProperty? property), "Missing runtime setting: " + name);
        SettingsProperty runtimeProperty = property!;
        SettingsPropertyValue runtimeDefault = new(runtimeProperty);
        AssertTypedValueEqual(expectedValue, runtimeDefault.PropertyValue, "Unexpected runtime default: " + name);
    }

    private static void AssertConfigSettingTypedValue(
        IReadOnlyDictionary<string, SettingsProperty> runtimeProperties,
        IReadOnlyDictionary<string, ConfigSettingValue> configValues,
        string name,
        object expectedValue)
    {
        Assert.IsTrue(runtimeProperties.TryGetValue(name, out SettingsProperty? property), "Missing runtime setting: " + name);
        Assert.IsTrue(configValues.TryGetValue(name, out ConfigSettingValue? configValue), "Missing compatibility setting: " + name);

        SettingsProperty runtimeProperty = property!;
        ConfigSettingValue compatibilityValue = configValue!;
        SettingsPropertyValue runtimeDefault = new(runtimeProperty);
        object runtimeTypedValue = runtimeDefault.PropertyValue;
        AssertTypedValueEqual(expectedValue, runtimeTypedValue, "Unexpected runtime default: " + name);

        SettingsPropertyValue configPropertyValue = new(runtimeProperty);
        configPropertyValue.SerializedValue = compatibilityValue.SerializedValue;
        object configTypedValue = configPropertyValue.PropertyValue;
        if (configPropertyValue.UsingDefaultValue)
        {
            // Empty XML (or an empty string setting) intentionally delegates to the runtime default contract.
            Assert.IsTrue(
                IsEmptyDefaultPayload(runtimeProperty, compatibilityValue),
                "An explicit config value fell back to the runtime default: " + name);
            AssertTypedValueEqual(runtimeTypedValue, configTypedValue, "Default fallback mismatch: " + name);
            return;
        }

        AssertTypedValueEqual(expectedValue, configTypedValue, "Unexpected typed compatibility value: " + name);
    }

    private static void AssertConfigValueMatchesRuntimeDefault(SettingsProperty property, ConfigSettingValue configValue, string name)
    {
        SettingsPropertyValue runtimeDefault = new(property);
        object runtimeTypedValue = runtimeDefault.PropertyValue;

        SettingsPropertyValue configPropertyValue = new(property);
        configPropertyValue.SerializedValue = configValue.SerializedValue;
        object configTypedValue = configPropertyValue.PropertyValue;
        if (configPropertyValue.UsingDefaultValue)
        {
            // A default value is valid only for an empty serialized payload. Non-empty malformed values must not silently fall back.
            Assert.IsTrue(
                IsEmptyDefaultPayload(property, configValue),
                "Non-empty config value fell back to the runtime default: " + name);
            AssertTypedValueEqual(runtimeTypedValue, configTypedValue, "Config default fallback mismatch: " + name);
            return;
        }

        AssertTypedValueEqual(runtimeTypedValue, configTypedValue, "Typed config/runtime parity mismatch: " + name);
    }

    private static bool IsEmptyDefaultPayload(SettingsProperty property, ConfigSettingValue configValue)
    {
        if (configValue.SerializeAs == SettingsSerializeAs.Xml)
        {
            return string.IsNullOrWhiteSpace(configValue.SerializedValue);
        }

        return configValue.SerializeAs == SettingsSerializeAs.String
            && property.PropertyType == typeof(string)
            && configValue.SerializedValue.Length == 0;
    }

    private static void AssertTypedValueEqual(object expected, object actual, string message)
    {
        if (expected is null)
        {
            Assert.IsNull(actual, message);
            return;
        }

        Assert.IsNotNull(actual, message);
        if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
        {
            CollectionAssert.AreEqual(expectedBytes, actualBytes, message);
            return;
        }

        Assert.AreEqual(expected, actual, message);
    }

    private static Dictionary<string, SettingsProperty> GetRuntimeProperties()
    {
        Settings settings = new();
        return settings.Properties
            .Cast<SettingsProperty>()
            .ToDictionary(property => property.Name, StringComparer.Ordinal);
    }

    private static Dictionary<string, ConfigSettingValue> GetConfigValues()
    {
        XElement settingsSection = GetSettingsSection(LoadRepositoryAppConfig());
        Dictionary<string, ConfigSettingValue> values = new(StringComparer.Ordinal);
        foreach (XElement setting in settingsSection.Elements("setting"))
        {
            string? name = (string?)setting.Attribute("name");
            Assert.IsFalse(string.IsNullOrWhiteSpace(name), "Every config setting must have a non-empty name.");
            string validName = name!;
            Assert.IsTrue(values.TryAdd(validName, ReadConfigValue(setting, ParseSerializeAs(setting, validName), validName)), "Duplicate config setting: " + validName);
        }

        Assert.IsTrue(values.Count > 0, "The compatibility subset must not be empty.");
        return values;
    }

    private static SettingsSerializeAs ParseSerializeAs(XElement setting, string name)
    {
        string? serializedAs = (string?)setting.Attribute("serializeAs");
        Assert.IsTrue(
            Enum.TryParse(serializedAs, ignoreCase: false, out SettingsSerializeAs result),
            "Invalid serializeAs value for config setting: " + name);
        return result;
    }

    private static ConfigSettingValue ReadConfigValue(XElement setting, SettingsSerializeAs serializeAs, string name)
    {
        XElement value = RequireElement(setting.Element("value"), "value for config setting " + name);
        string serializedValue = serializeAs == SettingsSerializeAs.Xml
            ? string.Concat(value.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)))
            : value.Value;
        return new ConfigSettingValue(serializeAs, serializedValue);
    }

    private static XElement GetSettingsSection(XDocument config)
    {
        XElement settingsSection = RequireElement(
            config.Root?.Element(SettingsGroupName)?.Element(SettingsSectionName),
            SettingsGroupName + "." + SettingsSectionName);
        return settingsSection;
    }

    private static Type ResolveConfigType(XElement element, string attributeName)
    {
        string? typeName = (string?)element.Attribute(attributeName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(typeName), "Missing config type: " + attributeName);
        Type? resolvedType = Type.GetType(typeName, throwOnError: false);
        Assert.IsNotNull(resolvedType, "Unable to resolve config type: " + typeName);
        return resolvedType!;
    }

    private static XDocument LoadRepositoryAppConfig()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string path = Path.Combine(directory.FullName, "app.config");
            if (File.Exists(path))
            {
                return XDocument.Load(path, LoadOptions.PreserveWhitespace);
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate repository app.config from " + AppContext.BaseDirectory);
        return null!;
    }

    private static XElement RequireElement(XElement? element, string description)
    {
        Assert.IsNotNull(element, "Missing XML element: " + description);
        return element!;
    }

    private sealed class ConfigSettingValue
    {
        internal ConfigSettingValue(SettingsSerializeAs serializeAs, string serializedValue)
        {
            SerializeAs = serializeAs;
            SerializedValue = serializedValue;
        }

        internal SettingsSerializeAs SerializeAs { get; }

        internal string SerializedValue { get; }
    }
}
