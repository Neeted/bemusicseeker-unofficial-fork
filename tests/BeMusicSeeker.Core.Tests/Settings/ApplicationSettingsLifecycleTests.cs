using System.Globalization;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

/// <remarks>専有ストアと専用XMLで検証し、返された文化圏の表示リソースへの実適用はAppの起動検証へ分担します。</remarks>
[TestClass]
public sealed class ApplicationSettingsLifecycleTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>独立した旧XML生成主体の設定を実providerで読み、型付きバージョンと XML 特殊文字を含む値の互換読込みと既存起動判定を確認します。</summary>
    [TestMethod]
    public void LegacySettingsFixtureGeneratorPreservesTypedVersionAndScalarValues()
    {
        string directory = Path.Combine(CaseWorkspace.RootDirectory, "BmsLegacySettingsFixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        try
        {
            LegacySettingsFixture.Write(path, new Dictionary<string, string>
            {
                ["AssemblyVersion"] = "17.23.41.59",
                ["Lang"] = "ja-JP",
                ["AppearanceTheme"] = "Light",
                ["OperationModeLR2DB"] = "False",
                ["BMSRootPath"] = @"C:\譜面 & <fixture>",
                ["BMSInstallDir"] = @"C:\譜面 & <fixture>\install",
                ["TableListURL"] = "https://example.invalid/?a=1&b=<fixture>"
            });

            Settings settings = TestSettingsFactory.OpenSettings(path);
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
            Directory.Delete(directory, true);
        }
    }

    /// <summary>最初の正規化が失敗した場合、元例外を返して設定の読取り・保存へ進む誤りを検出します。</summary>
    [TestMethod]
    public void InitializeReadFailureDoesNotReadStoreOrContinue()
    {
        var store = new FakeApplicationSettingsStore();
        var expected = new System.IO.IOException("input unavailable");
        var lifecycle = new ApplicationSettingsLifecycle(
            settingsStore: store,
            normalizeSettings: () => throw expected);

        Exception actual = Assert.ThrowsExactly<System.IO.IOException>(() => lifecycle.Initialize(
            ["ja-JP", "en-US"], () => new SerializableVersion(1, 2, 3, 4)));

        Assert.AreSame(expected, actual);
        Assert.AreEqual(0, store.Events.Count);
    }

    /// <summary>旧設定移行の一回呼出し後に不正な言語とテーマを正規化し、既存バージョンを初回起動として扱う誤りを検出します。</summary>
    [TestMethod]
    public void InitializeMigratesAndNormalizesLanguageAndThemeWithoutChangingVersionTiming()
    {
        int migrationCallCount = 0;
        SerializableVersion currentVersion = new(1, 2, 3, 4);
        var store = new FakeApplicationSettingsStore
        {
            AssemblyVersion = currentVersion,
            Language = "invalid-language",
            AppearanceTheme = "invalid-theme"
        };
        var lifecycle = new ApplicationSettingsLifecycle(
            (availableCultures, currentCultureName) => migrationCallCount++,
            settingsStore: store,
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
        Assert.AreEqual(currentVersion, store.AssemblyVersion);
        Assert.AreEqual(0, store.UpgradeCount);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual("ja-JP", store.Language);
        Assert.AreEqual(AppThemeService.Light, result.AppearanceTheme);
        Assert.AreEqual(AppThemeService.Light, store.AppearanceTheme);
        Assert.AreEqual("ja-JP", result.Culture.Name);
    }

    /// <summary>バージョン取得が失敗しても初回扱いせず、読込み済み設定の言語とテーマを正規化して返すことを確認します。</summary>
    [TestMethod]
    public void InitializeVersionFactoryFailureStillNormalizesCultureAndTheme()
    {
        var store = new FakeApplicationSettingsStore
        {
            AssemblyVersion = new SerializableVersion(1, 2, 3, 4),
            Language = "invalid-language",
            AppearanceTheme = "invalid-theme"
        };
        var lifecycle = new ApplicationSettingsLifecycle(settingsStore: store, normalizeSettings: () => { });
        bool observedFirstStartup = false;

        ApplicationSettingsInitializationResult result = lifecycle.Initialize(
            ["ja-JP", "en-US"],
            () => throw new InvalidOperationException("version factory failure"),
            value => observedFirstStartup = value);

        Assert.IsFalse(result.FirstStartup);
        Assert.IsFalse(observedFirstStartup);
        Assert.AreEqual("ja-JP", result.Culture.Name);
        Assert.AreEqual("ja-JP", store.Language);
        Assert.AreEqual(AppThemeService.Light, result.AppearanceTheme);
        Assert.AreEqual(AppThemeService.Light, store.AppearanceTheme);
    }

    /// <summary>保存済みバージョンなしの場合、初回通知を upgrade と保存より前に一回行い、新バージョンを保持することを確認します。</summary>
    [TestMethod]
    public void InitializeReportsFirstStartupBeforeUpgradeAndSave()
    {
        var store = new FakeApplicationSettingsStore { Language = "ja-JP", AppearanceTheme = AppThemeService.Light };
        var events = new List<string>();
        var lifecycle = new ApplicationSettingsLifecycle(
            upgradeSettings: () => events.Add("upgrade"),
            saveSettings: () => events.Add("save"),
            settingsStore: store,
            normalizeSettings: () => { });

        ApplicationSettingsInitializationResult result = lifecycle.Initialize(
            ["ja-JP", "en-US"],
            () => new SerializableVersion(1, 2, 3, 4),
            value => events.Add("firstStartup=" + value));

        Assert.IsTrue(result.FirstStartup);
        CollectionAssert.AreEqual(new[] { "firstStartup=True", "upgrade", "save" }, events);
        Assert.AreEqual(new SerializableVersion(1, 2, 3, 4), store.AssemblyVersion);
    }

    /// <summary>初回更新と正規化は注入ストアに届き、対応言語は保持・未対応言語は日本語として返し、表示リソースの共有文化圏を変更しません。</summary>
    /// <remarks>共有文化圏は書き換えませんが、前後の同一性比較を他試験の切替から分離するため、このケースだけ非並列です。</remarks>
    [TestMethod]
    [DataRow("invalid-language", "ja-JP")]
    [DataRow("en-US", "en-US")]
    [DoNotParallelize]
    public void InitializeUsesInjectedSettingsStoreWithoutChangingResourceCulture(string language, string expectedCulture)
    {
        CultureInfo? previousCulture = Resources.Culture;
        var store = new FakeApplicationSettingsStore
        {
            Language = language,
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
        Assert.AreEqual(expectedCulture, store.Language);
        Assert.AreEqual(expectedCulture, result.Culture.Name);
        Assert.AreSame(previousCulture, Resources.Culture);
        Assert.AreEqual(AppThemeService.Light, store.AppearanceTheme);
        Assert.AreEqual(AppThemeService.Light, result.AppearanceTheme);
        Assert.AreEqual(AppThemeService.Light, lifecycle.GetCurrentAppearanceTheme());
    }

    /// <summary>正規化を記録してから store を読み、旧値の先行 materialize で正規化を取り逃す誤りを検出します。</summary>
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

    /// <summary>実設定の正規化保存を阻止しても一回警告と同じ値・文化圏・テーマを利用します。警告先の通常完了・例外を分担し、排他読取り失敗では警告せず設定処理を止めます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ValidSaveBlockedInputWarnsAndMaterializesCanonicalValuesWhileReadFailureIsFatal(bool warningThrows)
    {
        string directory = Path.Combine(CaseWorkspace.RootDirectory, "BmsLifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        try
        {
            WriteConfig(path, ("AssemblyVersion", "9.8.7.6"), ("Lang", "en-US"), ("AppearanceTheme", AppThemeService.Dark), ("PlayerPanelState", "12"));
            byte[] before = File.ReadAllBytes(path);
            Settings settings = TestSettingsFactory.OpenSettings(path);
            var notices = new System.Collections.Generic.List<Exception>();
            var lifecycle = new ApplicationSettingsLifecycle(settingsStore: new FileSettingsStore(settings),
                normalizeSettings: () => PortableSettingsProvider.NormalizePortableConfig(path),
                warnSaveFailure: exception =>
                {
                    notices.Add(exception);
                    if (warningThrows) { throw new InvalidOperationException("warning callback failure"); }
                });
            File.SetAttributes(path, FileAttributes.ReadOnly);
            ApplicationSettingsInitializationResult result = lifecycle.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6));
            Assert.IsFalse(result.FirstStartup);
            Assert.AreEqual("en-US", result.Culture.Name);
            Assert.AreEqual(AppThemeService.Dark, result.AppearanceTheme);
            Assert.AreEqual(AppThemeService.Dark, settings.AppearanceTheme);
            Assert.AreEqual(new SerializableVersion(9, 8, 7, 6), settings.AssemblyVersion);
            Assert.AreEqual((BeMusicSeeker.ViewModels.PlayerPanelState)10, settings.PlayerPanelState);
            Assert.AreEqual(1, notices.Count);
            Assert.AreEqual("Save", ((PortableSettingsException)notices[0]).Operation);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            File.SetAttributes(path, FileAttributes.Normal);

            var store = new FakeApplicationSettingsStore();
            int readWarningCount = 0;
            lifecycle = new ApplicationSettingsLifecycle(settingsStore: store,
                normalizeSettings: () => PortableSettingsProvider.NormalizePortableConfig(path),
                warnSaveFailure: _ => readWarningCount++);
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsExactly<PortableSettingsException>(() => lifecycle.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)));
            }

            Assert.AreEqual(0, readWarningCount);
            Assert.AreEqual(0, store.Events.Count);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>実ファイルの再読込み失敗は警告せず致命的にし、公開だけの失敗は警告先が例外を返しても同じ値・文化圏・テーマで続行します。全条件で元bytesを保持します。</summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public void VersionUpdateDistinguishesReadFailureFromPublicationFailure(bool blockRead, bool warningThrows)
    {
        string directory = Path.Combine(CaseWorkspace.RootDirectory, "BmsVersionSave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        try
        {
            WriteConfig(path, ("AssemblyVersion", "1.0.0.0"), ("Lang", "en-US"), ("AppearanceTheme", AppThemeService.Dark));
            byte[] before = File.ReadAllBytes(path);
            Settings settings = TestSettingsFactory.OpenSettings(path);
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
                warnSaveFailure: exception =>
                {
                    notices.Add(exception);
                    if (warningThrows) { throw new InvalidOperationException("warning callback failure"); }
                });

            if (blockRead)
            {
                PortableSettingsException failure = Assert.ThrowsExactly<PortableSettingsException>(() => lifecycle.Initialize(
                    ["ja-JP", "en-US"], () => new SerializableVersion(2, 0, 0, 0)));
                Assert.AreEqual("Read", failure.Operation);
                Assert.AreEqual(path, failure.FilePath);
                Assert.AreEqual(0, notices.Count);
            }
            else
            {
                ApplicationSettingsInitializationResult result = lifecycle.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(2, 0, 0, 0));
                Assert.IsFalse(result.FirstStartup);
                Assert.AreEqual("en-US", result.Culture.Name);
                Assert.AreEqual(AppThemeService.Dark, result.AppearanceTheme);
                Assert.AreEqual(AppThemeService.Dark, settings.AppearanceTheme);
                Assert.AreEqual(new SerializableVersion(2, 0, 0, 0), settings.AssemblyVersion);
                Assert.AreEqual(1, notices.Count);
                Assert.AreEqual("Save", ((PortableSettingsException)notices[0]).Operation);
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>バージョン保存の例外を注入し、元例外を警告先へ渡して読込み済み文化圏を結果として返すことを確認します。</summary>
    [TestMethod]
    public void VersionSaveFailureWarnsAndContinuesWithLoadedSettings()
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

    /// <summary>適格な旧設定を実際に移行できる条件で、破損退避後の新規作成失敗を起こし、新 owner による次回起動で再輸入せず退避内容と現設定を保つことを確認します。</summary>
    [TestMethod]
    public void RestartAfterQuarantineAndCreateFailureDoesNotImportEligibleLegacySettings()
    {
        int unexpectedNotificationCount = 0;
        string directory = Path.Combine(CaseWorkspace.RootDirectory, "BmsRestart-" + Guid.NewGuid().ToString("N"));
        string legacyRoot = Path.Combine(directory, "legacy");
        string legacyPath = Path.Combine(legacyRoot, "BeMusicSeeker.exe_Url_example", "1.0", "user.config");
        string path = Path.Combine(directory, "user.config");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var saveWarnings = new List<Exception>();
        try
        {
            WriteConfig(legacyPath, ("AssemblyVersion", "9.8.7.6"), ("Lang", "en-US"),
                ("TableListURL", "https://example.invalid/legacy"), ("BMSInstallDir", "legacy-install"),
                ("OperationModeLR2DB", "False"), ("BMSRootPath", "legacy-root"));
            // First prove that this exact physical legacy candidate is eligible through the real owner.
            ApplicationSettingsLifecycle normal = CreateFileLifecycle(path, legacyRoot, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), saveWarnings.Add);
            Assert.IsFalse(normal.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)).FirstStartup);
            Assert.AreEqual("legacy-install", TestSettingsFactory.OpenSettings(path).BMSInstallDir);

            File.WriteAllText(path, "<configuration>");
            byte[] corrupt = File.ReadAllBytes(path);
            string? backup = null;
            ApplicationSettingsLifecycle failed = CreateFileLifecycle(path, legacyRoot, preserved =>
            {
                backup = preserved;
                Directory.CreateDirectory(path);
            }, saveWarnings.Add);
            PortableSettingsException failure = Assert.ThrowsExactly<PortableSettingsException>(() =>
                failed.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)));
            Assert.AreEqual("Create", failure.Operation);
            Assert.IsNotNull(backup);
            Directory.Delete(path);

            ApplicationSettingsLifecycle restarted = CreateFileLifecycle(path, legacyRoot, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), saveWarnings.Add);
            Assert.IsTrue(restarted.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)).FirstStartup);
            Assert.AreNotEqual("legacy-install", TestSettingsFactory.OpenSettings(path).BMSInstallDir);
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(backup));

            WriteConfig(path, ("AssemblyVersion", "9.8.7.6"), ("Lang", "en-US"), ("BMSInstallDir", "current-install"));
            ApplicationSettingsLifecycle current = CreateFileLifecycle(path, legacyRoot, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), saveWarnings.Add);
            Assert.IsFalse(current.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)).FirstStartup);
            Assert.AreEqual("current-install", TestSettingsFactory.OpenSettings(path).BMSInstallDir);
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(backup));
            Assert.AreEqual(0, saveWarnings.Count);
        }
        finally { Directory.Delete(directory, true); }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>実際に移行可能な旧XMLの公開を対象ディレクトリで阻止し、移行失敗を保存警告へ変換せず、設定読取り・元XML変更を行わないことを確認します。</summary>
    [TestMethod]
    public void InitializeLegacyMigrationFailureIsFatalWithoutSaveWarningOrStoreRead()
    {
        int unexpectedNotificationCount = 0;
        string directory = Path.Combine(CaseWorkspace.RootDirectory, "BmsMigrationFailure-" + Guid.NewGuid().ToString("N"));
        string legacyRoot = Path.Combine(directory, "legacy");
        string legacyPath = Path.Combine(legacyRoot, "BeMusicSeeker.exe_Url_example", "1.0", "user.config");
        string path = Path.Combine(directory, "user.config");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        try
        {
            WriteConfig(legacyPath, ("AssemblyVersion", "9.8.7.6"), ("Lang", "en-US"),
                ("TableListURL", "https://example.invalid/legacy"), ("BMSInstallDir", "legacy-install"),
                ("OperationModeLR2DB", "False"), ("BMSRootPath", "legacy-root"));
            byte[] before = File.ReadAllBytes(legacyPath);
            // 同じ旧XMLが実移行対象になることを確認してから、別の起動で公開だけを阻止します。
            var normalWarnings = new List<Exception>();
            ApplicationSettingsLifecycle normal = CreateFileLifecycle(path, legacyRoot, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), normalWarnings.Add);
            normal.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6));
            Assert.AreEqual("legacy-install", TestSettingsFactory.OpenSettings(path).BMSInstallDir);
            Assert.AreEqual(0, normalWarnings.Count);
            File.Delete(path);
            Directory.CreateDirectory(path);
            var store = new FakeApplicationSettingsStore();
            int warnings = 0;
            Action migrate = () => LegacyUserConfigMigrator.MigrateIfNeeded(path, legacyRoot, new HashSet<string>(["ja-JP", "en-US"]), "en-US");
            var lifecycle = new ApplicationSettingsLifecycle(
                legacyMigration: (cultures, culture) => LegacyUserConfigMigrator.MigrateIfNeeded(path, legacyRoot, cultures, culture),
                settingsStore: store,
                normalizeSettings: migrate,
                warnSaveFailure: _ => warnings++);

            PortableSettingsException failure = Assert.ThrowsExactly<PortableSettingsException>(() =>
                lifecycle.Initialize(["ja-JP", "en-US"], () => new SerializableVersion(9, 8, 7, 6)));

            Assert.AreEqual("Migrate", failure.Operation);
            Assert.AreEqual(path, failure.FilePath);
            Assert.AreEqual(0, warnings);
            Assert.AreEqual(0, store.Events.Count);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(legacyPath));
            Assert.IsTrue(Directory.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    private static ApplicationSettingsLifecycle CreateFileLifecycle(
        string path, string legacyRoot, Action<string> warnRecovery, Action<Exception> warnSaveFailure)
    {
        Action migrate = () => LegacyUserConfigMigrator.MigrateIfNeeded(path, legacyRoot, new HashSet<string>(["ja-JP", "en-US"]), "en-US");
        return new ApplicationSettingsLifecycle(
            legacyMigration: (cultures, culture) => LegacyUserConfigMigrator.MigrateIfNeeded(path, legacyRoot, cultures, culture),
            settingsStore: new FileSettingsStore(TestSettingsFactory.OpenSettings(path)),
            normalizeSettings: () => new PortableSettingsStartupFile(path).Prepare(migrate, warnRecovery),
            warnSaveFailure: warnSaveFailure);
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
            else
            {
                section.Add(new XElement("setting", new XAttribute("name", value.Key), new XAttribute("serializeAs", "String"), new XElement("value", value.Value)));
            }
        }
        new XDocument(new XElement("configuration", new XElement("userSettings", section))).Save(path);
    }

    private sealed class FakeApplicationSettingsStore : IApplicationSettingsStore
    {
        private SerializableVersion? assemblyVersion;

        private string language = string.Empty;

        private string appearanceTheme = string.Empty;

        public System.Collections.Generic.List<string> Events { get; } = [];

        public SerializableVersion? AssemblyVersion
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
