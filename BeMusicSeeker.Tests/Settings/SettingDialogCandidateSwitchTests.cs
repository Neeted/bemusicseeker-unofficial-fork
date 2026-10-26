using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
    [DataRow("install", "library-busy")]
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

    /// <summary>今回の同値・不正入力だけで破棄確認へ進まず、元BMS候補と確定媒体を保ちます。出力は初期Binding相当の同値通知を対象とし、明示再選択の再保存とは分けます。</summary>
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
                    case "output": dialog.LR2CustomFolderOutputDir = values.LR2CustomFolderOutputBaseDir; break;
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
                StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, alternate));
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, null, primaryFailure); }
        });
    }
    /// <summary>独立導入先はL中に拒否し、解放だけでは保存せず次の明示選択を待ちます。Pや無関係Window操作中の普通保存を保ちます。BMS破棄確認だけは設定担当の単一所有に従って無効表示と入口拒否を揃え、普通保存・同じTask・元候補と終端後再操作を保ちます。</summary>
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
                bool rejected = busy == "library" || busy == "window" && hasDraft;
                Assert.AreEqual(!(busy == "window" && hasDraft), combo.IsEnabled, "設定busyでも確認不要の同期保存は有効に保ちます。");
                Task originalCompletion = owner.SettingDialog.SettingsOperationCompletionTask;
                combo.SetCurrentValue(Selector.SelectedItemProperty, alternate);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(rejected ? original : alternate, PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
                Assert.AreEqual(rejected ? original : alternate, combo.SelectedItem);
                Assert.AreEqual(hasDraft && rejected, dialog.AvailableBMSDirectories.Contains(staged));
                Assert.AreEqual(hasDraft && !rejected ? 1 : 0, dialogs.ConfirmationCount);
                if (busy == "library")
                {
                    dialog.StartupSortCachePrewarmPriority = 3;
                    ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationGeneral");
                    ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationInstall");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    held?.Dispose(); held = null;
                    Assert.AreEqual(original, PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
                    combo.SetCurrentValue(Selector.SelectedItemProperty, alternate);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(alternate, PortableSettingsPersistenceTests.OpenSettings(path).BMSInstallDir);
                    Assert.AreEqual(3, PortableSettingsPersistenceTests.OpenSettings(path).StartupSortCachePrewarmPriority);
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

    /// <summary>実ライブラリのLと同じ受付で独立選択を拒否し、次の明示入力だけを保存します。続く実root登録解除でも新導入先と無関係候補を保持します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IndependentInstallSelection_LibraryAdmissionPreservesValuesUntilExplicitRetry(bool lr2)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            TestBmsLibrary? library = null;
            IDisposable? held = null;
            Exception? primaryFailure = null;
            try
            {
                string config = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(config));
                string original = values.BMSInstallDir;
                string alternate = Directory.CreateDirectory(Path.Combine(root, "Alternate")).FullName;
                if (lr2) { var xml = new LR2Config(values.LR2ConfigXmlPath); xml.AddBMSSearchDirectories([alternate]); xml.Save(xml.ConfigFilePath); }
                else { values.OperationModeLR2DB = false; values.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([original, alternate]); }
                values.Save();
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: new RecordingRootDialogService());
                library = MainWindowViewModelTestFactory.CreateLibrary(values.LR2SongDBPath, values, owner);
                held = library.TryBeginLibraryFileMutation("independent-install-test", showMessage: false);
                Assert.IsNotNull(held);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                dialog.EnableBeatorajaBmtOutput = !values.EnableBeatorajaBmtOutput;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(alternate), "independent-install-busy");
                Assert.AreEqual(original, values.BMSInstallDir);
                Assert.AreEqual(original, PortableSettingsPersistenceTests.OpenSettings(config).BMSInstallDir);
                Assert.IsTrue(dialog.CanCancelBmtSettings);
                held.Dispose(); held = null;
                Assert.AreEqual(original, PortableSettingsPersistenceTests.OpenSettings(config).BMSInstallDir);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(alternate), "independent-install-explicit");
                Assert.AreEqual(alternate, PortableSettingsPersistenceTests.OpenSettings(config).BMSInstallDir);
                Assert.IsTrue(dialog.CanCancelBmtSettings);
                string chartPath = Path.Combine(original, "root-delete.bms");
                File.WriteAllText(chartPath, "#TITLE Removed root");
                ChartFile chart = ChartTestValues.Empty() with { Path = chartPath, Md5 = new string('a', 32) };
                library.BmsCharts = [chart]; library.BmsonCharts = [];
                new BmsLibraryDbGateway(values.LR2SongDBPath).UpsertSongs([chart]);
                LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                    library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(chart)]), false, [original]);
                Assert.AreEqual(1, removal.ConfirmedChartCount);
                Assert.IsNull(removal.RegistrationFailure);
                Assert.IsFalse(Directory.Exists(original));
                Assert.AreEqual(alternate, PortableSettingsPersistenceTests.OpenSettings(config).BMSInstallDir,
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

    /// <summary>実providerの同期保存前後でも同じLを保有し、成功・実媒体失敗の両終端で解放します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IndependentInstallSelection_SaveHoldsLibraryAdmissionAndReleasesAfterFailure(bool failSave)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            Exception? primaryFailure = null;
            try
            {
                string config = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(config));
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
                Assert.AreEqual(failSave ? original : alternate, PortableSettingsPersistenceTests.OpenSettings(config).BMSInstallDir);
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
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            Exception? primaryFailure = null;
            try
            {
                string config = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(config));
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
                Assert.AreEqual(original, PortableSettingsPersistenceTests.OpenSettings(config).BMSInstallDir);
                dialogs.ConfirmationObserved = null;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(staged), "independent-install-invalid-candidate");
                Assert.AreEqual(1, dialogs.ConfirmationCount);
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally { CompleteCandidateFixture(owner, root, null, null, primaryFailure); }
        });
    }

    /// <summary>保存要求を実providerへ一回委譲し、元例外と保存結果をそのまま返す局所観測です。</summary>
    private sealed class InstallAdmissionObservingSettingsProvider : System.Configuration.SettingsProvider
    {
        private readonly System.Configuration.SettingsProvider inner;
        private readonly Action observe;
        internal InstallAdmissionObservingSettingsProvider(System.Configuration.SettingsProvider inner, Action observe)
        {
            this.inner = inner; this.observe = observe;
            Initialize(nameof(InstallAdmissionObservingSettingsProvider), new System.Collections.Specialized.NameValueCollection());
        }
        public override string ApplicationName { get => inner.ApplicationName; set => inner.ApplicationName = value; }
        public override System.Configuration.SettingsPropertyValueCollection GetPropertyValues(System.Configuration.SettingsContext context, System.Configuration.SettingsPropertyCollection properties)
            => inner.GetPropertyValues(context, properties);
        public override void SetPropertyValues(System.Configuration.SettingsContext context, System.Configuration.SettingsPropertyValueCollection values)
        {
            observe();
            try { inner.SetPropertyValues(context, values); }
            finally { observe(); }
        }
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
