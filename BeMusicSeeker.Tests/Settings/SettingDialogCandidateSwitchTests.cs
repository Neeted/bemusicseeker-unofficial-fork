using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
    /// <summary>BMS候補から競合操作へ切り替える通常入口で、取消・承認・実確定のP先着・候補編集のL/P非要求を分け、対象範囲名・候補保持と保存副作用を確認します。無関係なBMT候補は全経路で保持します。</summary>
    /// <remarks>条件網羅はWindowを使わず実専用providerと既存受付で行い、pickerとBindingはcompiled系列へ分担します。</remarks>
    [DataTestMethod]
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
    public void ConflictingBmsCandidates_SwitchOnlyAfterValidAcceptedOperation(string operation, string outcome)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            IDisposable? held = null;
            Exception? primaryFailure = null;
            try
            {
                string settingsPath = Path.Combine(root, "settings", "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
                string alternate = Directory.CreateDirectory(Path.Combine(root, "AlternateSongs")).FullName;
                var xml = new LR2Config(values.LR2ConfigXmlPath);
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
                    case "additional": dialog.AddCustomFolderAdditionalOutputBaseDir(Path.Combine(root, "NewAdditional")); request = dialog.CommitOutputSettingsAsync("additional"); break;
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
                    if (operation == "install") { Assert.AreEqual(alternate, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BMSInstallDir); }
                    if (operation == "lr2-root")
                    {
                        Assert.AreEqual(config, values.LR2ConfigXmlPath);
                        Assert.IsFalse(dialog.CheckValidationForInitialization(out _), "新環境に導入先がなくても環境保存は成立し、利用開始前検査で止めます。");
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
    [DataTestMethod]
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
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            IDisposable? held = null;
            Exception? primaryFailure = null;
            try
            {
                string settingsPath = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
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

    /// <summary>今回の同値・不正入力だけで破棄確認へ進まず、元BMS候補と確定媒体を保ちます。確認承認を入力の採用と取り違える経路を防ぎます。</summary>
    [DataTestMethod]
    [DataRow("root")]
    [DataRow("song")]
    [DataRow("config")]
    [DataRow("install")]
    [DataRow("output")]
    public void ConflictingBmsCandidates_InvalidOrSameInputDoesNotAskOrDiscard(string operation)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            Exception? primaryFailure = null;
            try
            {
                string settingsPath = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
                values.Save();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                string staged = Directory.CreateDirectory(Path.Combine(root, "Staged")).FullName;
                dialog.AddBmsSearchRootPathsAsync([staged]).GetAwaiter().GetResult();
                byte[] before = File.ReadAllBytes(settingsPath);
                switch (operation)
                {
                    case "root": TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectLr2RootAsync(Path.Combine(root, "Missing")), "invalid-root"); break;
                    case "song": dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), Path.Combine(root, "Missing", "song.db")).GetAwaiter().GetResult(); break;
                    case "config": dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), Path.Combine(root, "Missing", "config.xml")).GetAwaiter().GetResult(); break;
                    case "install": dialog.SelectBmsInstallDirectoryAsync(values.BMSInstallDir).GetAwaiter().GetResult(); break;
                    case "output": TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("normal"), "same-output"); break;
                }
                Assert.AreEqual(0, dialogs.ConfirmationCount);
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(settingsPath));
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, null, primaryFailure); }
        });
    }
    /// <summary>独立導入先の実Bindingは確定BMSだけを候補にし、選択の確認取消では元候補・保存先・表示を保ち、承認時だけ当該項目を保存します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CompiledInstallSelection_UsesConfirmedRootsAndConfirmsBmsDiscard(bool accepted)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            Exception? primaryFailure = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(path));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                var xml = new LR2Config(values.LR2ConfigXmlPath);
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
                    Assert.AreEqual(accepted ? alternate : original, PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
                    Assert.AreEqual(accepted ? alternate : original, combo.SelectedItem);
                    Assert.AreEqual(!accepted, dialog.AvailableBMSDirectories.Contains(staged));
                    CollectionAssert.DoesNotContain(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), staged);
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
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            Exception? primaryFailure = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(path));
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                var xml = new LR2Config(values.LR2ConfigXmlPath);
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
                Assert.AreEqual(originalOutput, PortableSettingsPersistenceTests.OpenSettings(path).LR2CustomFolderOutputBaseDir);
                Assert.AreEqual(alternate, PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
                Assert.AreEqual(originalOutput, dialog.LR2CustomFolderOutputDir);
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
                CollectionAssert.AreEqual(beforeXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                Assert.IsFalse(dialog.IsOutputSaveIncomplete);
                StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, alternate));
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, null, primaryFailure); }
        });
    }
    /// <summary>独立導入先は無関係なL/PやWindow操作中も普通の項目保存を行います。BMS破棄確認だけは設定担当の単一所有に従って無効表示と入口拒否を揃え、普通保存・同じTask・元候補と終端後再操作を保ちます。</summary>
    [DataTestMethod]
    [DataRow("library", false)]
    [DataRow("playlist", true)]
    [DataRow("window", false)]
    [DataRow("window", true)]
    public void CompiledInstallSelection_PreservesOrdinarySaveAndSingleConfirmationOwnership(string busy, bool hasDraft)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            IDisposable? held = null;
            Task? otherOperation = null;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? primaryFailure = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(path));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                var xml = new LR2Config(values.LR2ConfigXmlPath);
                xml.AddBMSSearchDirectories([alternate]); xml.Save(xml.ConfigFilePath);
                values.Save();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs);
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
                bool rejected = busy == "window" && hasDraft;
                Assert.AreEqual(!rejected, combo.IsEnabled, "設定busyでも確認不要の同期保存は有効に保ちます。");
                Task originalCompletion = owner.SettingDialog.SettingsOperationCompletionTask;
                combo.SetCurrentValue(Selector.SelectedItemProperty, alternate);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(rejected ? original : alternate, PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
                Assert.AreEqual(rejected ? original : alternate, combo.SelectedItem);
                Assert.AreEqual(rejected, dialog.AvailableBMSDirectories.Contains(staged));
                Assert.AreEqual(hasDraft && !rejected ? 1 : 0, dialogs.ConfirmationCount);
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
                        Assert.AreEqual(alternate, PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
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

    /// <summary>候補切替の専用媒体・Window・ownerを回収し、本体の失敗を後片付けの失敗で上書きしません。</summary>
    private static void CompleteCandidateFixture(MainWindowViewModel? owner, string root, IDisposable? held, SettingsWindow? window, Exception? primaryFailure)
    {
        var failures = new List<Exception>();
        void Cleanup(Action action) { try { action(); } catch (Exception failure) { failures.Add(failure); } }
        if (held != null) { Cleanup(held.Dispose); }
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
