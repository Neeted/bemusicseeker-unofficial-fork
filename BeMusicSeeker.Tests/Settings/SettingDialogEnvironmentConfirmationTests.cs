using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
    /// <summary>確定スコア使用がONなら、有効rootでも解決player/score.dbが無効な環境を保存・確認・出力・再起動前に拒否します。</summary>
    [DataTestMethod]
    [DataRow(false)] // playerは存在しますが、そのscore.dbがありません。
    [DataRow(true)] // 構成にplayer名があっても、playerディレクトリがありません。
    public void EnvironmentConfirmation_InvalidResolvedScoreDatabaseRejectsBeforeSideEffects(bool missingPlayer)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string oldRoot = Path.Combine(root, "old-beatoraja");
            string newRoot = Path.Combine(root, "new-beatoraja");
            WriteEnvironmentBeatorajaRoot(oldRoot, "old-player", ["old-player"], createScoreFiles: true);
            WriteEnvironmentBeatorajaRoot(newRoot, "new-player", missingPlayer ? [] : ["new-player"], createScoreFiles: false);
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            values.BeatorajaRootPath = oldRoot;
            values.BeatorajaPlayerId = "old-player";
            values.BeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(oldRoot, "old-player");
            values.BeatorajaBmtTablePath = BeatorajaConfigService.GetTablePath(oldRoot);
            values.UseBeatorajaScoreDb = true;
            values.EnableBeatorajaBmtOutput = true;
            values.Save();
            byte[] savedBytes = File.ReadAllBytes(settingsPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                int restarts = 0;
                owner.ShellShutdownWorkflow.OperationModeRestartRequested += () => restarts++;
                SetActiveLibraryProfile(owner, true);
                try
                {
                    owner.SettingDialog.BeatorajaRootPath = newRoot;
                    owner.SettingDialog.BeatorajaPlayerId = "invalid-score-candidate";
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitEnvironmentAsync(), "environment-invalid-score");
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreEqual(Resources.Error_InvalidBeatorajaScoreDbPath, dialogs.LastMessageText);
                    Assert.AreEqual(0, dialogs.ConfirmationCount);
                    Assert.AreEqual(0, failures.Count);
                    Assert.AreEqual(0, restarts);
                    Assert.IsFalse(owner.ShellShutdownWorkflow.IsShutdownRequested);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    CollectionAssert.AreEqual(savedBytes, File.ReadAllBytes(settingsPath));
                    Assert.AreEqual(oldRoot, values.BeatorajaRootPath);
                    Assert.AreEqual(oldRoot, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BeatorajaRootPath);
                    Assert.IsFalse(Directory.Exists(BeatorajaConfigService.GetTablePath(newRoot)), "拒否では新環境の実BMT出力を開始しません。");
                    Assert.AreEqual(newRoot, owner.SettingDialog.BeatorajaRootPath);
                    Assert.IsTrue(owner.SettingDialog.UseBeatorajaScoreDb);
                    Assert.AreEqual("invalid-score-candidate", owner.SettingDialog.BeatorajaPlayerId);
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "environment-invalid-owner-terminal");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>実環境ボタンは局所差分・既存検証・実受付で有効状態を更新し、無効候補も取消できます。取消後は差分を消し、保存を行いません。</summary>
    [TestMethod]
    public void EnvironmentButtons_TrackLocalValidityAdmissionAndCancellation()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")));
            values.UseBeatorajaScoreDb = false;
            values.EnableBeatorajaBmtOutput = false;
            values.Save();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
            SetActiveLibraryProfile(owner, true);
            SettingsDialogViewModel settings = owner.SettingDialog;
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow { DataContext = settings };
                scope.ShowAndWaitForContentRendered(window);
                Button confirm = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:environment"));
                Button cancel = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "cancel:environment"));
                Assert.IsFalse(confirm.IsEnabled);
                Assert.IsFalse(cancel.IsEnabled);
                settings.BeatorajaRootPath = Path.Combine(root, "unused-root");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(confirm.IsEnabled);
                Assert.IsTrue(cancel.IsEnabled);
                Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable? libraryLease));
                using (libraryLease)
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(confirm.IsEnabled);
                    Assert.IsTrue(cancel.IsEnabled);
                }
                Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable? outputLease));
                using (outputLease)
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(confirm.IsEnabled);
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(confirm.IsEnabled);
                settings.OperationModeLR2DB = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(confirm.IsEnabled);
                Assert.IsTrue(cancel.IsEnabled);
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(confirm.IsEnabled);
                Assert.IsFalse(cancel.IsEnabled);
                Assert.IsFalse(settings.HasEnvironmentChanges);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "environment-button-owner-terminal");
                settings.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>実compiled環境欄の解決済み組をそのまま保存し、rootが変わってもスコア側の未確定選択・使用候補を保持して最新rootで検証します。</summary>
    [DataTestMethod]
    [DataRow(true)] // 設定されたplayerを採用します。
    [DataRow(false)] // 設定playerがない場合は従来どおり一覧の先頭を採用します。
    public void EnvironmentConfirmation_CompiledResolvedGroupIsSavedAndKeepsScoreCandidates(bool configuredPlayerExists)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            SettingsWindow? window = null;
            SettingsDialogViewModel? dialog = null;
            MainWindowViewModel? owner = null;
            try
            {
                string oldRoot = Path.Combine(root, "old-beatoraja");
                string newRoot = Path.Combine(root, "new-beatoraja");
                WriteEnvironmentBeatorajaRoot(oldRoot, "old-player", ["old-player", "draft-player"], createScoreFiles: true);
                WriteEnvironmentBeatorajaRoot(newRoot, configuredPlayerExists ? "z-player" : "missing", configuredPlayerExists ? ["a-player", "z-player", "draft-player"] : ["a-player", "z-player"], createScoreFiles: true);
                string settingsPath = Path.Combine(root, "settings", "user.config");
                Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(settingsPath));
                values.BeatorajaRootPath = oldRoot;
                values.BeatorajaPlayerId = "old-player";
                values.BeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(oldRoot, "old-player");
                values.BeatorajaBmtTablePath = BeatorajaConfigService.GetTablePath(oldRoot);
                values.UseBeatorajaScoreDb = true;
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService();
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                owner = composition.CreateMainWindowViewModelForTest();
                owner.SettingDialog.Dispose();
                dialog = new SettingsDialogViewModel(owner, owner.PlaylistWorkspace, owner.PlaylistWorkspace,
                    owner.PlayHistory, owner.LibraryFolderTree, composition, owner.PlaybackPanel, owner.Lr2SongDbSyncWorkflow,
                    new SettingsEditSession(values), reportApplyFailure: failures.Add, schemaDialogs: dialogs,
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    externalShellGateway: ExternalShellGatewayPolicy.Current, applicationPathSnapshot: ApplicationPathPolicy.Current,
                    audioDeviceCatalog: new TestAudioDeviceCatalog(), audioSettingsGateway: new SettingsAudioGateway(() => values),
                    audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
                window = new SettingsWindow { DataContext = dialog, PlaybackPanel = owner.PlaybackPanel };
                dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
                { if (request == "close") { window.CloseFromPresentation(); } }));
                scope.ShowAndWaitForContentRendered(window);
                ComboBox scorePlayer = FindDescendants<ComboBox>(window).Single(item => item.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BeatorajaPlayerId));
                File.Delete(BeatorajaConfigService.GetScoreDbPath(oldRoot, "draft-player"));
                scorePlayer.SetCurrentValue(ComboBox.SelectedItemProperty, "draft-player");
                dialog.BeatorajaRootPath = newRoot;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Expander environmentDetails = FindDescendants<Expander>(window).Single(item =>
                    FindLogicalDescendants<TextBox>(item).Any(text => text.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.EnvironmentBeatorajaScoreDbPath)));
                Assert.IsFalse(environmentDetails.IsExpanded);
                environmentDetails.IsExpanded = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                string expectedPlayer = configuredPlayerExists ? "z-player" : "a-player";
                TextBox player = FindDescendants<TextBox>(window).Single(item => item.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.EnvironmentBeatorajaPlayerId));
                TextBox score = FindDescendants<TextBox>(window).Single(item => item.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.EnvironmentBeatorajaScoreDbPath));
                TextBox table = FindDescendants<TextBox>(window).Single(item => item.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.EnvironmentBeatorajaBmtTablePath));
                Assert.AreEqual(expectedPlayer, player.Text);
                Assert.AreEqual(BeatorajaConfigService.GetScoreDbPath(newRoot, expectedPlayer), score.Text);
                Assert.AreEqual(BeatorajaConfigService.GetTablePath(newRoot), table.Text);
                Assert.AreEqual(oldRoot, values.BeatorajaRootPath);
                Assert.AreEqual("draft-player", scorePlayer.SelectedItem);
                string displayedScore = score.Text;
                string displayedTable = table.Text;
                // 表示後に外部configの解決値が変わっても、確定で別の組へ黙って再解決しません。
                WriteEnvironmentBeatorajaRoot(newRoot, configuredPlayerExists ? "a-player" : "z-player", ["a-player", "z-player"], createScoreFiles: true);
                Button confirm = FindDescendants<Button>(window).Single(item => Equals(item.Tag, "confirm:environment"));
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, confirm));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Settings saved = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.AreEqual(newRoot, saved.BeatorajaRootPath);
                Assert.AreEqual(expectedPlayer, saved.BeatorajaPlayerId);
                Assert.AreEqual(displayedScore, saved.BeatorajaScoreDbPath);
                Assert.AreEqual(displayedTable, saved.BeatorajaBmtTablePath);
                Assert.IsTrue(saved.UseBeatorajaScoreDb);
                Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                Assert.AreEqual("draft-player", scorePlayer.SelectedItem);
                Assert.IsTrue(dialog.UseBeatorajaScoreDb);
                Assert.AreEqual(BeatorajaConfigService.GetScoreDbPath(newRoot, "draft-player"), dialog.BeatorajaScoreDbPath);
                if (configuredPlayerExists)
                {
                    Button retry = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "retry:score"));
                    Assert.AreEqual(Visibility.Visible, retry.Visibility, "別区分の保存で有効になった候補は暗黙保存せず明示再試行を表示します。");
                    Assert.AreEqual(expectedPlayer, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BeatorajaPlayerId);
                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "environment-retained-valid-score-retry");
                    Assert.AreEqual("draft-player", PortableSettingsPersistenceTests.OpenSettings(settingsPath).BeatorajaPlayerId);
                }
                else
                {
                    int priorMessages = dialogs.MessageCount;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitScoreSettingsAsync(), "environment-retained-score-candidate-validation");
                    Assert.AreEqual(priorMessages + 1, dialogs.MessageCount);
                    Assert.AreEqual(Resources.Error_InvalidBeatorajaScoreDbPath, dialogs.LastMessageText);
                    Assert.AreEqual(expectedPlayer, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BeatorajaPlayerId);
                }
                // 同じ確定rootで別セクションを確定した後は、その最新値を環境欄に反映し、古い組へ戻しません。
                string nextPlayer = configuredPlayerExists ? "a-player" : "z-player";
                scorePlayer.SetCurrentValue(ComboBox.SelectedItemProperty, nextPlayer);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitScoreSettingsAsync(), "environment-follows-confirmed-score");
                Assert.AreEqual(nextPlayer, player.Text);
                Assert.AreEqual(BeatorajaConfigService.GetScoreDbPath(newRoot, nextPlayer), score.Text);
                string newTablePath = Path.Combine(newRoot, "later-tables");
                string beatorajaConfigPath = Path.Combine(newRoot, BeatorajaConfigService.ConfigFileName);
                var config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(beatorajaConfigPath));
                config["tablepath"] = "later-tables";
                File.WriteAllText(beatorajaConfigPath, config.ToString());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitBmtSettingsAsync(), "environment-follows-confirmed-bmt");
                Assert.AreEqual(newTablePath, table.Text);
                Assert.AreEqual(newTablePath, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BeatorajaBmtTablePath);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                if (dialog != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "environment-compiled-audio-terminal"); dialog.Dispose(); }
                if (owner != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "environment-compiled-owner-terminal"); }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static System.Collections.Generic.IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) { continue; }
            if (element is T match) { yield return match; }
            foreach (T nested in FindLogicalDescendants<T>(element)) { yield return nested; }
        }
    }

    /// <summary>機器を必要としない実beatoraja構成とplayer/score媒体を、ケース固有rootに作ります。</summary>
    private static void WriteEnvironmentBeatorajaRoot(string root, string configuredPlayer, IReadOnlyList<string> players, bool createScoreFiles)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(root, BeatorajaConfigService.ConfigFileName), System.Text.Json.JsonSerializer.Serialize(
            new { playerpath = "profiles", playername = configuredPlayer, tablepath = "tables", tableURL = Array.Empty<string>() }));
        foreach (string player in players)
        {
            string directory = Path.Combine(root, "profiles", player);
            Directory.CreateDirectory(directory);
            if (createScoreFiles) { File.WriteAllText(Path.Combine(directory, "score.db"), string.Empty); }
        }
    }

    /// <summary>環境だけを実保存して旧新BMT入力を固定し、実gzip・URL整理または出力失敗通知の後に同じ受理要求から終了へ進みます。他セクション候補は混入しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EnvironmentConfirmation_RealBmtTerminalAndFailureNotificationPrecedeShutdown(bool failBmtOutput)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string oldRoot = Path.Combine(root, "old-beatoraja");
            string newRoot = Path.Combine(root, "new-beatoraja");
            foreach (string path in new[] { oldRoot, newRoot })
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "beatoraja.jar"), string.Empty);
                File.WriteAllText(Path.Combine(path, BeatorajaConfigService.ConfigFileName), "{\"tablepath\":\"table\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
            }
            string configPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(configPath));
            values.BeatorajaRootPath = oldRoot;
            values.BeatorajaBmtTablePath = Path.Combine(oldRoot, "table");
            values.EnableBeatorajaBmtOutput = true;
            values.RegisterBeatorajaBmtUrls = true;
            values.Save();
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var events = new List<string>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                dialogs.MessageObserved = () =>
                {
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    Assert.AreEqual(newRoot, PortableSettingsPersistenceTests.OpenSettings(configPath).BeatorajaRootPath);
                    events.Add("notification");
                };
                SetActiveLibraryProfile(owner, true);
                string database = BmsPlaylistTestSupport.CreateTempSongDbPath(root);
                PlaylistPersistenceRepository.EnsureSchema(database);
                BMSPlaylist playlist = AttachPlaylistTables(owner, root, existingDatabasePath: database);
                var table = new BMSTable
                {
                    playlist_id = 671,
                    name = "Environment",
                    Folder_order = ["Folder"],
                    entries = [new BMSTableEntry { md5 = new string('a', 32), sha256 = new string('b', 64), title = "Song", folder = "Folder" }]
                };
                playlist.BMSTables.Add(table);
                Task? shutdown = null;
                try
                {
                    playlist.CommitBMSTablesWithEntriesToDB([table]);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(playlist.BmtOutput.ExportAllAsync("old-environment"), "environment-initial-bmt");
                    Assert.AreEqual(1, Directory.GetFiles(values.BeatorajaBmtTablePath, "*.bmt").Length);
                    if (failBmtOutput) { File.WriteAllText(Path.Combine(newRoot, "table"), "directory unavailable"); }
                    owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
                    {
                        if (request.Receipt.Notifications.Count == 0) { return; }
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                        Assert.AreEqual(newRoot, PortableSettingsPersistenceTests.OpenSettings(configPath).BeatorajaRootPath);
                        events.Add("notification");
                    };
                    owner.ShellShutdownWorkflow.OperationModeRestartRequested += () =>
                    {
                        events.Add("close");
                        shutdown = owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                    };
                    owner.SettingDialog.BeatorajaRootPath = newRoot;
                    owner.SettingDialog.UseBeatorajaScoreDb = true;
                    owner.SettingDialog.UseExternalPanelImage = true;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitEnvironmentAsync(), "environment-confirmation-bmt-terminal");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Settings saved = PortableSettingsPersistenceTests.OpenSettings(configPath);
                    Assert.AreEqual(newRoot, saved.BeatorajaRootPath);
                    Assert.IsFalse(saved.UseBeatorajaScoreDb);
                    Assert.IsFalse(saved.UseExternalPanelImage);
                    Assert.IsTrue(owner.SettingDialog.IsEditCompletionInProgress, "保存後のBMT失敗でも通常編集へ戻しません。");
                    Assert.IsNotNull(shutdown);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "environment-owned-shutdown");
                    CollectionAssert.AreEqual(new[] { "https://example.com/unmanaged" }, BeatorajaConfigService.ReadTableUrls(oldRoot).ToArray());
                    if (failBmtOutput)
                    {
                        Assert.IsTrue(events.IndexOf("notification") >= 0);
                        Assert.IsTrue(events.IndexOf("notification") < events.IndexOf("close"));
                    }
                    else
                    {
                        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(newRoot, "table"), "*.bmt").Length);
                        CollectionAssert.Contains(BeatorajaConfigService.ReadTableUrls(newRoot).ToArray(), "bemusicseeker://playlist/671");
                        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(oldRoot, "table"), "*.bmt").Length);
                    }
                }
                finally
                {
                    playlist.RequestShutdown("environment-bmt-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown ?? owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "environment-fixture-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
