using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationSettingsLifecycleTests
{
    // PORTABLE-SETTINGS-FAILURE-20260905 P03/P06: failed initial reads are fatal before materialization.
    [TestMethod]
    public void InitializeReadFailureDoesNotReadStoreOrContinue()
    {
        var store = new FakeApplicationSettingsStore();
        var expected = new System.IO.IOException("input unavailable");
        var lifecycle = new ApplicationSettingsLifecycle(
            settingsStore: store,
            normalizeSettings: () => throw expected);

        Exception actual = Assert.ThrowsException<System.IO.IOException>(() => lifecycle.Initialize(
            ["ja-JP", "en-US"], () => new SerializableVersion(1, 2, 3, 4)));

        Assert.AreSame(expected, actual);
        Assert.AreEqual(0, store.Events.Count);
    }

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
            var lifecycle = new ApplicationSettingsLifecycle(
                (availableCultures, currentCultureName) => migrationCallCount++,
                normalizeSettings: () => { });

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
            var lifecycle = new ApplicationSettingsLifecycle(
                (availableCultures, currentCultureName) => { },
                normalizeSettings: () => { });
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
                () => events.Add("save"),
                normalizeSettings: () => { });

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

    [TestMethod]
    public void InitializeUsesInjectedSettingsStoreForVersionLanguageAndThemeLifecycle()
    {
        var store = new FakeApplicationSettingsStore
        {
            AssemblyVersion = null!,
            Language = "invalid-language",
            AppearanceTheme = "invalid-theme"
        };
        // メモリ上のライフサイクルを検証する。実ファイルの正規化は専用の一時ファイル試験で扱う。
        var lifecycle = new ApplicationSettingsLifecycle(settingsStore: store, normalizeSettings: () => { });

        ApplicationSettingsInitializationResult result = lifecycle.Initialize(
            ["ja-JP", "en-US"],
            () => new SerializableVersion(1, 2, 3, 4));

        Assert.IsTrue(result.FirstStartup);
        Assert.AreEqual(1, store.UpgradeCount);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(new SerializableVersion(1, 2, 3, 4), store.AssemblyVersion);
        Assert.AreEqual("ja-JP", store.Language);
        Assert.AreEqual(AppThemeService.Light, store.AppearanceTheme);
        Assert.AreEqual(AppThemeService.Light, result.AppearanceTheme);
        Assert.AreEqual(AppThemeService.Light, lifecycle.GetCurrentAppearanceTheme());
    }

    [TestMethod]
    public void InitializeNormalizesSettingsBeforeReadingTheInjectedStore()
    {
        var store = new FakeApplicationSettingsStore
        {
            AssemblyVersion = new SerializableVersion(1, 2, 3, 4),
            Language = "ja-JP",
            AppearanceTheme = AppThemeService.Light
        };
        var lifecycle = new ApplicationSettingsLifecycle(
            settingsStore: store,
            normalizeSettings: () => store.Events.Add("normalize"));

        lifecycle.Initialize(
            ["ja-JP", "en-US"],
            () => new SerializableVersion(1, 2, 3, 4));

        int normalizeIndex = store.Events.IndexOf("normalize");
        int firstReadIndex = store.Events.FindIndex(value => value.StartsWith("read:", StringComparison.Ordinal));
        Assert.IsTrue(normalizeIndex >= 0);
        Assert.IsTrue(firstReadIndex > normalizeIndex);
    }

    // P06: read and save failures use the real provider and generated materialization.
    [TestMethod]
    public void ValidSaveBlockedInputWarnsAndMaterializesCanonicalValuesWhileReadFailureIsFatal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsLifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        CultureInfo previousCulture = Resources.Culture;
        try
        {
            WriteConfig(path, ("AssemblyVersion", "9.8.7.6"), ("Lang", "en-US"), ("PlayerPanelState", "12"));
            byte[] before = File.ReadAllBytes(path);
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
            var notices = new System.Collections.Generic.List<Exception>();
            var lifecycle = new ApplicationSettingsLifecycle(settingsStore: new FileSettingsStore(settings),
                normalizeSettings: () => PortableSettingsProvider.NormalizePortableConfig(path),
                warnSaveFailure: notices.Add);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            ApplicationSettingsInitializationResult result = lifecycle.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6));
            Assert.IsFalse(result.FirstStartup);
            Assert.AreEqual("en-US", result.Culture.Name);
            Assert.AreEqual((BeMusicSeeker.ViewModels.PlayerPanelState)10, settings.PlayerPanelState);
            Assert.AreEqual(1, notices.Count);
            Assert.AreEqual("Save", ((PortableSettingsException)notices[0]).Operation);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            File.SetAttributes(path, FileAttributes.Normal);

            var store = new FakeApplicationSettingsStore();
            lifecycle = new ApplicationSettingsLifecycle(settingsStore: store,
                normalizeSettings: () => PortableSettingsProvider.NormalizePortableConfig(path), warnSaveFailure: _ => Assert.Fail("Read is fatal."));
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsException<PortableSettingsException>(() => lifecycle.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)));
            Assert.AreEqual(0, store.Events.Count);
        }
        finally
        {
            Resources.Culture = previousCulture;
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    // P06: external sharing changes after materialization still distinguish READ from publication failure.
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void VersionUpdateDistinguishesReadFailureFromPublicationFailure(bool blockRead)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsVersionSave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        CultureInfo previousCulture = Resources.Culture;
        try
        {
            WriteConfig(path, ("AssemblyVersion", "1.0.0.0"), ("Lang", "en-US"));
            byte[] before = File.ReadAllBytes(path);
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
            var notices = new System.Collections.Generic.List<Exception>();
            var lifecycle = new ApplicationSettingsLifecycle(
                settingsStore: new FileSettingsStore(settings),
                normalizeSettings: () => PortableSettingsProvider.NormalizePortableConfig(path),
                saveSettings: () =>
                {
                    // The initial getter has succeeded. Model another process opening the file before version Save.
                    using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read,
                        blockRead ? FileShare.None : FileShare.ReadWrite);
                    settings.Save();
                },
                warnSaveFailure: notices.Add);

            if (blockRead)
            {
                PortableSettingsException failure = Assert.ThrowsException<PortableSettingsException>(() => lifecycle.Initialize(
                    ["ja-JP", "en-US"], () => new SerializableVersion(2, 0, 0, 0)));
                Assert.AreEqual("Read", failure.Operation);
                Assert.AreEqual(path, failure.FilePath);
                Assert.AreEqual(0, notices.Count);
                Assert.AreSame(previousCulture, Resources.Culture);
            }
            else
            {
                ApplicationSettingsInitializationResult result = lifecycle.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(2, 0, 0, 0));
                Assert.IsFalse(result.FirstStartup);
                Assert.AreEqual("en-US", result.Culture.Name);
                Assert.AreEqual(1, notices.Count);
                Assert.AreEqual("Save", ((PortableSettingsException)notices[0]).Operation);
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }
        finally
        {
            Resources.Culture = previousCulture;
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void VersionSaveFailureWarnsAndContinuesWithLoadedSettings()
    {
        CultureInfo previousCulture = Resources.Culture;
        try
        {
            var store = new FakeApplicationSettingsStore { AssemblyVersion = new SerializableVersion(1, 0, 0, 0), Language = "en-US", AppearanceTheme = AppThemeService.Light };
            var expected = new IOException("save unavailable");
            Exception? warning = null;
            var lifecycle = new ApplicationSettingsLifecycle(settingsStore: store, normalizeSettings: () => { },
                saveSettings: () => throw expected, warnSaveFailure: exception => warning = exception);
            ApplicationSettingsInitializationResult result = lifecycle.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(2, 0, 0, 0));
            Assert.AreSame(expected, warning);
            Assert.IsFalse(result.FirstStartup);
            Assert.AreEqual("en-US", result.Culture.Name);
            Assert.AreEqual(1, store.UpgradeCount);
        }
        finally { Resources.Culture = previousCulture; }
    }

    [TestMethod]
    [TestCategory("ProcessIntegration")]
    public void LegacySettingsFixtureGeneratorPreservesTypedVersionAndScalarValues()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsLegacySettingsFixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        CultureInfo previousCulture = Resources.Culture;
        try
        {
            RunLegacySettingsFixtureGenerator(path);

            Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
            SerializableVersion version = settings.AssemblyVersion;
            Assert.IsNotNull(version);
            Assert.AreEqual(17, version.Major);
            Assert.AreEqual(23, version.Minor);
            Assert.AreEqual(41, version.Build);
            Assert.AreEqual(59, version.Revision);
            Assert.AreEqual("ja-JP", settings.Lang);
            Assert.AreEqual("Light", settings.AppearanceTheme);
            Assert.IsFalse(settings.OperationModeLR2DB);
            Assert.AreEqual("C:\\譜面 & <fixture>", settings.BMSRootPath);
            Assert.AreEqual("C:\\譜面 & <fixture>\\install", settings.BMSInstallDir);
            Assert.AreEqual(
                new Uri("https://example.invalid/?a=1&b=<fixture>"),
                settings.TableListURL);

            var lifecycle = new ApplicationSettingsLifecycle(
                settingsStore: new FileSettingsStore(settings),
                normalizeSettings: () => { });
            ApplicationSettingsInitializationResult result = lifecycle.Initialize(
                ["ja-JP", "en-US"],
                () => new SerializableVersion(17, 23, 41, 59));
            Assert.IsFalse(result.FirstStartup);
        }
        finally
        {
            Resources.Culture = previousCulture;
            Directory.Delete(directory, true);
        }
    }

    // P04b rev2: a new lifecycle after failed creation must use the preserved file, not process memory, to suppress import.
    [TestMethod]
    public void RestartAfterQuarantineAndCreateFailureDoesNotImportEligibleLegacySettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsRestart-" + Guid.NewGuid().ToString("N"));
        string legacyRoot = Path.Combine(directory, "legacy");
        string legacyPath = Path.Combine(legacyRoot, "BeMusicSeeker.exe_Url_example", "1.0", "user.config");
        string path = Path.Combine(directory, "user.config");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        CultureInfo previousCulture = Resources.Culture;
        try
        {
            WriteConfig(legacyPath, ("AssemblyVersion", "9.8.7.6"), ("Lang", "en-US"),
                ("TableListURL", "https://example.invalid/legacy"), ("BMSInstallDir", "legacy-install"),
                ("OperationModeLR2DB", "False"), ("BMSRootPath", "legacy-root"));
            // First prove that this exact physical legacy candidate is eligible through the real owner.
            ApplicationSettingsLifecycle normal = CreateFileLifecycle(path, legacyRoot, _ => Assert.Fail());
            Assert.IsFalse(normal.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)).FirstStartup);
            Assert.AreEqual("legacy-install", PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);

            File.WriteAllText(path, "<configuration>");
            byte[] corrupt = File.ReadAllBytes(path);
            string? backup = null;
            ApplicationSettingsLifecycle failed = CreateFileLifecycle(path, legacyRoot, preserved =>
            {
                backup = preserved;
                Directory.CreateDirectory(path);
            });
            PortableSettingsException failure = Assert.ThrowsException<PortableSettingsException>(() =>
                failed.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)));
            Assert.AreEqual("Create", failure.Operation);
            Assert.IsNotNull(backup);
            Directory.Delete(path);

            ApplicationSettingsLifecycle restarted = CreateFileLifecycle(path, legacyRoot, _ => Assert.Fail("Already quarantined."));
            Assert.IsTrue(restarted.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)).FirstStartup);
            Assert.AreNotEqual("legacy-install", PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(backup));

            WriteConfig(path, ("AssemblyVersion", "9.8.7.6"), ("Lang", "en-US"), ("BMSInstallDir", "current-install"));
            ApplicationSettingsLifecycle current = CreateFileLifecycle(path, legacyRoot, _ => Assert.Fail());
            Assert.IsFalse(current.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)).FirstStartup);
            Assert.AreEqual("current-install", PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(backup));
        }
        finally { Resources.Culture = previousCulture; Directory.Delete(directory, true); }
    }

    private static ApplicationSettingsLifecycle CreateFileLifecycle(string path, string legacyRoot, Action<string> warnRecovery)
    {
        ApplicationSettingsLifecycle lifecycle = null!;
        lifecycle = new ApplicationSettingsLifecycle(
            legacyMigration: (cultures, culture) => LegacyUserConfigMigrator.MigrateIfNeeded(path, legacyRoot, cultures, culture),
            settingsStore: new FileSettingsStore(PortableSettingsPersistenceTests.OpenSettings(path)),
            normalizeSettings: () => new PortableSettingsStartupFile(path).Prepare(
                () => lifecycle.MigrateLegacy(["ja-JP", "en-US"], "en-US"), warnRecovery),
            warnSaveFailure: _ => Assert.Fail("No save fault is expected."));
        return lifecycle;
    }

    private static void RunLegacySettingsFixtureGenerator(string path)
    {
        string repositoryRoot = PowerShellTestProcess.FindRepositoryRoot();
        PowerShellTestResult result = PowerShellTestProcess.Run(
            repositoryRoot,
            """
            $ErrorActionPreference = 'Stop'
            . $env:BMS_SETTINGS_FIXTURE_SCRIPT
            $settings = @{
                AssemblyVersion = '17.23.41.59'
                Lang = 'ja-JP'
                AppearanceTheme = 'Light'
                OperationModeLR2DB = 'False'
                BMSRootPath = 'C:\譜面 & <fixture>'
                BMSInstallDir = 'C:\譜面 & <fixture>\install'
                TableListURL = 'https://example.invalid/?a=1&b=<fixture>'
            }
            Write-LegacyConfig -Path $env:BMS_SETTINGS_FIXTURE_PATH -Settings $settings
            """,
            "Legacy settings fixture",
            new Dictionary<string, string>
            {
                ["BMS_SETTINGS_FIXTURE_SCRIPT"] = Path.Combine(
                    repositoryRoot,
                    "scripts",
                    "acceptance-settings-fixture.ps1"),
                ["BMS_SETTINGS_FIXTURE_PATH"] = path
            });
        Assert.IsTrue(
            string.IsNullOrWhiteSpace(result.Error),
            $"Legacy settings fixture generator wrote stderr: {result.Error}");
    }

    private static void WriteConfig(string path, params (string Key, string Value)[] values)
    {
        var section = new XElement(PortableSettingsProvider.SettingsSectionName);
        foreach ((string Key, string Value) value in values)
        {
            if (value.Key == "AssemblyVersion")
            {
                using var serialized = new StringWriter();
                new System.Xml.Serialization.XmlSerializer(typeof(SerializableVersion)).Serialize(serialized, new SerializableVersion(value.Value));
                section.Add(new XElement("setting", new XAttribute("name", value.Key), new XAttribute("serializeAs", "Xml"),
                    new XElement("value", XElement.Parse(serialized.ToString()))));
            }
            else section.Add(new XElement("setting", new XAttribute("name", value.Key), new XAttribute("serializeAs", "String"), new XElement("value", value.Value)));
        }
        new XDocument(new XElement("configuration", new XElement("userSettings", section))).Save(path);
    }

    private sealed class FileSettingsStore(Settings settings) : IApplicationSettingsStore
    {
        public SerializableVersion AssemblyVersion { get => settings.AssemblyVersion; set => settings.AssemblyVersion = value; }
        public string Language { get => settings.Lang; set => settings.Lang = value; }
        public string AppearanceTheme { get => settings.AppearanceTheme; set => settings.AppearanceTheme = value; }
        public void Save() => settings.Save();
        public void Upgrade() => settings.Upgrade();
    }

    private sealed class FakeApplicationSettingsStore : IApplicationSettingsStore
    {
        private SerializableVersion assemblyVersion = null!;

        private string language = null!;

        private string appearanceTheme = null!;

        public System.Collections.Generic.List<string> Events { get; } = [];

        public SerializableVersion AssemblyVersion
        {
            get
            {
                Events.Add("read:AssemblyVersion");
                return assemblyVersion;
            }
            set => assemblyVersion = value;
        }

        public string Language
        {
            get
            {
                Events.Add("read:Language");
                return language;
            }
            set => language = value;
        }

        public string AppearanceTheme
        {
            get
            {
                Events.Add("read:AppearanceTheme");
                return appearanceTheme;
            }
            set => appearanceTheme = value;
        }

        public int UpgradeCount { get; private set; }

        public int SaveCount { get; private set; }

        public void Upgrade()
        {
            UpgradeCount++;
            Events.Add("upgrade");
        }

        public void Save()
        {
            SaveCount++;
            Events.Add("save");
        }
    }
}
