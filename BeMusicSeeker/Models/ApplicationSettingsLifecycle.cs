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

    internal ApplicationSettingsLifecycle(
        Action<ISet<string>, string> legacyMigration = null,
        Action upgradeSettings = null,
        Action saveSettings = null,
        IApplicationSettingsStore settingsStore = null)
    {
        this.legacyMigration = legacyMigration ?? LegacyUserConfigMigrator.MigrateIfNeeded;
        this.settingsStore = settingsStore ?? new SettingsApplicationSettingsStore();
        this.upgradeSettings = upgradeSettings ?? this.settingsStore.Upgrade;
        this.saveSettings = saveSettings ?? this.settingsStore.Save;
    }

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
