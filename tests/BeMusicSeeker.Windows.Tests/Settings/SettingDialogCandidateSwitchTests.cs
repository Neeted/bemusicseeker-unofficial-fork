using System.Configuration;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.Tests.SettingsRootTestFixture;
using static BeMusicSeeker.Tests.SettingsWindowTestFixture;
using static BeMusicSeeker.TestSupport.Components.SettingsMediaTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>設定候補の切替・破棄と導入先確定の接続を検証します。</summary>
/// <remarks>共有 culture・theme・Bass 受付と composition 購読を使うため非並列にします。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingDialogCandidateSwitchTests
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

    /// <summary>BMS候補から競合操作へ切り替える通常入口で、取消・承認・実確定のP先着・候補編集のL/P非要求を分け、対象範囲名・候補保持と保存副作用を確認します。無関係なBMT候補は全経路で保持します。</summary>
    /// <remarks>条件網羅はWindowを使わず実専用providerと既存受付で行い、pickerとBindingはcompiled系列へ分担します。</remarks>
    [TestMethod]
    [DataRow("normal", "cancel")]
    [DataRow("normal", "accept")]
    [DataRow("normal", "busy")]
    [DataRow("root-output", "cancel")]
    [DataRow("root-output", "accept")]
    [DataRow("additional", "cancel")]
    [DataRow("additional", "accept")]
    [DataRow("song", "cancel")]
    [DataRow("song", "accept")]
    [DataRow("song", "busy")]
    [DataRow("config", "cancel")]
    [DataRow("config", "accept")]
    [DataRow("config", "library-busy")]
    [DataRow("mode", "cancel")]
    [DataRow("mode", "accept")]
    [DataRow("lr2-root", "cancel")]
    [DataRow("lr2-root", "accept")]
    [DataRow("install", "cancel")]
    [DataRow("install", "accept")]
    [DataRow("install", "library-busy")]
    public void ConflictingBmsCandidates_SwitchOnlyAfterValidAcceptedOperation(string operation, string outcome)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            IDisposable? held = null;
            Exception? primaryFailure = null;
            try
            {
                string settingsPath = Path.Combine(root, "settings", "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(settingsPath));
                string alternate = Directory.CreateDirectory(Path.Combine(root, "AlternateSongs")).FullName;
                LR2Config xml = TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath);
                xml.AddBMSSearchDirectories([alternate]);
                xml.Save(xml.ConfigFilePath);
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(outcome == "cancel" ? MessageBoxResult.Cancel : MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
                string staged = Directory.CreateDirectory(Path.Combine(root, "StagedSongs")).FullName;
                dialog.AddBmsSearchRootPathsAsync([staged]).GetAwaiter().GetResult();
                dialog.EnableBeatorajaBmtOutput = !values.EnableBeatorajaBmtOutput;
                byte[] before = File.ReadAllBytes(settingsPath);
                byte[] beforeXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                if (outcome is "busy" or "library-busy")
                {
                    dialogs.ConfirmationObserved = _ =>
                    {
                        ChartFileOperationSynchronizer admission = outcome == "library-busy" ? composition.OperationAdmission : composition.PlaylistOperationAdmission;
                        Assert.IsTrue(admission.TryEnter(out IDisposable acquired));
                        held = acquired;
                    };
                }
                (string song, string config) = CreateValidLr2Layout(Path.Combine(root, "SelectedLr2"));
                Task request = Task.CompletedTask;
                switch (operation)
                {
                    case "normal": dialog.LR2CustomFolderOutputDir = Path.Combine(root, "NewNormal"); request = dialog.CommitOutputSettingsAsync("normal"); break;
                    case "root-output": dialog.LR2CustomFolderAsRootOutputDir = Path.Combine(root, "NewRoot"); request = dialog.CommitOutputSettingsAsync("root"); break;
                    case "additional": dialog.AddCustomFolderAdditionalOutputBaseDirs([Path.Combine(root, "NewAdditional")]); request = dialog.CommitOutputSettingsAsync("additional"); break;
                    case "song": dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), song).GetAwaiter().GetResult(); break;
                    case "config": dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), config).GetAwaiter().GetResult(); break;
                    case "mode": request = dialog.SelectOperationModeAsync(false); break;
                    case "lr2-root": request = dialog.SelectLr2RootAsync(Path.Combine(root, "SelectedLr2")); break;
                    case "install": dialog.SelectBmsInstallDirectoryAsync(alternate).GetAwaiter().GetResult(); break;
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(request, "candidate-switch-terminal");
                Assert.AreEqual(1, dialogs.ConfirmationCount);
                string prompt = dialogs.ConfirmationRequests.Single().MessageBoxText;
                StringAssert.Contains(prompt, Resources.Standalone_BMSDirectories.Trim().TrimEnd(':', '：'));
                Assert.IsFalse(prompt.Contains(Resources.Settings_lr2_detailed_paths, StringComparison.Ordinal), "今回入力する詳細パス自体を破棄対象にしません。");
                bool adopted = outcome == "accept" || (operation is "song" or "config") && (outcome is "busy" or "library-busy");
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.IsTrue(dialog.CanCancelBmtSettings, "無関係な候補は保持します。");
                if (adopted)
                {
                    CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, staged);
                    if (operation == "song") { Assert.AreEqual(song, dialog.LR2SongDBPath); }
                    if (operation == "config") { Assert.AreEqual(config, dialog.LR2ConfigXmlPath); }
                    if (operation == "install") { Assert.AreEqual(alternate, TestSettingsFactory.OpenSettings(settingsPath).BMSInstallDir); }
                    if (operation == "lr2-root")
                    {
                        Assert.AreEqual(config, values.LR2ConfigXmlPath);
                        Assert.IsTrue(dialog.Deficiencies.Any(item => item.Field == SettingsDeficiencyField.InstallDirectory), "新環境に導入先がなくても環境保存は成立し、環境変更の終端で作り直した不備の一覧で止めます。");
                    }
                }
                else
                {
                    CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(beforeXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                }
                held?.Dispose(); held = null;
                dialogs.ConfirmationObserved = null;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                if (!adopted || operation is "song" or "config") { CollectionAssert.AreEqual(before, File.ReadAllBytes(settingsPath)); }
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, held, null, primaryFailure); }
        });
    }
    /// <summary>LR2詳細の別DB候補を持つとき、BMS候補編集はL/Pを要求せず、出力確定だけ既存受付を使います。取消は候補を保ち、採用時だけ対象名を示した詳細候補を確定値へ戻します。</summary>
    [TestMethod]
    [DataRow("add", "cancel")]
    [DataRow("add", "accept")]
    [DataRow("add", "busy")]
    [DataRow("add", "library-busy")]
    [DataRow("remove", "cancel")]
    [DataRow("remove", "accept")]
    [DataRow("remove", "busy")]
    [DataRow("remove", "library-busy")]
    [DataRow("normal", "cancel")]
    [DataRow("normal", "accept")]
    [DataRow("normal", "busy")]
    public void ConflictingLr2Details_SwitchDiscardsOnlyWhenAdopted(string operation, string outcome)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            IDisposable? held = null;
            Exception? primaryFailure = null;
            try
            {
                string settingsPath = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(settingsPath));
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(outcome == "cancel" ? MessageBoxResult.Cancel : MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                (string song, _) = CreateValidLr2Layout(Path.Combine(root, "Draft"));
                dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), song).GetAwaiter().GetResult();
                dialog.EnableBeatorajaBmtOutput = true;
                string added = Directory.CreateDirectory(Path.Combine(root, "Added")).FullName;
                byte[] before = File.ReadAllBytes(settingsPath);
                byte[] xml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                if (outcome is "busy" or "library-busy")
                {
                    dialogs.ConfirmationObserved = _ =>
                    {
                        ChartFileOperationSynchronizer admission = outcome == "library-busy" ? composition.OperationAdmission : composition.PlaylistOperationAdmission;
                        Assert.IsTrue(admission.TryEnter(out IDisposable acquired));
                        held = acquired;
                    };
                }
                if (operation == "add") { dialog.AddBmsSearchRootPathsAsync([added]).GetAwaiter().GetResult(); }
                else if (operation == "remove") { dialog.RemoveBmsSearchRootPathAsync(values.BMSInstallDir).GetAwaiter().GetResult(); }
                else
                {
                    dialog.LR2CustomFolderOutputDir = Path.Combine(root, "SelectedOutput");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("normal"), "details-output-terminal");
                }
                Assert.AreEqual(1, dialogs.ConfirmationCount);
                StringAssert.Contains(dialogs.ConfirmationRequests.Single().MessageBoxText, Resources.Settings_lr2_detailed_paths);
                bool adopted = outcome == "accept" || operation != "normal" && (outcome is "busy" or "library-busy");
                Assert.AreEqual(adopted ? values.LR2SongDBPath : song, dialog.LR2SongDBPath);
                Assert.IsTrue(dialog.CanCancelBmtSettings);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                if (operation == "add") { Assert.AreEqual(adopted, dialog.AvailableBMSDirectories.Contains(added)); }
                if (operation == "remove") { Assert.AreEqual(!adopted, dialog.AvailableBMSDirectories.Contains(values.BMSInstallDir)); }
                if (!adopted || operation != "normal")
                {
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(xml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                }
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, held, null, primaryFailure); }
        });
    }

    /// <summary>今回の同値・不正入力だけで破棄確認へ進まず、元BMS候補と確定媒体を保ちます。出力は初期Binding相当の同値通知を対象とし、明示再選択の再保存とは分けます。</summary>
    [TestMethod]
    [DataRow("root")]
    [DataRow("song")]
    [DataRow("config")]
    [DataRow("install")]
    [DataRow("output")]
    public void ConflictingBmsCandidates_InvalidOrSameInputDoesNotAskOrDiscard(string operation)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            SettingsDialogHarness? harness = null;
            Exception? primaryFailure = null;
            try
            {
                string settingsPath = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(settingsPath));
                values.Save();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                harness = SettingsDialogHarness.Create(values, settingsEditSession: new SettingsEditSession(values), dialogs: dialogs);
                SettingsDialogViewModel dialog = harness.Dialog;
                string staged = Directory.CreateDirectory(Path.Combine(root, "Staged")).FullName;
                dialog.AddBmsSearchRootPathsAsync([staged]).GetAwaiter().GetResult();
                byte[] before = File.ReadAllBytes(settingsPath);
                switch (operation)
                {
                    case "root": TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectLr2RootAsync(Path.Combine(root, "Missing")), "invalid-root"); break;
                    case "song": dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), Path.Combine(root, "Missing", "song.db")).GetAwaiter().GetResult(); break;
                    case "config": dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), Path.Combine(root, "Missing", "config.xml")).GetAwaiter().GetResult(); break;
                    case "install": dialog.SelectBmsInstallDirectoryAsync(values.BMSInstallDir).GetAwaiter().GetResult(); break;
                    case "output": dialog.LR2CustomFolderOutputDir = values.LR2CustomFolderOutputBaseDir; break;
                }
                Assert.AreEqual(0, dialogs.ConfirmationCount);
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(settingsPath));
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(null, root, null, null, primaryFailure, harness); }
        });
    }
    /// <summary>事前生成した導入設定ページで、ルート確定の非先頭代替B、局所取消、公開前の実保存失敗、追加だけの非先頭A維持を、確定候補・fresh reader・実選択の一致で確認します。</summary>
    /// <remarks>候補通知の生産から同じcompiledページのBindingまで通し、選択の再代入や再生成で表示を補いません。媒体部分成功と公開後再読込みは既存BMS確定系列、独立選択の確認取消と未確定候補非漏出は次のcompiled系列へ分担します。</remarks>
    [TestMethod]
    [DataRow("replace")]
    [DataRow("cancel")]
    [DataRow("saveFailure")]
    [DataRow("add")]
    public void CompiledBmsRootConfirmation_UpdatesExistingInstallSelectionOrKeepsConfirmedState(string operation)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            Exception? primaryFailure = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                string original = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
                string firstAlternative = Directory.CreateDirectory(Path.Combine(root, "C")).FullName;
                string replacement = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
                string added = Directory.CreateDirectory(Path.Combine(root, "D")).FullName;
                string[] initialRoots = operation == "add" ? [firstAlternative, original, replacement] : [original, firstAlternative, replacement];
                Settings values = CreateValidStandaloneSettings(original, TestSettingsFactory.OpenSettings(path));
                values.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize(initialRoots);
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService();
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false,
                    reportSettingsApplyFailure: failures.Add, dialogs: dialogs);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                window = new SettingsWindow(dialogs) { DataContext = dialog };
                scope.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                navigation.SelectedItem = window.FindName("navigationInstall");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                ComboBox install = FindDescendants<ComboBox>(window).Single(item => item.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BMSInstallDir));
                CollectionAssert.AreEqual(initialRoots, install.Items.Cast<string>().ToArray());
                Assert.AreEqual(original, install.SelectedItem);

                (SettingsProperty Property, SettingsProvider Provider)[] observedProperties = new[]
                { nameof(Settings.StandaloneBmsRootPaths), nameof(Settings.BMSRootPath), nameof(Settings.BMSInstallDir) }
                    .Select(name => (values.Properties[name], values.Properties[name].Provider)).ToArray();
                var provider = new InstallAdmissionObservingSettingsProvider(observedProperties[0].Provider, () => { });
                foreach ((SettingsProperty Property, SettingsProvider Provider) item in observedProperties) { item.Property.Provider = provider; }
                try
                {
                    navigation.SelectedItem = window.FindName("navigationGeneral");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    if (operation == "add")
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AddBmsSearchRootPathsAsync([added]), "install-selection-add-root"); }
                    else
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.RemoveBmsSearchRootPathAsync(original), "install-selection-remove-root");
                        ComboBox replacementChoice = FindDescendants<ComboBox>(window).Single(item => item.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.ReplacementBmsInstallDir));
                        replacementChoice.SetCurrentValue(Selector.SelectedItemProperty, replacement);
                    }
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(0, provider.SaveCount, "候補編集だけでは保存しません。");
                    CollectionAssert.AreEqual(initialRoots, dialog.ConfirmedBMSDirectories.ToArray());
                    CollectionAssert.AreEqual(initialRoots, install.Items.Cast<string>().ToArray());
                    Assert.AreEqual(original, install.SelectedItem);
                    bool cancelled = operation == "cancel";
                    bool failed = operation == "saveFailure";
                    Button action = FindDescendants<Button>(window).Single(button => Equals(button.Tag, cancelled ? "cancel:bms" : "confirm:bms"));
                    Assert.IsTrue(action.IsEnabled);
                    using (failed ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                    {
                        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SettingsOperationCompletionTask, "install-selection-root-terminal");
                    }
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    string[] expectedRoots = cancelled || failed ? initialRoots
                        : operation == "add" ? [.. initialRoots, added] : [firstAlternative, replacement];
                    string expectedInstall = operation == "replace" ? replacement : original;
                    Settings saved = TestSettingsFactory.OpenSettings(path);
                    CollectionAssert.AreEqual(expectedRoots, StandaloneBmsRootPathSettings.Deserialize(saved.StandaloneBmsRootPaths, saved.BMSRootPath).ToArray());
                    CollectionAssert.AreEqual(expectedRoots, dialog.ConfirmedBMSDirectories.ToArray());
                    Assert.AreEqual(expectedInstall, saved.BMSInstallDir);
                    Assert.AreEqual(expectedInstall, dialog.BMSInstallDir);
                    Assert.AreEqual(expectedInstall, install.SelectedItem);
                    CollectionAssert.AreEqual(expectedRoots, install.Items.Cast<string>().ToArray());
                    CollectionAssert.AreEqual(failed ? new[] { firstAlternative, replacement } : expectedRoots, dialog.AvailableBMSDirectories);
                    Assert.AreEqual(failed ? replacement : null, dialog.ReplacementBmsInstallDir);
                    Assert.AreEqual(failed ? 1 : 0, failures.Count);
                    Assert.AreEqual(cancelled ? 0 : 1, provider.SaveCount, "確定の一回だけが保存を要求します。");

                    navigation.SelectedItem = window.FindName("navigationInstall");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    ComboBox redisplayed = FindDescendants<ComboBox>(window).Single(item => item.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BMSInstallDir));
                    Assert.AreSame(install, redisplayed, "生成済みの導入設定ページを再表示します。");
                    CollectionAssert.AreEqual(expectedRoots, redisplayed.Items.Cast<string>().ToArray());
                    Assert.AreEqual(expectedInstall, redisplayed.SelectedItem);
                    Assert.AreEqual(cancelled ? 0 : 1, provider.SaveCount, "失敗後の解放やページ再表示で保存を再試行しません。");
                    Assert.AreEqual(failed ? 1 : 0, failures.Count);
                }
                finally
                {
                    foreach ((SettingsProperty Property, SettingsProvider Provider) item in observedProperties) { item.Property.Provider = item.Provider; }
                }
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, window, primaryFailure); }
        });
    }

    /// <summary>独立導入先の実Bindingは確定BMSだけを候補にし、選択の確認取消では元候補・保存先・表示を保ち、承認時だけ当該項目を保存します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CompiledInstallSelection_UsesConfirmedRootsAndConfirmsBmsDiscard(bool accepted)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            Exception? primaryFailure = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(path));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                LR2Config xml = TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath);
                xml.AddBMSSearchDirectories([alternate]); xml.Save(xml.ConfigFilePath);
                values.Save();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(accepted ? MessageBoxResult.OK : MessageBoxResult.Cancel) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                window = new SettingsWindow(dialogs) { DataContext = dialog };
                scope.ShowAndWaitForContentRendered(window);
                string staged = Directory.CreateDirectory(Path.Combine(root, "Staged")).FullName;
                dialog.AddBmsSearchRootPathsAsync([staged]).GetAwaiter().GetResult();
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationInstall");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                ComboBox combo = FindDescendants<ComboBox>(window).Single(item => item.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BMSInstallDir));
                CollectionAssert.Contains(combo.Items.Cast<string>().ToArray(), alternate);
                CollectionAssert.DoesNotContain(combo.Items.Cast<string>().ToArray(), staged);
                Assert.IsTrue(combo.IsEnabled);
                bool enabledDuringAcceptedOperation = false;
                System.ComponentModel.PropertyChangedEventHandler availabilityChanged = (_, change) =>
                {
                    if (change.PropertyName == nameof(SettingsDialogViewModel.IsBmsInstallDirectorySelectionEnabled)
                        && !dialog.IsSettingsOperationAvailable && dialog.IsBmsInstallDirectorySelectionEnabled)
                    { enabledDuringAcceptedOperation = true; }
                };
                dialog.PropertyChanged += availabilityChanged;
                try
                {
                    combo.SetCurrentValue(Selector.SelectedItemProperty, alternate);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "install-selection-terminal");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(1, dialogs.ConfirmationCount);
                    Assert.AreEqual(accepted ? alternate : original, TestSettingsFactory.OpenSettings(path).BMSInstallDir);
                    Assert.AreEqual(accepted ? alternate : original, combo.SelectedItem);
                    Assert.AreEqual(!accepted, dialog.AvailableBMSDirectories.Contains(staged));
                    CollectionAssert.DoesNotContain(TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), staged);
                    Assert.IsTrue(combo.IsEnabled);
                    Assert.AreEqual(accepted, enabledDuringAcceptedOperation, "採用点の候補破棄だけでも可否を通知し、取消では候補を保持します。");
                }
                finally { dialog.PropertyChanged -= availabilityChanged; }
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, window, primaryFailure); }
        });
    }
    /// <summary>確認中に導入先の確定値が変わった場合、出力採用点で新しい導入先保護を再検査し、BMS候補を破棄せず出力媒体を保持します。</summary>
    [TestMethod]
    public void ConflictingCandidates_OutputRechecksLatestInstallBeforeDiscard()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            Exception? primaryFailure = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(path));
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                LR2Config xml = TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath);
                xml.AddBMSSearchDirectories([alternate]); xml.Save(xml.ConfigFilePath);
                values.Save();
                byte[] beforeXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                string originalOutput = values.LR2CustomFolderOutputBaseDir;
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                string staged = Directory.CreateDirectory(Path.Combine(root, "Staged")).FullName;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AddBmsSearchRootPathsAsync([staged]), "latest-install-bms-input");
                dialog.LR2CustomFolderOutputDir = alternate;
                dialogs.ConfirmationObserved = _ => values.Commit((nameof(Settings.BMSInstallDir), alternate));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("normal"), "latest-install-output-terminal");
                Assert.AreEqual(1, dialogs.ConfirmationCount);
                Assert.AreEqual(originalOutput, TestSettingsFactory.OpenSettings(path).LR2CustomFolderOutputBaseDir);
                Assert.AreEqual(alternate, TestSettingsFactory.OpenSettings(path).BMSInstallDir);
                Assert.AreEqual(originalOutput, dialog.LR2CustomFolderOutputDir);
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
                CollectionAssert.AreEqual(beforeXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, alternate));
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, null, primaryFailure); }
        });
    }
    /// <summary>
    /// 独立導入先はL中に拒否し、解放だけでは保存せず次の明示選択を待ちます。Pや無関係Window操作中の普通保存を保ちます。BMS破棄確認だけは設定担当の単一所有に従って無効表示と入口拒否を揃え、普通保存・同じTask・元候補と終端後再操作を保ちます。
    /// Lの使用中による拒否だけが受付使用中の警告を一度表示し、P・Window操作中や受理では警告しないことを確認します。
    /// </summary>
    [TestMethod]
    [DataRow("library", false)]
    [DataRow("playlist", true)]
    [DataRow("window", false)]
    [DataRow("window", true)]
    public void CompiledInstallSelection_PreservesOrdinarySaveAndSingleConfirmationOwnership(string busy, bool hasDraft)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            IDisposable? held = null;
            Task? otherOperation = null;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? primaryFailure = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(path));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                LR2Config xml = TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath);
                xml.AddBMSSearchDirectories([alternate]); xml.Save(xml.ConfigFilePath);
                values.Save();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                var failures = new List<Exception>();
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                window = new SettingsWindow(dialogs) { DataContext = dialog };
                scope.ShowAndWaitForContentRendered(window);
                string staged = Directory.CreateDirectory(Path.Combine(root, "Staged")).FullName;
                if (hasDraft) { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AddBmsSearchRootPathsAsync([staged]), "independent-install-bms-input"); }
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationInstall");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                ComboBox combo = FindDescendants<ComboBox>(window).Single(item => item.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BMSInstallDir));
                Assert.IsTrue(combo.IsEnabled, "設定実操作を受け付けられる間は候補確認付きの選択も可能です。");
                if (busy == "window") { otherOperation = window.RunViewOperationAsync(() => release.Task); }
                else
                {
                    ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
                    ChartFileOperationSynchronizer admission = busy == "library" ? composition.OperationAdmission : composition.PlaylistOperationAdmission;
                    Assert.IsTrue(admission.TryEnter(out IDisposable acquired)); held = acquired;
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                bool rejected = busy == "library" || busy == "window" && hasDraft;
                Assert.AreEqual(!(busy == "window" && hasDraft), combo.IsEnabled, "設定busyでも確認不要の同期保存は有効に保ちます。");
                Task originalCompletion = owner.SettingDialog.SettingsOperationCompletionTask;
                combo.SetCurrentValue(Selector.SelectedItemProperty, alternate);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(rejected ? original : alternate, TestSettingsFactory.OpenSettings(path).BMSInstallDir, string.Join(Environment.NewLine, failures));
                Assert.AreEqual(rejected ? original : alternate, combo.SelectedItem);
                Assert.AreEqual(hasDraft && rejected, dialog.AvailableBMSDirectories.Contains(staged));
                Assert.AreEqual(hasDraft && !rejected ? 1 : 0, dialogs.ConfirmationCount);
                Assert.AreEqual(busy == "library" ? 1 : 0, CountBusyWarnings(dialogs), "Lの使用中による拒否だけを一度警告します。");
                if (busy == "library")
                {
                    dialog.StartupSortCachePrewarmPriority = 3;
                    ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationGeneral");
                    ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationInstall");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    held?.Dispose(); held = null;
                    Assert.AreEqual(original, TestSettingsFactory.OpenSettings(path).BMSInstallDir);
                    combo.SetCurrentValue(Selector.SelectedItemProperty, alternate);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(alternate, TestSettingsFactory.OpenSettings(path).BMSInstallDir, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(3, TestSettingsFactory.OpenSettings(path).StartupSortCachePrewarmPriority);
                    Assert.AreEqual(1, CountBusyWarnings(dialogs), "解放後の明示選択は警告しません。");
                }
                if (busy == "window")
                {
                    Assert.AreSame(originalCompletion, owner.SettingDialog.SettingsOperationCompletionTask);
                    Assert.IsFalse(originalCompletion.IsCompleted, "普通の保存も確認拒否も別操作Taskを置き換えません。");
                    Assert.IsTrue(dialog.IsEditCancellationEnabled, "通常閉鎖の可否と確認付き項目の単一受付を分けます。");
                    release.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(otherOperation ?? throw new AssertFailedException("window operation missing"), "independent-install-other-operation");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsTrue(combo.IsEnabled, "実終端後は候補確認付きの明示選択も再開します。");
                    if (rejected)
                    {
                        combo.SetCurrentValue(Selector.SelectedItemProperty, alternate);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "independent-install-explicit-retry");
                        Assert.AreEqual(1, dialogs.ConfirmationCount);
                        Assert.AreEqual(alternate, TestSettingsFactory.OpenSettings(path).BMSInstallDir, string.Join(Environment.NewLine, failures));
                        CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, staged);
                    }
                }
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                release.TrySetResult();
                if (otherOperation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(otherOperation, "independent-install-operation-cleanup"); }
                CompleteCandidateFixture(owner, root, held, window, primaryFailure);
            }
        });
    }

    /// <summary>
    /// 実ライブラリのLと同じ受付で独立選択を拒否し、受付使用中の警告（本文・題名・Exclamation・OK）を一度だけ表示します。
    /// 解放だけでは保存も再警告もせず、次の明示入力だけを警告なしで保存します。続く実root登録解除でも新導入先と無関係候補を保持します。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IndependentInstallSelection_LibraryAdmissionPreservesValuesUntilExplicitRetry(bool lr2)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            TestBmsLibrary? library = null;
            IDisposable? held = null;
            Exception? primaryFailure = null;
            try
            {
                string config = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(config));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                if (lr2) { LR2Config xml = TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath); xml.AddBMSSearchDirectories([alternate]); xml.Save(xml.ConfigFilePath); }
                else { values.OperationModeLR2DB = false; values.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([original, alternate]); }
                values.Save();
                var dialogs = new RecordingRootDialogService();
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs);
                library = MainWindowViewModelTestFactory.CreateLibrary(MainWindowViewModelTestFactory.GetComposition(owner ?? throw new AssertFailedException()).DatabaseScope, values.LR2SongDBPath, values, owner);
                held = library.Lr2Synchronization.TryBeginMutation("independent-install-test");
                Assert.IsNotNull(held);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                dialog.EnableBeatorajaBmtOutput = !values.EnableBeatorajaBmtOutput;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(alternate), "independent-install-busy");
                Assert.AreEqual(original, values.BMSInstallDir);
                Assert.AreEqual(original, TestSettingsFactory.OpenSettings(config).BMSInstallDir);
                Assert.AreEqual(original, dialog.BMSInstallDir, "拒否後の選択表示は保存済みの値へ戻ります。");
                Assert.IsTrue(dialog.CanCancelBmtSettings);
                AssertSingleBusyWarning(dialogs);
                Assert.AreEqual(0, dialogs.ConfirmationCount);
                held.Dispose(); held = null;
                Assert.AreEqual(original, TestSettingsFactory.OpenSettings(config).BMSInstallDir);
                Assert.AreEqual(1, dialogs.MessageCount, "解放だけでは再警告しません。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(alternate), "independent-install-explicit");
                Assert.AreEqual(alternate, TestSettingsFactory.OpenSettings(config).BMSInstallDir);
                Assert.AreEqual(1, dialogs.MessageCount, "次の明示選択は警告なしで保存します。");
                Assert.IsTrue(dialog.CanCancelBmtSettings);
                string chartPath = Path.Combine(original, "root-delete.bms");
                File.WriteAllText(chartPath, "#TITLE Removed root");
                ChartFile chart = ChartTestValues.Empty() with { Path = chartPath, Md5 = new string('a', 32) };
                library.BmsCharts = [chart]; library.BmsonCharts = [];
                new BmsLibraryDbGateway(MainWindowViewModelTestFactory.GetComposition(owner ?? throw new AssertFailedException()).DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).UpsertSongs([chart]);
                LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                    library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(chart)]), false, [original], new LibraryOperationNotices());
                Assert.AreEqual(1, removal.ConfirmedChartCount);
                Assert.IsNull(removal.RegistrationFailure);
                Assert.IsFalse(Directory.Exists(original));
                Assert.AreEqual(alternate, TestSettingsFactory.OpenSettings(config).BMSInstallDir,
                    "登録解除は独立選択で成立した導入先を古い値へ戻しません。");
                CollectionAssert.AreEqual(new[] { alternate }, dialog.ConfirmedBMSDirectories.ToArray());
                Assert.IsTrue(dialog.CanCancelBmtSettings);
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                held?.Dispose();
                library?.RequestShutdown("independent-install-test");
                CompleteCandidateFixture(owner, root, null, null, primaryFailure);
            }
        });
    }

    /// <summary>
    /// BMS候補の破棄確認が必要な独立導入先の選択で、確認前にLが使用中なら確認を出さず警告を一度だけ表示します（予約なしの事前検査）。
    /// 確認中にLを取られた場合は確認一回の後に警告を一度だけ表示します。どちらも保存せず、BMS候補と選択表示を保ち、
    /// 解放だけでは何も実行せず、次の明示選択が警告なしで保存されることを確認します。答えても進めない確認・黙った拒否・候補の破棄・予約による自動保存を検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IndependentInstallSelection_BusyAroundCandidateConfirmationWarnsOnceWithoutSaving(bool takenDuringConfirmation)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            IDisposable? held = null;
            Exception? primaryFailure = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(path));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                LR2Config xml = TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath);
                xml.AddBMSSearchDirectories([alternate]); xml.Save(xml.ConfigFilePath);
                values.Save();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                string staged = Directory.CreateDirectory(Path.Combine(root, "Staged")).FullName;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AddBmsSearchRootPathsAsync([staged]), "independent-install-bms-candidate");
                Assert.IsTrue(dialog.RequiresBmsInstallDirectoryConfirmation);
                ChartFileOperationSynchronizer admission = MainWindowViewModelTestFactory.GetComposition(owner).OperationAdmission;
                if (takenDuringConfirmation)
                {
                    dialogs.ConfirmationObserved = _ =>
                    {
                        Assert.IsTrue(admission.TryEnter(out IDisposable acquired), "確認前の検査はLを予約しません。");
                        held = acquired;
                    };
                }
                else
                {
                    Assert.IsTrue(admission.TryEnter(out IDisposable acquired));
                    held = acquired;
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(alternate), "independent-install-confirmation-busy");
                Assert.AreEqual(takenDuringConfirmation ? 1 : 0, dialogs.ConfirmationCount, "使用中を確認前に知っている場合は確認を出しません。");
                AssertSingleBusyWarning(dialogs);
                Assert.AreEqual(original, TestSettingsFactory.OpenSettings(path).BMSInstallDir);
                Assert.AreEqual(original, dialog.BMSInstallDir, "拒否後の選択表示は保存済みの値へ戻ります。");
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged, "拒否ではBMS候補を破棄しません。");
                dialogs.ConfirmationObserved = null;
                Assert.IsNotNull(held);
                held.Dispose(); held = null;
                Assert.AreEqual(original, TestSettingsFactory.OpenSettings(path).BMSInstallDir, "解放だけでは保存しません。");
                Assert.AreEqual(1, dialogs.MessageCount, "解放だけでは再警告しません。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(alternate), "independent-install-confirmation-retry");
                Assert.AreEqual(alternate, TestSettingsFactory.OpenSettings(path).BMSInstallDir);
                Assert.AreEqual(1, dialogs.MessageCount, "次の明示選択は警告なしで保存します。");
                Assert.AreEqual(takenDuringConfirmation ? 2 : 1, dialogs.ConfirmationCount);
                CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, staged);
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                held?.Dispose();
                CompleteCandidateFixture(owner, root, null, null, primaryFailure);
            }
        });
    }

    private static int CountBusyWarnings(RecordingRootDialogService dialogs)
        => dialogs.MessageRequests.Count(request => request.MessageBoxText == Resources.Warn_LibraryOperationBusy);

    private static void AssertSingleBusyWarning(RecordingRootDialogService dialogs)
    {
        UiMessageRequest warning = dialogs.MessageRequests.Single();
        Assert.AreEqual(Resources.Warn_LibraryOperationBusy, warning.MessageBoxText);
        Assert.AreEqual(Resources.Warning, warning.Caption);
        Assert.AreEqual(MessageBoxImage.Exclamation, warning.Icon);
        Assert.AreEqual(MessageBoxButton.OK, warning.Button);
    }

    /// <summary>実providerの同期保存前後でも同じLを保有し、成功・実媒体失敗の両終端で解放します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IndependentInstallSelection_SaveHoldsLibraryAdmissionAndReleasesAfterFailure(bool failSave)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            Exception? primaryFailure = null;
            try
            {
                string config = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(config));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                values.OperationModeLR2DB = false;
                values.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([original, alternate]); values.Save();
                var failures = new List<Exception>();
                ChartFileOperationSynchronizer? admission = null;
                bool reportedWithLibraryHeld = false;
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, reportSettingsApplyFailure: failure =>
                { failures.Add(failure); reportedWithLibraryHeld = admission?.IsActive == true; });
                admission = MainWindowViewModelTestFactory.GetComposition(owner).OperationAdmission;
                SettingsProperty property = values.Properties[nameof(Settings.BMSInstallDir)];
                SettingsProvider originalProvider = property.Provider;
                var observations = new List<bool>();
                var provider = new InstallAdmissionObservingSettingsProvider(originalProvider, () =>
                {
                    bool acquired = admission.TryEnter(out IDisposable lease);
                    lease?.Dispose();
                    observations.Add(acquired);
                });
                property.Provider = provider;
                try
                {
                    using (failSave ? File.Open(config, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SelectBmsInstallDirectoryAsync(alternate), "independent-install-save"); }
                }
                finally { property.Provider = originalProvider; }
                CollectionAssert.AreEqual(new[] { false, false }, observations);
                Assert.AreEqual(failSave ? 1 : 0, failures.Count);
                Assert.IsFalse(reportedWithLibraryHeld, "失敗案内は短い保存区間のL解放後です。");
                Assert.AreEqual(failSave ? original : alternate, values.BMSInstallDir);
                Assert.AreEqual(failSave ? original : alternate, TestSettingsFactory.OpenSettings(config).BMSInstallDir);
                Assert.IsTrue(admission.TryEnter(out IDisposable fresh)); fresh.Dispose();
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, null, primaryFailure); }
        });
    }

    /// <summary>確認中に確定一覧が更新されても、実L取得後の最終検証で外れた先を拒否し、関係候補を先回り破棄しません。</summary>
    [TestMethod]
    public void IndependentInstallSelection_RevalidatesConfirmedRootsBeforeDiscard()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            Exception? primaryFailure = null;
            try
            {
                string config = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(config));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                string staged = Directory.CreateDirectory(Path.Combine(root, "Staged")).FullName;
                values.OperationModeLR2DB = false;
                values.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([original, alternate]); values.Save();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AddBmsSearchRootPathsAsync([staged]), "independent-install-staged");
                dialog.EnableBeatorajaBmtOutput = !values.EnableBeatorajaBmtOutput;
                ChartFileOperationSynchronizer admission = MainWindowViewModelTestFactory.GetComposition(owner).OperationAdmission;
                dialogs.ConfirmationObserved = _ =>
                {
                    Assert.IsTrue(admission.TryEnter(out IDisposable lease));
                    using (lease) { new BmsRootRegistrationStore(values, () => null).SaveUserRoots([original]); }
                };
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(alternate), "independent-install-latest-input");
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
                Assert.IsTrue(dialog.CanCancelBmtSettings);
                Assert.AreEqual(original, values.BMSInstallDir);
                Assert.AreEqual(original, TestSettingsFactory.OpenSettings(config).BMSInstallDir);
                dialogs.ConfirmationObserved = null;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(staged), "independent-install-invalid-candidate");
                Assert.AreEqual(1, dialogs.ConfirmationCount);
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, null, primaryFailure); }
        });
    }

    /// <summary>候補切替の専用媒体・Window・ownerを回収し、本体の失敗を後片付けの失敗で上書きしません。</summary>
    private static void CompleteCandidateFixture(MainWindowViewModel? owner, string root, IDisposable? held, SettingsWindow? window, Exception? primaryFailure, SettingsDialogHarness? harness = null)
    {
        var failures = new List<Exception>();
        void Cleanup(Action action) { try { action(); } catch (Exception failure) { failures.Add(failure); } }
        if (held != null) { Cleanup(held.Dispose); }
        if (harness != null) { Cleanup(harness.Dispose); }
        if (window != null)
        {
            Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(owner?.SettingDialog.SettingsOperationCompletionTask ?? Task.CompletedTask, "candidate-window-cleanup"));
            Cleanup(window.CloseForOwnerShutdown);
        }
        if (owner != null)
        {
            Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "candidate-owner-cleanup"));
            Cleanup(owner.SettingDialog.Dispose);
        }
        Cleanup(() => Directory.Delete(root, recursive: true));
        if (failures.Count == 0) { return; }
        var cleanupFailure = new AggregateException("candidate fixture cleanup", failures);
        if (primaryFailure == null) { throw cleanupFailure; }
        Console.WriteLine(cleanupFailure);
    }
}
