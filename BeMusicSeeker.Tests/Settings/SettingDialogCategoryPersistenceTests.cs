using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
    /// <summary>実改名Bindingは途中の空白を保ち、同値の明示改名でも保存せず前後空白を除去します。別ページの通常保存は候補を保ち、通常Closeだけが未確定名を最新公開名へ戻し、同VM再表示後の明示改名にも混入させません。</summary>
    /// <remarks>実表・生成先はOutputConfirmation_HeaderFailureUsesPublishedNamesOrFreshSavedDefinitions、受理済みTaskのClose時の継続はRunViewOperation_NormalCloseAndEscapePreserveTaskAcrossReopenへ分担し、このWindowは生入力と確定・破棄の接続を確認します。</remarks>
    [TestMethod]
    public void CompiledAdditionalOutputRename_PreservesInputSpacesUntilConfirmation()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            string original = Path.Combine(root, "Original");
            Settings values = CreateValidCustomFolderSettings(root, [original], PortableSettingsPersistenceTests.OpenSettings(path));
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([original]);
            values.Save();
            string oldNormal = values.LR2CustomFolderOutputBaseDir;
            string oldRoot = values.LR2CustomFolderOutputBaseDirRootType;
            string savedAdditional = values.LR2CustomFolderAdditionalOutputBaseDirs;
            byte[] previousSettings = File.ReadAllBytes(path);
            byte[] previousConfig = File.ReadAllBytes(values.LR2ConfigXmlPath);
            SettingsProperty[] observedProperties = values.Properties.Cast<SettingsProperty>().ToArray();
            SettingsProvider originalProvider = values.Properties[nameof(Settings.LR2CustomFolderOutputBaseDir)].Provider;
            int providerWriteNotifications = 0;
            var observingProvider = new InstallAdmissionObservingSettingsProvider(originalProvider,
                () => Interlocked.Increment(ref providerWriteNotifications));
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            var failures = new List<Exception>();
            MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: true, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
            SettingsWindow? window = null;
            SettingsWindow? reopened = null;
            try
            {
                // 直接Persistする出力確定と通常Saveの両方を、専用providerへの実書込み要求で観測します。
                foreach (SettingsProperty property in observedProperties) { property.Provider = observingProvider; }
                SettingsDialogViewModel dialog = owner.SettingDialog;
                var presentation = new WindowClosingPresentationPort();
                dialog.AttachPresentationPort(presentation);
                window = new SettingsWindow(dialogs) { DataContext = dialog };
                presentation.CurrentWindow = window;
                scope.ShowAndWaitForContentRendered(window);
                var editor = (TextBox)GetCategoryControl(window, "Playlist", nameof(SettingsDialogViewModel.SelectedCustomFolderAdditionalOutputBaseName)).Control;
                var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                ListBox list = FindDescendants<ListBox>(page).Single(item => item.Name == "additionalOutputList");
                list.SelectedItem = original;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(original, list.SelectedItem);
                Assert.AreEqual("Original", editor.Text);

                void AssertUnconfirmedInput(string expected)
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(expected, editor.Text, "Bindingからの書戻しで編集中の空白を失いません。");
                    Assert.AreEqual(expected, dialog.SelectedCustomFolderAdditionalOutputBaseName);
                    Assert.AreEqual(original, dialog.SelectedCustomFolderAdditionalOutputBaseDir);
                    CollectionAssert.AreEqual(new[] { original }, dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                    Assert.AreEqual(oldNormal, dialog.LR2CustomFolderOutputDir);
                    Assert.AreEqual(oldRoot, dialog.LR2CustomFolderAsRootOutputDir);
                    Assert.AreEqual(oldNormal, values.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(oldRoot, values.LR2CustomFolderOutputBaseDirRootType);
                    Assert.AreEqual(savedAdditional, values.LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.AreEqual(0, Volatile.Read(ref providerWriteNotifications), "改名ボタンで確定するまではproviderへの書込みを開始しません。");
                    Assert.AreEqual(0, dialogs.ConfirmationCount);
                    CollectionAssert.AreEqual(previousSettings, File.ReadAllBytes(path));
                    CollectionAssert.AreEqual(previousConfig, File.ReadAllBytes(values.LR2ConfigXmlPath));
                }

                editor.SelectAll();
                editor.SelectedText = "My";
                AssertUnconfirmedInput("My");
                editor.Select(editor.Text.Length, 0);
                editor.SelectedText = " ";
                AssertUnconfirmedInput("My ");
                editor.Select(editor.Text.Length, 0);
                editor.SelectedText = "Charts";
                AssertUnconfirmedInput("My Charts");
                editor.Select(editor.Text.Length, 0);
                editor.SelectedText = " ";
                AssertUnconfirmedInput("My Charts ");
                editor.Select(0, 0);
                editor.SelectedText = " ";
                AssertUnconfirmedInput(" My Charts ");

                Button rename = FindDescendants<Button>((DependencyObject)list.Parent).Single(button => Equals(button.Content, Resources.Rename_folder));
                Assert.IsTrue(rename.IsEnabled);
                rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SettingsOperationCompletionTask, "additional-output-rename-terminal");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
                Assert.IsTrue(Volatile.Read(ref providerWriteNotifications) > 0, "実確定は専用providerへ書込みを要求します。");
                string renamed = Path.Combine(root, "My Charts");
                Assert.AreEqual("My Charts", editor.Text);
                Assert.AreEqual("My Charts", dialog.SelectedCustomFolderAdditionalOutputBaseName);
                Assert.AreEqual(renamed, dialog.SelectedCustomFolderAdditionalOutputBaseDir);
                CollectionAssert.AreEqual(new[] { renamed }, dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(path);
                CollectionAssert.AreEqual(new[] { renamed }, CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(fresh.LR2CustomFolderAdditionalOutputBaseDirs).ToArray());
                Assert.AreEqual(oldNormal, fresh.LR2CustomFolderOutputBaseDir);
                Assert.AreEqual(oldRoot, fresh.LR2CustomFolderOutputBaseDirRootType);
                Assert.IsTrue(window.IsVisible);

                int writesBeforeSameName = Volatile.Read(ref providerWriteNotifications);
                int confirmationsBeforeSameName = dialogs.ConfirmationCount;
                int messagesBeforeSameName = dialogs.MessageCount;
                int failuresBeforeSameName = failures.Count;
                byte[] settingsBeforeSameName = File.ReadAllBytes(path);
                byte[] configBeforeSameName = File.ReadAllBytes(values.LR2ConfigXmlPath);
                bool confirmedKeepBmt = values.KeepBeatorajaBmtFilesWhenOutputDisabled;
                dialog.KeepBeatorajaBmtFilesWhenOutputDisabled = !confirmedKeepBmt;
                editor.Select(0, 0);
                editor.SelectedText = " ";
                editor.Select(editor.Text.Length, 0);
                editor.SelectedText = "  ";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(" My Charts  ", editor.Text);
                Assert.AreEqual(" My Charts  ", dialog.SelectedCustomFolderAdditionalOutputBaseName);
                Assert.AreEqual(writesBeforeSameName, Volatile.Read(ref providerWriteNotifications));
                rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SettingsOperationCompletionTask, "additional-output-same-name-terminal");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual("My Charts", editor.Text, "同値の明示改名でも確定時の正規化を表示へ戻します。");
                Assert.AreEqual("My Charts", dialog.SelectedCustomFolderAdditionalOutputBaseName);
                Assert.AreEqual(renamed, dialog.SelectedCustomFolderAdditionalOutputBaseDir);
                CollectionAssert.AreEqual(new[] { renamed }, dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([renamed]), values.LR2CustomFolderAdditionalOutputBaseDirs);
                Assert.AreEqual(oldNormal, values.LR2CustomFolderOutputBaseDir);
                Assert.AreEqual(oldRoot, values.LR2CustomFolderOutputBaseDirRootType);
                Assert.AreEqual(writesBeforeSameName, Volatile.Read(ref providerWriteNotifications));
                Assert.AreEqual(confirmationsBeforeSameName, dialogs.ConfirmationCount);
                Assert.AreEqual(messagesBeforeSameName, dialogs.MessageCount);
                Assert.AreEqual(failuresBeforeSameName, failures.Count);
                CollectionAssert.AreEqual(settingsBeforeSameName, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(configBeforeSameName, File.ReadAllBytes(values.LR2ConfigXmlPath));
                Assert.AreEqual(!confirmedKeepBmt, dialog.KeepBeatorajaBmtFilesWhenOutputDisabled, "同値改名は無関係なBMT候補を破棄・採用しません。");
                Assert.AreEqual(confirmedKeepBmt, values.KeepBeatorajaBmtFilesWhenOutputDisabled);
                fresh = PortableSettingsPersistenceTests.OpenSettings(path);
                CollectionAssert.AreEqual(new[] { renamed }, CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(fresh.LR2CustomFolderAdditionalOutputBaseDirs).ToArray());
                Assert.AreEqual(confirmedKeepBmt, fresh.KeepBeatorajaBmtFilesWhenOutputDisabled);

                int writesAfterRename = Volatile.Read(ref providerWriteNotifications);
                editor.SelectAll();
                editor.SelectedText = "Discarded Draft ";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual("Discarded Draft ", editor.Text);
                Assert.AreEqual("Discarded Draft ", dialog.SelectedCustomFolderAdditionalOutputBaseName);
                Assert.AreEqual(writesAfterRename, Volatile.Read(ref providerWriteNotifications), "未確定名の入力だけでは追加保存しません。");
                int writesBeforeOrdinarySave = Volatile.Read(ref providerWriteNotifications);
                string savedFormat = values.FolderNameFormat == "%ARTIST% - %TITLE%" ? "%TITLE%" : "%ARTIST% - %TITLE%";
                EditCategoryControl(window, "Install", nameof(SettingsDialogViewModel.FolderNameFormat), savedFormat);
                Assert.AreEqual(savedFormat, values.FolderNameFormat);
                Assert.AreEqual(savedFormat, PortableSettingsPersistenceTests.OpenSettings(path).FolderNameFormat);
                Assert.IsTrue(Volatile.Read(ref providerWriteNotifications) > writesBeforeOrdinarySave, "別ページの通常項目を実保存します。");
                editor = (TextBox)GetCategoryControl(window, "Playlist", nameof(SettingsDialogViewModel.SelectedCustomFolderAdditionalOutputBaseName)).Control;
                Assert.AreEqual("Discarded Draft ", editor.Text, "ページ移動と別項目の保存では編集中の候補を破棄しません。");
                Assert.AreEqual("Discarded Draft ", dialog.SelectedCustomFolderAdditionalOutputBaseName);
                Assert.AreEqual(renamed, dialog.SelectedCustomFolderAdditionalOutputBaseDir);
                CollectionAssert.AreEqual(new[] { renamed }, dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([renamed]), values.LR2CustomFolderAdditionalOutputBaseDirs);
                Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([renamed]), PortableSettingsPersistenceTests.OpenSettings(path).LR2CustomFolderAdditionalOutputBaseDirs);
                int writesBeforeClose = Volatile.Read(ref providerWriteNotifications);
                byte[] settingsBeforeClose = File.ReadAllBytes(path);
                byte[] configBeforeClose = File.ReadAllBytes(values.LR2ConfigXmlPath);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler closedHandler = (_, _) => closed.TrySetResult();
                window.Closed += closedHandler;
                try
                {
                    window.Close();
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "additional-output-rename-normal-close");
                }
                finally { window.Closed -= closedHandler; }
                Assert.IsFalse(window.IsVisible);
                Assert.IsNull(window.DataContext);
                Assert.AreEqual("My Charts", dialog.SelectedCustomFolderAdditionalOutputBaseName, "通常Closeでは未確定名だけを最新公開名へ戻します。");
                Assert.AreEqual(writesBeforeClose, Volatile.Read(ref providerWriteNotifications));
                CollectionAssert.AreEqual(settingsBeforeClose, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(configBeforeClose, File.ReadAllBytes(values.LR2ConfigXmlPath));

                reopened = new SettingsWindow(dialogs) { DataContext = dialog };
                presentation.CurrentWindow = reopened;
                scope.ShowAndWaitForContentRendered(reopened);
                editor = (TextBox)GetCategoryControl(reopened, "Playlist", nameof(SettingsDialogViewModel.SelectedCustomFolderAdditionalOutputBaseName)).Control;
                Assert.AreEqual("My Charts", editor.Text, "同VMの再表示では再選択せずに最新公開名を表示します。");
                Assert.AreEqual("My Charts", dialog.SelectedCustomFolderAdditionalOutputBaseName);
                Assert.AreEqual(writesBeforeClose, Volatile.Read(ref providerWriteNotifications));
                CollectionAssert.AreEqual(settingsBeforeClose, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(configBeforeClose, File.ReadAllBytes(values.LR2ConfigXmlPath));
                Assert.AreEqual(savedFormat, PortableSettingsPersistenceTests.OpenSettings(path).FolderNameFormat);

                editor.SelectAll();
                editor.SelectedText = "Next Charts";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual("Next Charts", editor.Text);
                Assert.AreEqual("Next Charts", dialog.SelectedCustomFolderAdditionalOutputBaseName);
                page = (FrameworkElement)((ContentControl)reopened.FindName("settingsPageContent")).Content;
                list = FindDescendants<ListBox>(page).Single(item => item.Name == "additionalOutputList");
                Assert.AreEqual(renamed, list.SelectedItem);
                rename = FindDescendants<Button>((DependencyObject)list.Parent).Single(button => Equals(button.Content, Resources.Rename_folder));
                Assert.IsTrue(rename.IsEnabled);
                rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SettingsOperationCompletionTask, "additional-output-rename-after-reopen-terminal");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
                string next = Path.Combine(root, "Next Charts");
                Assert.AreEqual("Next Charts", editor.Text);
                Assert.AreEqual("Next Charts", dialog.SelectedCustomFolderAdditionalOutputBaseName);
                Assert.AreEqual(next, dialog.SelectedCustomFolderAdditionalOutputBaseDir);
                CollectionAssert.AreEqual(new[] { next }, dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                fresh = PortableSettingsPersistenceTests.OpenSettings(path);
                CollectionAssert.AreEqual(new[] { next }, CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(fresh.LR2CustomFolderAdditionalOutputBaseDirs).ToArray());
                Assert.AreEqual(savedFormat, fresh.FolderNameFormat);
                Assert.AreEqual(oldNormal, fresh.LR2CustomFolderOutputBaseDir);
                Assert.AreEqual(oldRoot, fresh.LR2CustomFolderOutputBaseDirRootType);
            }
            finally
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "additional-output-rename-cleanup-terminal");
                if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                if (reopened?.IsVisible == true) { reopened.CloseForOwnerShutdown(); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "additional-output-rename-owner-terminal");
                owner.SettingDialog.Dispose();
                foreach (SettingsProperty property in observedProperties) { property.Provider = originalProvider; }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>実recordボタンは選択行の差分へBindingし、別行だけの変更では無効、無効入力でも取消可となり、実取消は選択行だけを戻します。不正な保存rawから追加した両種の新規行も取消でき、実保存rawは変更しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CompiledRecordButtons_TrackSelectedRecordAndCancelInvalidDraftWithoutChangingOtherRows(bool invalidPersisted)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(path));
            values.RightClickActionsJson = invalidPersisted ? "{broken JSON" : """
                {"webActions":[{"id":"one","name":"One","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"},{"id":"two","name":"Two","urlTemplate":"https://example.test/{sha256}","enabled":true,"chartKind":"All"}],"programActions":[]}
                """;
            values.Save();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
            SettingsWindow? window = null;
            try
            {
                window = OpenSettingsWindow(scope, owner.SettingDialog);
                GetCategoryControl(window, "RightClick", "RightClickActionSettingsEditor.SelectedWebAction.Name");
                RightClickActionSettingsEditor editor = owner.SettingDialog.RightClickActionSettingsEditor;
                if (invalidPersisted)
                {
                    foreach (bool web in new[] { true, false })
                    {
                        if (web) { editor.AddWebAction(); } else { editor.AddProgramAction(); }
                        string section = web ? "web" : "program";
                        Button recordConfirm = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:" + section));
                        Button recordCancel = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "cancel:" + section));
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(recordConfirm.IsEnabled);
                        Assert.IsTrue(recordCancel.IsEnabled);
                        recordCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(0, editor.WebActions.Count);
                        Assert.AreEqual(0, editor.ProgramActions.Count);
                        Assert.IsFalse(recordCancel.IsEnabled);
                        Assert.IsTrue(editor.IsInvalidPersistedSettings);
                        Assert.AreEqual("{broken JSON", values.RightClickActionsJson);
                        Assert.AreEqual("{broken JSON", PortableSettingsPersistenceTests.OpenSettings(path).RightClickActionsJson);
                    }
                    return;
                }
                Button confirm = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:web"));
                Button cancel = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "cancel:web"));
                Assert.IsFalse(confirm.IsEnabled); Assert.IsFalse(cancel.IsEnabled);
                editor.WebActions[1].Name = "Other draft";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(confirm.IsEnabled); Assert.IsFalse(cancel.IsEnabled);
                editor.WebActions[0].Name = "Edited";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(confirm.IsEnabled); Assert.IsTrue(cancel.IsEnabled);
                editor.WebActions[0].UrlTemplate = "https://example.test/{unknown}";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(confirm.IsEnabled); Assert.IsTrue(cancel.IsEnabled);
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual("One", editor.WebActions[0].Name);
                Assert.AreEqual("Other draft", editor.WebActions[1].Name);
                Assert.IsFalse(confirm.IsEnabled); Assert.IsFalse(cancel.IsEnabled);
                Assert.AreEqual("Two", RightClickActionSettingsSerializer.Parse(PortableSettingsPersistenceTests.OpenSettings(path).RightClickActionsJson).Settings.WebActions[1].Name);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "record-binding-owner-terminal");
                owner.SettingDialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>実音声入力の正常保存・失敗・retry・確定値への復帰を確認します。ページ初生成前と同じComboBox表示中の言語変更で品質・並列度の表示だけを更新し、選択・未保存候補・保存媒体・テスト結果を保ちます。</summary>
    /// <remarks>専用providerと実Bindingを使い、native機器だけを代替します。資源通知区間の列挙・照会・テスト回数と媒体を観測し、追加Commitの不在は表示更新経路の静的確認と分担します。既存language setterの言語保存は対象外です。</remarks>
    [TestMethod]
    public void CompiledAudioSelection_ShowsSaveRetryOnlyAfterFailureAndClearsOnSuccessOrRestoration()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            string culture = Resources.Culture?.Name ?? "ja-JP";
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(path));
            values.PlayerDriver = BassAudioMapping.ToBassDriver(AudioDriver.WasapiShared);
            values.PlayerDevice = string.Empty;
            values.PlayerDeviceName = string.Empty;
            values.PlayerWASAPIParam = false;
            values.PlayerResamplingQuality = 4;
            values.PlayerMixerThreadCount = 3;
            values.Save();
            var failures = new List<Exception>();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
            owner.SettingDialog.Dispose();
            int testCount = 0;
            int queryCount = 0;
            var catalog = new TestAudioDeviceCatalog();
            var workflow = new AudioDeviceTestWorkflowOwner(new NoOpAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                {
                    Interlocked.Increment(ref testCount);
                    return AudioDeviceTestResultFactory.CreateSuccessful(request);
                }),
                new DelegateAudioDeviceCapabilityRuntime(request =>
                {
                    Interlocked.Increment(ref queryCount);
                    return new AudioDeviceCapabilityResult(request.Backend, request.DeviceIdentity, request.DeviceName, AudioDeviceCapabilityStatus.Unsupported, []);
                }));
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
            using var dialog = new SettingsDialogViewModel(owner, owner.PlaylistWorkspace, owner.PlaylistWorkspace, owner.PlayHistory, owner.LibraryFolderTree,
                composition, owner.PlaybackPanel, owner.Lr2SongDbSyncWorkflow, new SettingsEditSession(values), reportApplyFailure: failures.Add,
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                externalShellGateway: ExternalShellGatewayPolicy.Current, applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: catalog, audioSettingsGateway: new SettingsAudioGateway(() => values), audioDeviceTestWorkflow: workflow);
            SettingsWindow? window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                byte[] beforeFirstCultureChange = File.ReadAllBytes(path);
                string previousQualityName = dialog.PlayerResamplingQualityNames[4];
                string previousDefaultThreadName = dialog.PlayerMixerThreadCountNames[1];
                ResourceService.Current.ChangeCulture("ja-JP");
                CollectionAssert.AreEqual(beforeFirstCultureChange, File.ReadAllBytes(path));
                Assert.AreNotEqual(previousQualityName, dialog.PlayerResamplingQualityNames[4]);
                Assert.AreNotEqual(previousDefaultThreadName, dialog.PlayerMixerThreadCountNames[1]);
                Assert.AreEqual(0, catalog.RefreshCount, "ページ初生成前の言語変更では列挙しません。");
                Assert.AreEqual(0, Volatile.Read(ref queryCount));
                Assert.AreEqual(0, Volatile.Read(ref testCount));
                window = OpenSettingsWindow(scope, dialog);
                var toggle = (CheckBox)GetCategoryControl(window, "Audio", nameof(SettingsDialogViewModel.PlayerWASAPIParam)).Control;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-language-first-page-terminal");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-language-first-capability-terminal");
                var quality = (ComboBox)GetCategoryControl(window, "Audio", nameof(SettingsDialogViewModel.PlayerResamplingQuality)).Control;
                var threads = (ComboBox)GetCategoryControl(window, "Audio", nameof(SettingsDialogViewModel.PlayerMixerThreadCount)).Control;
                void AssertLocalizedOptionsAndSelection()
                {
                    KeyValuePair<int, string>[] qualityItems = quality.Items.Cast<KeyValuePair<int, string>>().ToArray();
                    CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 6 }, qualityItems.Select(item => item.Key).ToArray());
                    foreach (KeyValuePair<int, string> item in qualityItems)
                    {
                        Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.AudioResamplingQualityOptionFormat,
                            item.Key, AudioResamplingQuality.GetSincPointCount(item.Key)), item.Value);
                    }
                    KeyValuePair<int, string>[] threadItems = threads.Items.Cast<KeyValuePair<int, string>>().ToArray();
                    CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, threadItems.Select(item => item.Key).ToArray());
                    foreach (KeyValuePair<int, string> item in threadItems)
                    {
                        string expected = item.Key == 1
                            ? string.Format(CultureInfo.CurrentCulture, Resources.AudioMixerThreadCountDefaultOptionFormat, item.Key)
                            : item.Key.ToString(CultureInfo.CurrentCulture);
                        Assert.AreEqual(expected, item.Value);
                    }
                    Assert.AreEqual(4, quality.SelectedValue);
                    Assert.AreEqual(3, threads.SelectedValue);
                    Assert.AreEqual(qualityItems.Single(item => item.Key == 4).Value, quality.Text);
                    Assert.AreEqual(threadItems.Single(item => item.Key == 3).Value, threads.Text);
                    Assert.AreEqual(4, dialog.PlayerResamplingQuality);
                    Assert.AreEqual(3, dialog.PlayerMixerThreadCount);
                    Settings saved = PortableSettingsPersistenceTests.OpenSettings(path);
                    Assert.AreEqual(4, saved.PlayerResamplingQuality);
                    Assert.AreEqual(3, saved.PlayerMixerThreadCount);
                }
                AssertLocalizedOptionsAndSelection();
                Assert.IsFalse(dialog.PlayerWASAPIParam);
                Assert.IsFalse(toggle.IsChecked);
                Assert.IsFalse(PortableSettingsPersistenceTests.OpenSettings(path).PlayerWASAPIParam);
                Button retry = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "retry:audio"));
                Assert.AreEqual(Visibility.Collapsed, retry.Visibility);
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(path).PlayerWASAPIParam);
                Assert.IsFalse(dialog.CanRetryAudioSettings);
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                { toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false); }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(1, failures.Count);
                Assert.AreEqual(Visibility.Visible, retry.Visibility);
                Assert.AreEqual(Resources.Settings_retry_audio_save, retry.Content);
                Assert.IsTrue(FindDescendants<TextBlock>(window).Any(text => text.Text == Resources.Settings_audio_save_retry_description && text.Visibility == Visibility.Visible));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-language-draft-query-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.RunAudioDeviceTestAsync(), "audio-language-test-result-terminal");
                Assert.IsFalse(dialog.PlayerWASAPIParam);
                Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(path).PlayerWASAPIParam);
                Assert.IsTrue(dialog.CanRetryAudioSettings);
                AssertLocalizedOptionsAndSelection();
                string result = dialog.AudioDeviceTestStatusMessage;
                string diagnostic = dialog.AudioDeviceTestDiagnosticMessage;
                Assert.IsFalse(string.IsNullOrEmpty(result));
                int refreshes = catalog.RefreshCount;
                int queries = Volatile.Read(ref queryCount);
                int tests = Volatile.Read(ref testCount);
                byte[] beforeVisibleCultureChange = File.ReadAllBytes(path);
                previousQualityName = quality.Text;
                previousDefaultThreadName = threads.Items.Cast<KeyValuePair<int, string>>().Single(item => item.Key == 1).Value;
                ResourceService.Current.ChangeCulture("en-US");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                AssertLocalizedOptionsAndSelection();
                Assert.AreNotEqual(previousQualityName, quality.Text);
                Assert.AreNotEqual(previousDefaultThreadName, threads.Items.Cast<KeyValuePair<int, string>>().Single(item => item.Key == 1).Value);
                Assert.AreSame(quality, GetCategoryControl(window, "Audio", nameof(SettingsDialogViewModel.PlayerResamplingQuality)).Control);
                Assert.AreSame(threads, GetCategoryControl(window, "Audio", nameof(SettingsDialogViewModel.PlayerMixerThreadCount)).Control);
                Assert.IsFalse(dialog.PlayerWASAPIParam);
                Assert.IsFalse(toggle.IsChecked);
                Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(path).PlayerWASAPIParam);
                Assert.IsTrue(dialog.CanRetryAudioSettings);
                Assert.AreEqual(Visibility.Visible, retry.Visibility);
                Assert.AreEqual(Resources.Settings_retry_audio_save, retry.Content);
                Assert.AreEqual(result, dialog.AudioDeviceTestStatusMessage);
                Assert.AreEqual(diagnostic, dialog.AudioDeviceTestDiagnosticMessage);
                Assert.AreEqual(refreshes, catalog.RefreshCount);
                Assert.AreEqual(queries, Volatile.Read(ref queryCount));
                Assert.AreEqual(tests, Volatile.Read(ref testCount));
                Assert.AreEqual(1, failures.Count);
                CollectionAssert.AreEqual(beforeVisibleCultureChange, File.ReadAllBytes(path));
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(PortableSettingsPersistenceTests.OpenSettings(path).PlayerWASAPIParam);
                Assert.AreEqual(Visibility.Collapsed, retry.Visibility);
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    Assert.IsTrue(dialog.CanRetryAudioSettings);
                    toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                    Assert.IsFalse(dialog.CanRetryAudioSettings);
                }
                Assert.AreEqual(2, failures.Count);
                Assert.IsFalse(dialog.IsAudioDeviceTestInProgress);
            }
            finally
            {
                try
                {
                    window?.CloseForOwnerShutdown();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-save-retry-terminal");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "audio-save-retry-owner-terminal");
                    dialog.Dispose();
                }
                finally
                {
                    ResourceService.Current.ChangeCulture(culture);
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    /// <summary>通常・ROOT・追加先の実compiled選択入口から保存し、picker取消・確認取消・確認中のP先着では候補と実二媒体を保持します。同値の通常・ROOT明示選択も共有拒否したXMLの再保存へ到達し、初期Bindingは保存しません。</summary>
    /// <remarks>確認中のP先着はWindowを開いたまま拒否後の実Binding復帰まで確認します。通常Closeの候補破棄とTask寿命はSettingsWindowPresentationTests、媒体失敗・実生成・整理はOutputConfirmationが分担します。</remarks>
    [DataTestMethod]
    [DataRow("normal", "accept")]
    [DataRow("root", "accept")]
    [DataRow("additional", "accept")]
    [DataRow("normal", "same")]
    [DataRow("root", "same")]
    [DataRow("normal", "picker-cancel")]
    [DataRow("root", "picker-cancel")]
    [DataRow("additional", "picker-cancel")]
    [DataRow("normal", "confirmation-cancel")]
    [DataRow("root", "confirmation-cancel")]
    [DataRow("additional", "confirmation-cancel")]
    [DataRow("normal", "invalid-after-busy")]
    [DataRow("root", "invalid-after-busy")]
    [DataRow("normal", "confirmation-busy")]
    [DataRow("additional", "invalid-after-busy")]
    public void CompiledOutputSelection_CommitsOnlySelectedPlacementAndKeepsCancelledMedia(string section, string outcome)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            string target = Path.Combine(root, "SelectedOutput");
            Settings values = CreateValidCustomFolderSettings(root, [target], PortableSettingsPersistenceTests.OpenSettings(path));
            values.Save();
            string oldNormal = values.LR2CustomFolderOutputBaseDir;
            string oldRoot = values.LR2CustomFolderOutputBaseDirRootType;
            if (outcome == "same") { target = section == "normal" ? oldNormal : oldRoot; }
            byte[] previousSettings = File.ReadAllBytes(path);
            byte[] previousConfig = File.ReadAllBytes(values.LR2ConfigXmlPath);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(outcome == "confirmation-cancel" ? MessageBoxResult.Cancel : MessageBoxResult.OK)
            };
            dialogs.FolderResults.Enqueue(outcome == "picker-cancel"
                ? new UiFolderPickerResult(UiDialogStatus.CancelledByUser)
                : new UiFolderPickerResult(UiDialogStatus.Accepted, [target]));
            var failures = new List<Exception>();
            MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: true, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
            SettingsWindow? window = null;
            IDisposable? confirmationLease = null;
            FileStream? blockedConfig = null;
            var confirmationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                if (outcome == "same") { blockedConfig = File.Open(values.LR2ConfigXmlPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                string staged = Directory.CreateDirectory(Path.Combine(root, "BmsDraft")).FullName;
                window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationPlaylist");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, failures.Count, "初期Bindingの同値通知では保存しません。");
                owner.SettingDialog.AddBmsSearchRootPathsAsync([staged]).GetAwaiter().GetResult();
                CollectionAssert.Contains(owner.SettingDialog.AvailableBMSDirectories, staged);
                var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                Button select;
                BeMusicSeeker.Views.Settings.SettingsPathPicker? outputPicker = null;
                if (section == "additional")
                {
                    ListBox list = FindDescendants<ListBox>(page).Single(item => item.Name == "additionalOutputList");
                    select = FindDescendants<Button>((DependencyObject)list.Parent).Single(button => Equals(button.Content, Resources.Add));
                }
                else
                {
                    string property = section == "normal" ? nameof(SettingsDialogViewModel.LR2CustomFolderOutputDir) : nameof(SettingsDialogViewModel.LR2CustomFolderAsRootOutputDir);
                    outputPicker = FindDescendants<BeMusicSeeker.Views.Settings.SettingsPathPicker>(page)
                        .Single(item => item.GetBindingExpression(BeMusicSeeker.Views.Settings.SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == property);
                    select = FindDescendants<Button>(outputPicker).Single();
                }
                if (outcome == "confirmation-busy")
                {
                    dialogs.ConfirmationObservedAsync = () => { confirmationEntered.TrySetResult(); return releaseConfirmation.Task; };
                }
                if (outcome == "invalid-after-busy")
                {
                    ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
                    Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable? lease));
                    using (lease)
                    {
                        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "output-rejected-candidate-terminal");
                    }
                    Assert.AreEqual(1, dialogs.MessageCount);
                    CollectionAssert.AreEqual(previousSettings, File.ReadAllBytes(path));
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [root]));
                }
                select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (outcome == "confirmation-busy")
                {
                    Task operation = owner.SettingDialog.SettingsOperationCompletionTask;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(confirmationEntered.Task, operation,
                        "output-confirmation-entered"), "output-confirmation-arrival");
                    Assert.IsFalse(operation.IsCompleted);
                    CollectionAssert.Contains(owner.SettingDialog.AvailableBMSDirectories, staged);
                    ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
                    Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable? confirmationLibraryLease), "確認待機ではLを予約しません。");
                    confirmationLibraryLease?.Dispose();
                    Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable acquired), "確認待機ではPを予約しません。");
                    confirmationLease = acquired;
                    releaseConfirmation.TrySetResult();
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "output-selection-terminal");
                if (outcome == "confirmation-busy")
                {
                    Assert.IsTrue(MainWindowViewModelTestFactory.GetComposition(owner).OperationAdmission.TryEnter(out IDisposable? rejectedLibraryLease), "P拒否で一時取得したLは解放します。");
                    rejectedLibraryLease?.Dispose();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(oldNormal, owner.SettingDialog.LR2CustomFolderOutputDir);
                    Assert.AreEqual(oldNormal, outputPicker?.Path, "未受理の出力候補だけを確定配置へ戻し、実Bindingへ反映します。");
                }
                Assert.AreEqual(outcome == "same" ? 1 : 0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.AreEqual(outcome == "invalid-after-busy" ? 2 : outcome == "confirmation-busy" ? 1 : 0, dialogs.MessageCount, dialogs.LastMessageText);
                Assert.AreEqual(outcome == "invalid-after-busy" ? 2 : 1, dialogs.FolderRequests.Count);
                Assert.IsTrue(dialogs.FolderRequests.All(request => ReferenceEquals(window, request.Owner)));
                Assert.AreEqual(outcome == "picker-cancel" ? 0 : 1, dialogs.ConfirmationCount);
                Assert.AreEqual(outcome is not ("accept" or "same"), owner.SettingDialog.AvailableBMSDirectories.Contains(staged), "競合候補は採用後だけ破棄し、picker取消・確認取消・受付拒否では保持します。");
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(path);
                if (outcome is "accept" or "same")
                {
                    Assert.AreEqual(section == "normal" ? target : oldNormal, fresh.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(section == "root" ? target : oldRoot, fresh.LR2CustomFolderOutputBaseDirRootType);
                    CollectionAssert.AreEqual(section == "additional" ? new[] { target } : Array.Empty<string>(),
                        CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(fresh.LR2CustomFolderAdditionalOutputBaseDirs).ToArray());
                }
                else
                {
                    CollectionAssert.AreEqual(previousSettings, File.ReadAllBytes(path));
                    CollectionAssert.AreEqual(previousConfig, File.ReadAllBytes(values.LR2ConfigXmlPath));
                }
                Assert.IsTrue(window.IsVisible);
            }
            finally
            {
                blockedConfig?.Dispose();
                releaseConfirmation.TrySetResult();
                confirmationLease?.Dispose();
                if (window != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "output-picker-cleanup-terminal"); }
                window?.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-picker-owner-terminal");
                owner.SettingDialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>初回LR2の両空値・両旧先欠落から、どちらの実pickerでも順に二媒体へ保存できます。未変更の要修復先へアクセスせず、別区分候補を混ぜず、空値の開始必須検査を維持します。</summary>
    [DataTestMethod]
    [DataRow("normal", false)]
    [DataRow("root", false)]
    [DataRow("normal", true)]
    [DataRow("root", true)]
    public void CompiledInitialLr2OutputPickers_SaveEachRequiredPlacementBeforeAllowingInitialization(string firstSection, bool missingPreviousDirectories)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            string normal = Path.Combine(root, "SelectedNormal");
            string rootOutput = Path.Combine(root, "SelectedRoot");
            Settings values = CreateValidCustomFolderSettings(root, [normal, rootOutput], PortableSettingsPersistenceTests.OpenSettings(path));
            string previousNormal = missingPreviousDirectories ? Path.Combine(root, "UnavailableNormal", "PreviousNormal") : string.Empty;
            string previousRoot = missingPreviousDirectories ? Path.Combine(root, "UnavailableRoot", "PreviousRoot") : string.Empty;
            values.LR2CustomFolderOutputBaseDir = previousNormal;
            values.LR2CustomFolderOutputBaseDirRootType = previousRoot;
            if (missingPreviousDirectories)
            {
                Directory.CreateDirectory(previousNormal);
                Directory.CreateDirectory(previousRoot);
                var config = new BeMusicSeeker.Models.LR2.LR2Config(values.LR2ConfigXmlPath);
                config.AddBMSSearchDirectories([previousNormal]);
                config.Save(config.ConfigFilePath);
                Directory.Delete(previousNormal);
                Directory.Delete(previousRoot);
                Directory.Delete(Path.Combine(root, "UnavailableNormal"));
                Directory.Delete(Path.Combine(root, "UnavailableRoot"));
                // 登録後に旧先を失い、別区分の保存から作り直すと失敗する実FS条件にします。
                File.WriteAllText(Path.Combine(root, "UnavailableNormal"), "unavailable previous parent");
                File.WriteAllText(Path.Combine(root, "UnavailableRoot"), "unavailable previous parent");
            }
            values.Save();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            var failures = new List<Exception>();
            MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: true, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
            SettingsWindow? window = null;
            try
            {
                if (!missingPreviousDirectories) { Assert.IsFalse(owner.SettingDialog.CheckValidationForInitialization(out _)); }
                window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationPlaylist");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                string otherSection = firstSection == "normal" ? "root" : "normal";
                SetCustomFolderOutput(owner.SettingDialog, otherSection, Path.Combine(root, "UnconfirmedOutput"));
                int completed = 0;
                foreach (string section in new[] { firstSection, otherSection })
                {
                    string property = section == "normal" ? nameof(SettingsDialogViewModel.LR2CustomFolderOutputDir) : nameof(SettingsDialogViewModel.LR2CustomFolderAsRootOutputDir);
                    BeMusicSeeker.Views.Settings.SettingsPathPicker picker = FindDescendants<BeMusicSeeker.Views.Settings.SettingsPathPicker>(page)
                        .Single(item => item.GetBindingExpression(BeMusicSeeker.Views.Settings.SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == property);
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [section == "normal" ? normal : rootOutput]));
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "initial-output-picker-terminal");
                    completed++;
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
                    Assert.AreEqual(completed, dialogs.ConfirmationCount);
                    Settings fresh = PortableSettingsPersistenceTests.OpenSettings(path);
                    string expectedNormal = completed == 2 || section == "normal" ? normal : previousNormal;
                    string expectedRoot = completed == 2 || section == "root" ? rootOutput : previousRoot;
                    Assert.AreEqual(expectedNormal, fresh.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(expectedRoot, fresh.LR2CustomFolderOutputBaseDirRootType);
                    CustomFolderOutputSettingsSnapshot reader = MainWindowViewModelTestFactory.GetComposition(owner).CustomFolderOutputSettingsProvider();
                    Assert.AreEqual(expectedNormal, reader.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(expectedRoot, reader.LR2CustomFolderOutputBaseDirRootType);
                    string[] registered = new BeMusicSeeker.Models.LR2.LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking().ToArray();
                    CollectionAssert.Contains(registered, normal);
                    if (missingPreviousDirectories)
                    {
                        Assert.IsFalse(Directory.Exists(previousNormal));
                        Assert.IsFalse(Directory.Exists(previousRoot));
                        if (expectedNormal == previousNormal) { CollectionAssert.Contains(registered, previousNormal); }
                        else { CollectionAssert.DoesNotContain(registered, previousNormal); }
                    }
                    if (!missingPreviousDirectories || completed == 2)
                    { Assert.AreEqual(completed == 2, owner.SettingDialog.CheckValidationForInitialization(out string error), error); }
                }
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "initial-output-owner-terminal");
                owner.SettingDialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>実画像controlの有効選択を自動保存し現在のPlaybackPanel画像へ届けます。実保存失敗は確定値と現画像を保持し、同値・別項目保存では再試行せず明示再試行だけで完了します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CompiledImageSelection_AutomaticallySavesAndUpdatesCurrentArtworkOrKeepsFailure(bool failSave)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            string imagePath = Path.Combine(root, "external.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
                1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 33, 44, 55, 255 }, 4)));
            using (FileStream imageFile = File.Create(imagePath)) { encoder.Save(imageFile); }
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(path));
            values.UseExternalPanelImage = false;
            values.StagefilePath = imagePath;
            values.Save();
            var failures = new List<Exception>();
            var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                reportSettingsApplyFailure: failures.Add, audioDeviceCatalog: new TestAudioDeviceCatalog(),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
            var view = new PlaybackPanelView { DataContext = owner.PlaybackPanel };
            var playbackWindow = new Window { Content = view, Width = 640, Height = 360, ShowInTaskbar = false };
            var artwork = (Image)view.FindName("gridBMSPlayerImage");
            SettingsWindow? window = null;
            try
            {
                scope.ShowAndWaitForContentRendered(playbackWindow);
                window = OpenSettingsWindow(scope, owner.SettingDialog);
                var toggle = (CheckBox)GetCategoryControl(window, "Appearance", nameof(SettingsDialogViewModel.UseExternalPanelImage)).Control;
                System.Windows.Media.ImageSource previousImage = artwork.Source;
                if (failSave)
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); }
                    Assert.AreEqual(1, failures.Count);
                    Assert.IsFalse(values.UseExternalPanelImage);
                    Assert.AreSame(previousImage, artwork.Source);
                    toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    owner.SettingDialog.ShowNewPackageInstallConfirmMsg = !values.ShowNewPackageInstallConfirmMsg;
                    Assert.IsFalse(PortableSettingsPersistenceTests.OpenSettings(path).UseExternalPanelImage);
                    Assert.AreEqual(1, failures.Count);
                    Assert.IsTrue(owner.SettingDialog.CanRetryImageSettings);
                    FindDescendants<Button>(window).Single(button => Equals(button.Tag, "retry:image"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else { toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsInstanceOfType<System.Windows.Media.Imaging.BitmapSource>(artwork.Source);
                var bitmap = (System.Windows.Media.Imaging.BitmapSource)artwork.Source;
                Assert.AreEqual(1, bitmap.PixelWidth);
                byte[] pixels = new byte[4];
                bitmap.CopyPixels(pixels, 4, 0);
                Assert.AreEqual((byte)33, pixels[0]);
                Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(path).UseExternalPanelImage);
                Assert.IsFalse(owner.SettingDialog.CanRetryImageSettings);
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(failSave ? 1 : 0, failures.Count);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                view.DisposePlayerHost();
                playbackWindow.Content = null;
                playbackWindow.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "image-owner-terminal");
                owner.SettingDialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>通常TextBoxのEnterとSliderのドラッグ完了だけを確定時機とし、途中値・不正な別候補を保存せず同じWindowを維持します。</summary>
    [TestMethod]
    public void CompiledOrdinaryInputs_EnterAndSliderCompletionPersistOnlyTheirOwnItem()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")));
            values.Save();
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false);
                SetActiveLibraryProfile(owner, true);
                SettingsWindow? window = null;
                try
                {
                    window = OpenSettingsWindow(scope, owner.SettingDialog);
                    owner.SettingDialog.UseBeatorajaScoreDb = true;
                    var text = (TextBox)GetCategoryControl(window, "Install", "FolderNameFormat").Control;
                    text.SetCurrentValue(TextBox.TextProperty, "%ARTIST% - %TITLE%");
                    Assert.AreNotEqual("%ARTIST% - %TITLE%", PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")).FolderNameFormat);
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    text.RaiseEvent(key);
                    Assert.IsTrue(key.Handled);
                    Assert.AreEqual("%ARTIST% - %TITLE%", PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")).FolderNameFormat);
                    var slider = (Slider)GetCategoryControl(window, "Appearance", "CustomTableRowHeight").Control;
                    double before = values.CustomTableRowHeight;
                    slider.SetCurrentValue(RangeBase.ValueProperty, 27d);
                    slider.SetCurrentValue(RangeBase.ValueProperty, 31d);
                    Assert.AreEqual(before, values.CustomTableRowHeight);
                    slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent, Source = slider });
                    Settings fresh = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config"));
                    Assert.AreEqual(31d, fresh.CustomTableRowHeight);
                    Assert.IsFalse(fresh.UseBeatorajaScoreDb);
                    ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAbout");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    var about = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                    System.Windows.Documents.Hyperlink inline = FindDescendants<TextBlock>(about)
                        .SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
                    inline.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                    Assert.IsTrue(window.IsVisible, "共通slider終端handlerは非VisualのHyperlink入力を妨げません。");
                }
                finally { window?.CloseForOwnerShutdown(); owner.SettingDialog.Dispose(); }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>各カテゴリの実compiled editorを変更して実保存し、専用providerの新reader、稼働側の設定窓口、新dialogの値を接続します。新しい取消編集は保存済み値へ戻し、別設定と別Web actionを保持します。</summary>
    /// <remarks>各副作用の条件表は既存設定・player・導入・playlist系列へ分担し、この実Window系列はBindingと保存の正常接続だけを補います。文化・テーマ・共有通知・Window一覧を準備から終端と復元まで所有します。</remarks>
    [DataTestMethod]
    [DataRow("General", "Language")]
    [DataRow("Install", "FolderNameFormat")]
    [DataRow("Playlist", "OverwritePlaylistUrlsWithCompletion")]
    [DataRow("Appearance", "CustomTableRowHeight")]
    [DataRow("Playback", "IsSaveLR2bodyWindowPosition")]
    [DataRow("Audio", "PlayerResamplingQuality")]
    [DataRow("Recording", "EncoderAmplifier")]
    [DataRow("Backup", "LR2BackupNum")]
    [DataRow("Install", "KeepInstallablePackagesPending")]
    [DataRow("Install", "RequireCompleteUsedResourcesForAutoInstall")]
    [DataRow("Install", "AutoApplyAmbiguousInstallDestination")]
    [DataRow("Install", "AutoInstallAfterAutomaticEstimation")]
    [DataRow("Install", "InstallDestinationAutoApplyMinAudioHealth")]
    [DataRow("RightClick", "RightClickActionSettingsEditor.SelectedWebAction.Name")]
    public void CompiledCategoryEditor_SaveReachesFreshProviderLiveSettingsAndNewDialog(string category, string bindingPath)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string config = Path.Combine(root, "config", "user.config");
            Directory.CreateDirectory(Path.GetDirectoryName(config) ?? throw new AssertFailedException());
            string culture = Resources.Culture?.Name ?? "ja-JP";
            string theme = Settings.Default.AppearanceTheme;
            MainWindowViewModel? owner = null;
            MainWindowViewModel? freshOwner = null;
            SettingsWindow? window = null;
            SettingsWindow? reopened = null;
            Task? save = null;
            Exception? primaryFailure = null;
            try
            {
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(config));
                values.Lang = "ja-JP";
                values.LangDisplayName = TestApplicationContext.CreateCultureCatalog().Cultures.Single(pair => pair.Value == "ja-JP").Key;
                values.EnablePlaylistUrlCompletion = true;
                values.PlaylistMd5UrlMappingTsvUri = null;
                values.OverwritePlaylistUrlsWithCompletion = false;
                values.CustomTableRowHeight = 22;
                values.IsSaveLR2bodyWindowPosition = false;
                values.UsePlayerLR2body = true;
                values.PlayerResamplingQuality = 2;
                values.EncoderAmplifier = 1f;
                values.IsLR2BackupEnabled = true;
                values.LR2BackupPath = Path.Combine(root, "backup");
                Directory.CreateDirectory(values.LR2BackupPath);
                values.LR2BackupNum = 2;
                values.KeepInstallablePackagesPending = false;
                values.RequireCompleteUsedResourcesForAutoInstall = false;
                values.AutoApplyAmbiguousInstallDestination = false;
                values.AutoInstallAfterAutomaticEstimation = false;
                // 範囲外の保存値は画面初期化で丸めず、選び直して97%へ修正します。
                values.InstallDestinationAutoApplyMinAudioHealth = bindingPath == "InstallDestinationAutoApplyMinAudioHealth" ? 89 : 100;
                values.FolderNameFormat = "%TITLE%";
                values.RightClickActionsJson = "{\"webActions\":[{\"id\":\"edited\",\"name\":\"Before\",\"urlTemplate\":\"https://example.com/{md5}\",\"enabled\":true,\"chartKind\":\"All\"},{\"id\":\"kept\",\"name\":\"Other\",\"urlTemplate\":\"https://example.org/{md5}\",\"enabled\":true,\"chartKind\":\"BmsOnly\"}],\"programActions\":[]}";
                values.Save();
                string untouched = values.EncodeFileNameFormat;
                var failures = new List<Exception>();
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, reportSettingsApplyFailure: failures.Add);
                SetActiveLibraryProfile(owner, true);
                var presentation = new WindowClosingPresentationPort();
                owner.SettingDialog.AttachPresentationPort(presentation);
                window = OpenSettingsWindow(scope, owner.SettingDialog);
                presentation.CurrentWindow = window;
                object before = ReadCategoryEditor(window, category, bindingPath);
                if (bindingPath == "InstallDestinationAutoApplyMinAudioHealth")
                {
                    Assert.IsNull(before);
                    Assert.AreEqual(89, owner.SettingDialog.InstallDestinationAutoApplyMinAudioHealth);
                    Assert.IsFalse(string.IsNullOrEmpty(owner.SettingDialog.InstallDestinationAutoApplyMinAudioHealthWarning));
                }
                object expected = bindingPath switch
                {
                    "InstallDestinationAutoApplyMinAudioHealth" => 97,
                    "Language" => TestApplicationContext.CreateCultureCatalog().Cultures.Single(pair => pair.Value == "en-US").Key,
                    "FolderNameFormat" => "%ARTIST% - %TITLE%",
                    "OverwritePlaylistUrlsWithCompletion" or "IsSaveLR2bodyWindowPosition"
                        or "KeepInstallablePackagesPending" or "RequireCompleteUsedResourcesForAutoInstall"
                        or "AutoApplyAmbiguousInstallDestination" or "AutoInstallAfterAutomaticEstimation" => true,
                    "CustomTableRowHeight" => 31d,
                    "PlayerResamplingQuality" => 4,
                    "EncoderAmplifier" => (double)1.3f,
                    "LR2BackupNum" => 5,
                    "RightClickActionSettingsEditor.SelectedWebAction.Name" => "Saved custom action",
                    _ => throw new AssertFailedException(bindingPath)
                };
                Assert.AreNotEqual(before, expected);
                EditCategoryControl(window, category, bindingPath, expected);
                if (category == "RightClick")
                {
                    Button confirm = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:web"));
                    Task click = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, confirm)));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(click, "category record confirmation");
                }
                Assert.IsTrue(window.IsVisible, "通常確定・record確定のいずれも同じWindowを保ちます。");
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                window.CloseForOwnerShutdown();
                AssertCategoryRuntime(owner, bindingPath);
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(config);
                AssertCategoryPersisted(fresh, bindingPath);
                Assert.AreEqual(untouched, fresh.EncodeFileNameFormat);
                freshOwner = CreateViewModel(new SettingsEditSession(fresh), firstStartup: false);
                SetActiveLibraryProfile(freshOwner, true);
                var freshPresentation = new WindowClosingPresentationPort();
                freshOwner.SettingDialog.AttachPresentationPort(freshPresentation);
                reopened = OpenSettingsWindow(scope, freshOwner.SettingDialog);
                freshPresentation.CurrentWindow = reopened;
                Assert.AreEqual(expected, ReadCategoryEditor(reopened, category, bindingPath));
                if (category == "RightClick") { EditCategoryControl(reopened, category, bindingPath, before); }
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                reopened.Closed += (_, _) => cancelled.TrySetResult();
                freshOwner.SettingDialog.CloseCommand.Execute();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(cancelled.Task, "category Cancel close");
                AssertCategoryRuntime(freshOwner, bindingPath);
                AssertCategoryPersisted(PortableSettingsPersistenceTests.OpenSettings(config), bindingPath);
                reopened = OpenSettingsWindow(scope, freshOwner.SettingDialog);
                freshPresentation.CurrentWindow = reopened;
                Assert.AreEqual(expected, ReadCategoryEditor(reopened, category, bindingPath));
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                var cleanupFailures = new List<Exception>();
                void Cleanup(Action action)
                {
                    try { action(); }
                    catch (Exception failure) { cleanupFailures.Add(failure); }
                }
                if (save != null) { Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(save, "category save cleanup")); }
                if (window?.IsVisible == true) { Cleanup(window.CloseForOwnerShutdown); }
                if (reopened?.IsVisible == true) { Cleanup(reopened.CloseForOwnerShutdown); }
                foreach (MainWindowViewModel current in new[] { freshOwner, owner }.OfType<MainWindowViewModel>())
                {
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(current.ShellShutdownWorkflow.RequestWindowCloseAsync(), "category owner shutdown"));
                    Cleanup(current.SettingDialog.Dispose);
                }
                Cleanup(() => ResourceService.Current.ChangeCulture(culture));
                Cleanup(() => AppThemeService.ApplyTheme(theme));
                Cleanup(() => Directory.Delete(root, recursive: true));
                if (cleanupFailures.Count > 0)
                {
                    var cleanupFailure = new AggregateException("category fixture cleanup", cleanupFailures);
                    if (primaryFailure == null) { throw cleanupFailure; }
                    Console.WriteLine(cleanupFailure);
                }
            }
        });
    }

    /// <summary>カテゴリを実navigationで材料化し、該当Bindingを持つ編集可能なcontrolを選びます。</summary>
    private static (FrameworkElement Control, DependencyProperty Property) GetCategoryControl(SettingsWindow window, string category, string path)
    {
        ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigation" + category);
        TestUiDispatcherHost.ProcessQueuedPresentation();
        var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
        foreach (FrameworkElement control in FindDescendants<FrameworkElement>(page))
        {
            DependencyProperty? property = control switch
            {
                TextBox => TextBox.TextProperty,
                ComboBox => path == "Language" ? Selector.SelectedItemProperty : Selector.SelectedValueProperty,
                CheckBox => ToggleButton.IsCheckedProperty,
                Slider => RangeBase.ValueProperty,
                _ => null
            };
            if (property != null && control.GetBindingExpression(property)?.ParentBinding.Path?.Path == path)
            {
                Assert.IsTrue(control.IsEnabled, category + " editor must be enabled");
                return (control, property);
            }
        }
        throw new AssertFailedException("compiled editor Binding not found: " + category + "/" + path);
    }

    /// <summary>実Bindingのeditor値を読み、ViewModelへの直接代入で端末確認を代用しません。</summary>
    private static object ReadCategoryEditor(SettingsWindow window, string category, string path)
    {
        (FrameworkElement control, DependencyProperty property) = GetCategoryControl(window, category, path);
        return control.GetValue(property);
    }

    /// <summary>実controlを編集し、boolは標準の即時Binding伝播、それ以外は明示UpdateSourceで保存入力へ届けます。</summary>
    private static void EditCategoryControl(SettingsWindow window, string category, string path, object value)
    {
        (FrameworkElement control, DependencyProperty property) = GetCategoryControl(window, category, path);
        control.SetCurrentValue(property, value);
        if (control is TextBox text)
        {
            text.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, text));
        }
        else if (control is Slider slider)
        {
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent, Source = slider });
        }
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    /// <summary>本番compositionが実稼働側へ渡す窓口と同じactionの読取りから、保存後の指定値を確認します。</summary>
    private static void AssertCategoryRuntime(MainWindowViewModel owner, string bindingPath)
    {
        ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
        object ReadField(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? throw new AssertFailedException("runtime field unavailable: " + name);
        switch (bindingPath)
        {
            case "Language": Assert.AreEqual("en-US", Resources.Culture?.Name); break;
            case "FolderNameFormat": Assert.AreEqual("%ARTIST% - %TITLE%", composition.BmsLibraryOptionsProvider().FolderNameFormat); break;
            case "OverwritePlaylistUrlsWithCompletion": Assert.IsTrue(composition.PlaylistUrlCompletionOptionsProvider().OverwritePlaylistUrlsWithCompletion); break;
            case "CustomTableRowHeight": Assert.AreEqual(31d, composition.MainWindowViewSettingsStore.CustomTableRowHeight); break;
            case "IsSaveLR2bodyWindowPosition": Assert.IsTrue(((IPlayerSettingsGateway)ReadField(composition, "playerSettingsGateway")).CaptureSnapshot().IsSaveLR2bodyWindowPosition); break;
            case "PlayerResamplingQuality": Assert.AreEqual(4, ((IPlayerSettingsGateway)ReadField(composition, "playerSettingsGateway")).CaptureSnapshot().SampleRateConversionQuality); break;
            case "EncoderAmplifier": Assert.AreEqual(1.3f, ((IAudioSettingsGateway)ReadField(composition, "audioSettingsGateway")).CaptureEncodingSettings().EncoderAmplifier); break;
            case "LR2BackupNum": Assert.AreEqual(5, composition.StartupSettingsProvider().LR2BackupNum); break;
            case "InstallDestinationAutoApplyMinAudioHealth":
                Assert.AreEqual(97, composition.BmsLibraryOptionsProvider().InstallDestinationAutoApplyMinAudioHealth); break;
            case "KeepInstallablePackagesPending":
            case "RequireCompleteUsedResourcesForAutoInstall":
            case "AutoApplyAmbiguousInstallDestination":
            case "AutoInstallAfterAutomaticEstimation":
                Assert.IsTrue(bindingPath switch
                {
                    "RequireCompleteUsedResourcesForAutoInstall" => composition.BmsLibraryOptionsProvider().RequireCompleteUsedResourcesForAutoInstall,
                    "KeepInstallablePackagesPending" => composition.BmsLibraryOptionsProvider().KeepInstallablePackagesPending,
                    "AutoApplyAmbiguousInstallDestination" => composition.BmsLibraryOptionsProvider().AutoApplyAmbiguousInstallDestination,
                    "AutoInstallAfterAutomaticEstimation" => composition.BmsLibraryOptionsProvider().AutoInstallAfterAutomaticEstimation,
                    _ => throw new AssertFailedException(bindingPath)
                }); break;
            case "RightClickActionSettingsEditor.SelectedWebAction.Name": AssertCustomWebActions(((RightClickActionSettingsStore)ReadField(owner.SelectedChartExternalActions, "rightClickActionSettingsStore")).Load()); break;
        }
    }

    /// <summary>同じSettings instanceの再確認と分け、新providerの実永続値をBinding先ごとに確認します。</summary>
    private static void AssertCategoryPersisted(Settings settings, string bindingPath)
    {
        switch (bindingPath)
        {
            case "Language": Assert.AreEqual("en-US", settings.Lang); break;
            case "FolderNameFormat": Assert.AreEqual("%ARTIST% - %TITLE%", settings.FolderNameFormat); break;
            case "OverwritePlaylistUrlsWithCompletion": Assert.IsTrue(settings.OverwritePlaylistUrlsWithCompletion); break;
            case "CustomTableRowHeight": Assert.AreEqual(31d, settings.CustomTableRowHeight); break;
            case "IsSaveLR2bodyWindowPosition": Assert.IsTrue(settings.IsSaveLR2bodyWindowPosition); break;
            case "PlayerResamplingQuality": Assert.AreEqual(4, settings.PlayerResamplingQuality); break;
            case "EncoderAmplifier": Assert.AreEqual(1.3f, settings.EncoderAmplifier); break;
            case "LR2BackupNum": Assert.AreEqual(5, settings.LR2BackupNum); break;
            case "InstallDestinationAutoApplyMinAudioHealth":
                Assert.AreEqual(97, settings.InstallDestinationAutoApplyMinAudioHealth); break;
            case "KeepInstallablePackagesPending":
            case "RequireCompleteUsedResourcesForAutoInstall":
            case "AutoApplyAmbiguousInstallDestination":
            case "AutoInstallAfterAutomaticEstimation":
                Assert.IsTrue(bindingPath switch
                {
                    "RequireCompleteUsedResourcesForAutoInstall" => settings.RequireCompleteUsedResourcesForAutoInstall,
                    "KeepInstallablePackagesPending" => settings.KeepInstallablePackagesPending,
                    "AutoApplyAmbiguousInstallDestination" => settings.AutoApplyAmbiguousInstallDestination,
                    "AutoInstallAfterAutomaticEstimation" => settings.AutoInstallAfterAutomaticEstimation,
                    _ => throw new AssertFailedException(bindingPath)
                }); break;
            case "RightClickActionSettingsEditor.SelectedWebAction.Name": AssertCustomWebActions(new RightClickActionSettingsStore(() => settings).Load()); break;
        }
    }

    /// <summary>編集対象IDと別actionの指定値を保持し、名前だけが別actionへ誤適用された結果を拒否します。</summary>
    private static void AssertCustomWebActions(RightClickActionSettingsParseResult parsed)
    {
        Assert.IsNotNull(parsed.Settings, parsed.Error?.ToString());
        Assert.AreEqual(2, parsed.Settings.WebActions.Count);
        RightClickWebActionDefinition edited = parsed.Settings.WebActions.Single(action => action.Id == "edited");
        Assert.AreEqual("Saved custom action", edited.Name);
        Assert.AreEqual("https://example.com/{md5}", edited.UrlTemplate);
        Assert.IsTrue(edited.Enabled);
        Assert.AreEqual(ExternalChartKind.All, edited.ChartKind);
        RightClickWebActionDefinition kept = parsed.Settings.WebActions.Single(action => action.Id == "kept");
        Assert.AreEqual("Other", kept.Name);
        Assert.AreEqual("https://example.org/{md5}", kept.UrlTemplate);
        Assert.IsTrue(kept.Enabled);
        Assert.AreEqual(ExternalChartKind.BmsOnly, kept.ChartKind);
    }
}
