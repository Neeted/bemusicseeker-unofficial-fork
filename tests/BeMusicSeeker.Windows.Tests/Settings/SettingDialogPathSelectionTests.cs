using System.IO;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.SettingsMediaTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>LR2パス候補の採用・由来表示・拒否状態の復旧を検証します。</summary>
/// <remarks>実設定VMと専用FS・XMLでパス候補を検証します。VMが共有ResourceServiceを購読するため非並列にします。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingDialogPathSelectionTests
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

    /// <summary>標準／個別の欠損 DB を同じ path に復旧して再選択すると status を通知・更新し、path tuple と config を保存せず保つことを確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lr2SongDbPicker_ReselectingRestoredFileRefreshesStatusWithoutSaving(bool useIndividualPath)
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string root = Path.Combine(scope, "root");
            (string standardSong, string configPath) = CreateValidLr2Layout(root);
            string songPath = useIndividualPath ? Path.Combine(scope, "songs.db") : standardSong;
            File.WriteAllBytes(songPath, []);
            File.Delete(songPath);
            byte[] configBefore = File.ReadAllBytes(configPath);

            Settings values = CreateValidStandaloneSettings(scope);
            values.OperationModeLR2DB = true;
            values.LR2RootPath = root;
            values.LR2SongDBPath = songPath;
            values.LR2ConfigXmlPath = configPath;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel draft = harness.Dialog;
            Assert.AreEqual(Resources.Settings_path_missing, draft.Lr2SongDbPathStatusText);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
            var notifications = new List<string>();
            draft.PropertyChanged += (_, args) => notifications.Add(args.PropertyName ?? string.Empty);

            File.WriteAllBytes(songPath, []);
            draft.SelectLr2DetailPathAsync(nameof(draft.LR2SongDBPath), songPath).GetAwaiter().GetResult();

            CollectionAssert.Contains(notifications, nameof(draft.Lr2SongDbPathStatusText));
            CollectionAssert.Contains(notifications, nameof(draft.Lr2SongDbPathStatusKind));
            CollectionAssert.Contains(notifications, nameof(draft.Lr2SongDbPathStatusIcon));
            Assert.AreEqual("Success", draft.Lr2SongDbPathStatusKind);
            Assert.AreEqual(
                useIndividualPath
                    ? Resources.Settings_path_detected_from_individual_setting
                    : Resources.Settings_path_detected_from_lr2_root,
                draft.Lr2SongDbPathStatusText);
            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(songPath, draft.LR2SongDBPath);
            Assert.AreEqual(configPath, draft.LR2ConfigXmlPath);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
            Assert.AreEqual(0, session.SaveCount);
            CollectionAssert.AreEqual(configBefore, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>個別 path と標準 song.db／config.xmh を往復選択すると現 path の由来を表示し、過去の選択履歴を引き継ぐ誤りを検出します。</summary>
    [TestMethod]
    public void Lr2PathSource_UsesCurrentStandardLayoutInsteadOfSelectionHistory()
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string root = Path.Combine(scope, "root");
            (string standardSong, _) = CreateValidLr2Layout(root);
            string standardXmh = Path.Combine(root, "LR2files", "Config", "config.xmh");
            File.WriteAllText(standardXmh, "<config><system /><jukebox /></config>");
            string individualSong = Path.Combine(scope, "individual", "database", "songs.db");
            string individualConfig = Path.Combine(scope, "individual", "configuration", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(individualSong)!);
            Directory.CreateDirectory(Path.GetDirectoryName(individualConfig)!);
            File.WriteAllBytes(individualSong, []);
            File.WriteAllText(individualConfig, "<config><system /><jukebox /></config>");

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = individualSong;
            values.LR2ConfigXmlPath = individualConfig;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel dialog = harness.Dialog;

            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2ConfigPathStatusText);

            dialog.SelectLr2DetailPathAsync(nameof(dialog.LR2SongDBPath), standardSong).GetAwaiter().GetResult();
            dialog.SelectLr2DetailPathAsync(nameof(dialog.LR2ConfigXmlPath), standardXmh).GetAwaiter().GetResult();

            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2ConfigPathStatusText);

            dialog.SelectLr2DetailPathAsync(nameof(dialog.LR2SongDBPath), individualSong).GetAwaiter().GetResult();
            dialog.SelectLr2DetailPathAsync(nameof(dialog.LR2ConfigXmlPath), individualConfig).GetAwaiter().GetResult();

            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, dialog.Lr2ConfigPathStatusText);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>別の有効 LR2 root を選択すると root・DB・config の標準 tuple と由来表示をまとめて置換し、旧 DB だけを保持する誤りを検出します。</summary>
    [TestMethod]
    public void Lr2RootSelection_ReplacesTheWholeStandardTupleWithoutKeepingStaleSongDb()
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        string firstRoot = Path.Combine(scope, "first");
        string nextRoot = Path.Combine(scope, "next");
        try
        {
            (string firstSong, string firstConfig) = CreateValidLr2Layout(firstRoot);
            (string nextSong, string nextConfig) = CreateValidLr2Layout(nextRoot);
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = firstRoot;
            values.LR2SongDBPath = firstSong;
            values.LR2ConfigXmlPath = firstConfig;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel dialog = harness.Dialog;

            dialog.LR2RootPath = nextRoot;

            Assert.AreEqual(nextRoot, dialog.LR2RootPath);
            Assert.AreEqual(nextSong, dialog.LR2SongDBPath);
            Assert.AreEqual(nextConfig, dialog.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, dialog.Lr2ConfigPathStatusText);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>壊れた config 候補を拒否して元の有効 path と成功状態を保ち、候補拒否の診断を別に出すことを確認します。</summary>
    [TestMethod]
    public void Lr2ConfigPicker_MalformedCandidateKeepsPreviousRawPathAndReportsFailure()
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string config) = CreateValidLr2Layout(root);
            string malformedConfig = Path.Combine(scope, "malformed", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(malformedConfig)!);
            File.WriteAllText(malformedConfig, "<config>");
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = config;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel draft = harness.Dialog;

            draft.SelectLr2DetailPathAsync(nameof(draft.LR2ConfigXmlPath), malformedConfig).GetAwaiter().GetResult();

            Assert.AreEqual(config, draft.LR2ConfigXmlPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            StringAssert.Contains(draft.Lr2PathSelectionError, Resources.Error_InvalidLR2SongDbOrConfigPath);
            System.Globalization.CultureInfo? previousLanguage = Resources.Culture;
            try
            {
                foreach (string language in new[] { "en-US", "ja-JP", "en-US" })
                {
                    ResourceService.Current.ChangeCulture(language);
                    Assert.AreEqual(Resources.Error_InvalidLR2SongDbOrConfigPath, draft.Lr2PathSelectionError);
                    Assert.IsTrue(draft.HasLr2PathSelectionError);
                    Assert.AreEqual(config, draft.LR2ConfigXmlPath);
                    Assert.AreEqual(0, session.SaveCount);
                }
                draft.CancelEnvironment();
                Assert.IsFalse(draft.HasLr2PathSelectionError);
                Assert.AreEqual(string.Empty, draft.Lr2PathSelectionError);
            }
            finally
            {
                if (previousLanguage == null) { Resources.Culture = null; }
                else { ResourceService.Current.ChangeCulture(previousLanguage.Name); }
            }
            Assert.AreEqual("Success", draft.Lr2ConfigPathStatusKind);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>不正 root 候補を拒否して root／DB／config 全 tuple を保ち、root 選択エラーを出すことを確認します。</summary>
    [TestMethod]
    public void Lr2RootPicker_InvalidCandidateLeavesWholeTupleUnchangedAndReportsFailure()
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string config) = CreateValidLr2Layout(root);
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = config;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel draft = harness.Dialog;

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), Path.Combine(scope, "invalid"));

            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(song, draft.LR2SongDBPath);
            Assert.AreEqual(config, draft.LR2ConfigXmlPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            StringAssert.Contains(draft.Lr2PathSelectionError, Resources.Error_InvalidLR2RootPath);
            System.Globalization.CultureInfo? previousLanguage = Resources.Culture;
            try
            {
                foreach (string language in new[] { "en-US", "ja-JP", "en-US" })
                {
                    ResourceService.Current.ChangeCulture(language);
                    Assert.AreEqual(Resources.Error_InvalidLR2RootPath, draft.Lr2PathSelectionError);
                    Assert.IsTrue(draft.HasLr2PathSelectionError);
                    Assert.AreEqual(root, draft.LR2RootPath);
                    Assert.AreEqual(0, session.SaveCount);
                }
                draft.SetPresentationActive(false);
                draft.SetPresentationActive(true);
                Assert.IsFalse(draft.HasLr2PathSelectionError);
                Assert.AreEqual(string.Empty, draft.Lr2PathSelectionError);
                Assert.AreEqual(root, draft.LR2RootPath);
                Assert.AreEqual(0, session.SaveCount);
            }
            finally
            {
                if (previousLanguage == null) { Resources.Culture = null; }
                else { ResourceService.Current.ChangeCulture(previousLanguage.Name); }
            }
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>同じ有効rootの再選択では採用済み個別XMLを維持し、DBの標準配置への復帰とXMLの個別指定の表示を区別します。</summary>
    [TestMethod]
    public void Lr2RootPicker_ReselectingSameRootKeepsAdoptedXmlAndRestoresStandardSongDb()
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string root = Path.Combine(scope, "root");
            (string standardSong, string standardConfig) = CreateValidLr2Layout(root);
            string customSong = Path.Combine(scope, "custom", "song.db");
            string customConfig = Path.Combine(scope, "custom", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(customSong)!);
            File.WriteAllBytes(customSong, []);
            File.WriteAllText(customConfig, "<config><system /><jukebox /></config>");
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = customSong;
            values.LR2ConfigXmlPath = customConfig;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel draft = harness.Dialog;

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), root);

            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(standardSong, draft.LR2SongDBPath);
            Assert.AreEqual(customConfig, draft.LR2ConfigXmlPath);
            Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, draft.Lr2SongDbPathStatusText);
            Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, draft.Lr2ConfigPathStatusText);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>不正 root の拒否後に現在の有効 root を再選択すると拒否状態だけを解除し、元 tuple を保つことを確認します。</summary>
    [TestMethod]
    public void Lr2RootPicker_ReselectingCurrentValidRootClearsPriorInvalidSelectionError()
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string config) = CreateValidLr2Layout(root);
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = config;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel draft = harness.Dialog;

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), Path.Combine(scope, "invalid"));
            Assert.IsTrue(draft.HasLr2PathSelectionError);

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), root);

            Assert.IsFalse(draft.HasLr2PathSelectionError);
            Assert.AreEqual(root, draft.LR2RootPath);
            Assert.AreEqual(song, draft.LR2SongDBPath);
            Assert.AreEqual(config, draft.LR2ConfigXmlPath);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>同じ／別の有効 root に復旧すると raw tuple を依存表示より先に一回ずつ通知し、拒否状態を解除することを確認して、途中の不整合表示や重複通知を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lr2RootPicker_RecoveryNotifiesRawTupleBeforeDependentPresentationExactlyOnce(bool useDifferentRoot)
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string originalRoot = Path.Combine(scope, "original");
            (string originalSong, string originalConfig) = CreateValidLr2Layout(originalRoot);
            string recoveryRoot = useDifferentRoot ? Path.Combine(scope, "different") : originalRoot;
            (string recoverySong, string recoveryConfig) = useDifferentRoot
                ? CreateValidLr2Layout(recoveryRoot)
                : (originalSong, originalConfig);
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = originalRoot;
            values.LR2SongDBPath = originalSong;
            values.LR2ConfigXmlPath = originalConfig;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel draft = harness.Dialog;
            var changedProperties = new List<string>();
            draft.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), Path.Combine(scope, "invalid"));
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            changedProperties.Clear();

            draft.SetRootFolderPathFromPicker(nameof(draft.LR2RootPath), recoveryRoot);

            string[] expectedSequence =
            [
                nameof(draft.LR2RootPath),
                nameof(draft.LR2SongDBPath),
                nameof(draft.LR2ConfigXmlPath),
                nameof(draft.LR2bodyPath),
                nameof(draft.Lr2SongDbPathStatusIcon),
                nameof(draft.Lr2SongDbPathStatusKind),
                nameof(draft.Lr2SongDbPathStatusText),
                nameof(draft.Lr2ConfigPathStatusIcon),
                nameof(draft.Lr2ConfigPathStatusKind),
                nameof(draft.Lr2ConfigPathStatusText),
                nameof(draft.Lr2PathSelectionError),
                nameof(draft.HasLr2PathSelectionError)
            ];
            HashSet<string> observedContractProperties = [.. expectedSequence];
            string[] actualSequence = changedProperties
                .Where(observedContractProperties.Contains)
                .ToArray();
            CollectionAssert.AreEqual(expectedSequence, actualSequence);
            Assert.AreEqual(recoveryRoot, draft.LR2RootPath);
            Assert.AreEqual(recoverySong, draft.LR2SongDBPath);
            Assert.AreEqual(recoveryConfig, draft.LR2ConfigXmlPath);
            Assert.IsFalse(draft.HasLr2PathSelectionError);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>DB／config 候補の拒否後に保持中の有効 path を再選択すると両方の拒否状態を解除し、成功 status を保つことを確認します。</summary>
    [TestMethod]
    public void Lr2IndividualPickers_ReselectingRetainedValidPathsClearsRejectedCandidateError()
    {
        string scope = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string root = Path.Combine(scope, "root");
            (string song, string config) = CreateValidLr2Layout(root);
            string invalidSong = Path.Combine(scope, "missing.db");
            string invalidConfig = Path.Combine(scope, "malformed.xml");
            File.WriteAllText(invalidConfig, "<config>");
            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = song;
            values.LR2ConfigXmlPath = config;
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            SettingsDialogViewModel draft = harness.Dialog;

            draft.SelectLr2DetailPathAsync(nameof(draft.LR2SongDBPath), invalidSong).GetAwaiter().GetResult();
            Assert.AreEqual(song, draft.LR2SongDBPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            draft.SelectLr2DetailPathAsync(nameof(draft.LR2SongDBPath), song).GetAwaiter().GetResult();
            Assert.IsFalse(draft.HasLr2PathSelectionError);
            Assert.AreEqual("Success", draft.Lr2SongDbPathStatusKind);

            draft.SelectLr2DetailPathAsync(nameof(draft.LR2ConfigXmlPath), invalidConfig).GetAwaiter().GetResult();
            Assert.AreEqual(config, draft.LR2ConfigXmlPath);
            Assert.IsTrue(draft.HasLr2PathSelectionError);
            draft.SelectLr2DetailPathAsync(nameof(draft.LR2ConfigXmlPath), config).GetAwaiter().GetResult();
            Assert.IsFalse(draft.HasLr2PathSelectionError);
            Assert.AreEqual("Success", draft.Lr2ConfigPathStatusKind);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }
}
