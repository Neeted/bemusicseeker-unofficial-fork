using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationSettingsLifecycleTests
{
    [TestMethod]
    public void InitializeMigratesAndNormalizesLanguageAndThemeWithoutChangingVersionTiming()
    {
        SerializableVersion previousVersion = Settings.Default.AssemblyVersion;
        string previousLanguage = Settings.Default.Lang;
        string previousTheme = Settings.Default.AppearanceTheme;
        System.Globalization.CultureInfo previousCulture = Resources.Culture;
        int migrationCallCount = 0;
        SerializableVersion currentVersion = new(1, 2, 3, 4);
        try
        {
            Settings.Default.AssemblyVersion = currentVersion;
            Settings.Default.Lang = "invalid-language";
            Settings.Default["AppearanceTheme"] = "invalid-theme";
            var lifecycle = new ApplicationSettingsLifecycle((availableCultures, currentCultureName) => migrationCallCount++);

            lifecycle.MigrateLegacy(["ja-JP", "en-US"], "en-US");
            bool observedFirstStartup = true;

            ApplicationSettingsInitializationResult result = lifecycle.Initialize(
                ["ja-JP", "en-US"],
                () => currentVersion,
                value => observedFirstStartup = value);

            Assert.AreEqual(1, migrationCallCount);
            Assert.IsFalse(result.FirstStartup);
            Assert.IsFalse(observedFirstStartup);
            Assert.AreEqual("ja-JP", Settings.Default.Lang);
            Assert.AreEqual(AppThemeService.Light, result.AppearanceTheme);
            Assert.AreEqual(AppThemeService.Light, Settings.Default.AppearanceTheme);
            Assert.AreEqual("ja-JP", result.Culture.Name);
            Assert.AreEqual("ja-JP", Resources.Culture.Name);
        }
        finally
        {
            Settings.Default.AssemblyVersion = previousVersion;
            Settings.Default.Lang = previousLanguage;
            Settings.Default.AppearanceTheme = previousTheme;
            Resources.Culture = previousCulture;
        }
    }

    [TestMethod]
    public void InitializeVersionFactoryFailureStillNormalizesCultureAndTheme()
    {
        SerializableVersion previousVersion = Settings.Default.AssemblyVersion;
        string previousLanguage = Settings.Default.Lang;
        string previousTheme = Settings.Default.AppearanceTheme;
        System.Globalization.CultureInfo previousCulture = Resources.Culture;
        try
        {
            Settings.Default.AssemblyVersion = new SerializableVersion(1, 2, 3, 4);
            Settings.Default.Lang = "invalid-language";
            Settings.Default["AppearanceTheme"] = "invalid-theme";
            var lifecycle = new ApplicationSettingsLifecycle((availableCultures, currentCultureName) => { });
            bool observedFirstStartup = false;

            ApplicationSettingsInitializationResult result = lifecycle.Initialize(
                ["ja-JP", "en-US"],
                () => throw new InvalidOperationException("version factory failure"),
                value => observedFirstStartup = value);

            Assert.IsFalse(result.FirstStartup);
            Assert.IsFalse(observedFirstStartup);
            Assert.AreEqual("ja-JP", Resources.Culture.Name);
            Assert.AreEqual(AppThemeService.Light, result.AppearanceTheme);
        }
        finally
        {
            Settings.Default.AssemblyVersion = previousVersion;
            Settings.Default.Lang = previousLanguage;
            Settings.Default.AppearanceTheme = previousTheme;
            Resources.Culture = previousCulture;
        }
    }

    [TestMethod]
    public void InitializeReportsFirstStartupBeforeUpgradeAndSave()
    {
        SerializableVersion previousVersion = Settings.Default.AssemblyVersion;
        string previousLanguage = Settings.Default.Lang;
        string previousTheme = Settings.Default.AppearanceTheme;
        System.Globalization.CultureInfo previousCulture = Resources.Culture;
        var events = new System.Collections.Generic.List<string>();
        try
        {
            Settings.Default.AssemblyVersion = null;
            var lifecycle = new ApplicationSettingsLifecycle(
                (availableCultures, currentCultureName) => { },
                () => events.Add("upgrade"),
                () => events.Add("save"));

            ApplicationSettingsInitializationResult result = lifecycle.Initialize(
                ["ja-JP", "en-US"],
                () => new SerializableVersion(1, 2, 3, 4),
                value => events.Add("firstStartup=" + value));

            Assert.IsTrue(result.FirstStartup);
            CollectionAssert.AreEqual(new[] { "firstStartup=True", "upgrade", "save" }, events);
            Assert.AreEqual(1, Settings.Default.AssemblyVersion?.Major);
        }
        finally
        {
            Settings.Default.AssemblyVersion = previousVersion;
            Settings.Default.Lang = previousLanguage;
            Settings.Default.AppearanceTheme = previousTheme;
            Resources.Culture = previousCulture;
        }
    }
}
