using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

internal sealed class ApplicationSettingsInitializationResult
{
    internal ApplicationSettingsInitializationResult(bool firstStartup, string appearanceTheme, CultureInfo culture)
    {
        FirstStartup = firstStartup;
        AppearanceTheme = appearanceTheme;
        Culture = culture;
    }

    internal bool FirstStartup { get; }

    internal string AppearanceTheme { get; }

    internal CultureInfo Culture { get; }
}

/// <summary>
/// 起動時の legacy migration、設定 version upgrade、言語 / theme 正規化を一つの lifecycle として実行します。
/// </summary>
internal sealed class ApplicationSettingsLifecycle
{
    private readonly Action<ISet<string>, string> legacyMigration;

    private readonly Action upgradeSettings;

    private readonly Action saveSettings;

    private readonly IApplicationSettingsStore settingsStore;

    private readonly Action normalizeSettings;

    /// <summary>
    /// Creates the startup settings lifecycle, including the pre-materialization normalization seam.
    /// </summary>
    /// <param name="legacyMigration">Migrates legacy user configuration when required.</param>
    /// <param name="upgradeSettings">Upgrades the settings store to the current version.</param>
    /// <param name="saveSettings">Persists settings after a version upgrade.</param>
    /// <param name="settingsStore">Store used for startup version, language, and theme values.</param>
    /// <param name="normalizeSettings">Normalizes persisted settings before the first store read.</param>
    internal ApplicationSettingsLifecycle(
        Action<ISet<string>, string> legacyMigration = null,
        Action upgradeSettings = null,
        Action saveSettings = null,
        IApplicationSettingsStore settingsStore = null,
        Action normalizeSettings = null)
    {
        this.legacyMigration = legacyMigration ?? LegacyUserConfigMigrator.MigrateIfNeeded;
        this.settingsStore = settingsStore ?? new SettingsApplicationSettingsStore();
        this.upgradeSettings = upgradeSettings ?? this.settingsStore.Upgrade;
        this.saveSettings = saveSettings ?? this.settingsStore.Save;
        this.normalizeSettings = normalizeSettings ?? (() => PortableSettingsProvider.NormalizeCurrentPortableConfig());
    }

    /// <summary>
    /// Normalizes persisted settings before reading the store, then runs version and culture startup handling.
    /// </summary>
    /// <param name="availableCultureNames">Culture names supported by the application.</param>
    /// <param name="serializableVersionFactory">Factory for the current application version.</param>
    /// <param name="firstStartupObserver">Optional observer notified after first-startup detection.</param>
    internal ApplicationSettingsInitializationResult Initialize(
        IEnumerable<string> availableCultureNames,
        Func<SerializableVersion> serializableVersionFactory,
        Action<bool> firstStartupObserver = null)
    {
        if (availableCultureNames == null)
        {
            throw new ArgumentNullException(nameof(availableCultureNames));
        }
        if (serializableVersionFactory == null)
        {
            throw new ArgumentNullException(nameof(serializableVersionFactory));
        }

        HashSet<string> availableCultures = new(availableCultureNames.Where(value => value != null));
        bool firstStartup = false;
        try
        {
            normalizeSettings();
            SerializableVersion serializableVersion = serializableVersionFactory();
            if (serializableVersion == null)
            {
                throw new InvalidOperationException("Serializable version factory returned null.");
            }
            firstStartup = settingsStore.AssemblyVersion == null;
            firstStartupObserver?.Invoke(firstStartup);
            if (settingsStore.AssemblyVersion == null || settingsStore.AssemblyVersion != serializableVersion)
            {
                upgradeSettings();
                settingsStore.AssemblyVersion = serializableVersion;
                saveSettings();
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "Settings upgrade/migration failed");
        }

        if (!availableCultures.Contains(settingsStore.Language))
        {
            settingsStore.Language = "ja-JP";
        }
        string appearanceTheme = AppThemeService.NormalizeTheme(settingsStore.AppearanceTheme);
        settingsStore.AppearanceTheme = appearanceTheme;
        CultureInfo culture = CultureInfo.GetCultureInfo(settingsStore.Language);
        Resources.Culture = culture;
        return new ApplicationSettingsInitializationResult(firstStartup, appearanceTheme, culture);
    }

    internal void MigrateLegacy(IEnumerable<string> availableCultureNames, string currentCultureName)
    {
        if (availableCultureNames == null)
        {
            throw new ArgumentNullException(nameof(availableCultureNames));
        }
        HashSet<string> availableCultures = new(availableCultureNames.Where(value => value != null));
        legacyMigration(availableCultures, currentCultureName);
    }

    internal string GetCurrentAppearanceTheme()
    {
        return settingsStore.AppearanceTheme;
    }
}
