using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings;
using BeMusicSeeker.Views.Settings.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Media.Audio;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>compiled 設定画面の操作・通知・保存 owner・終了への実接続を検証します。</summary>
/// <remarks>実 WPF は BAML のクリック配送、native Close、modal 復帰、表示寸法、Automation、live binding の観測に必要です。保存形式・判断条件は Settings の下位ケースが分担し、Application 辞書・culture・native 音声受付・ログを共有するため非並列にします。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class SettingsWindowPresentationTests
{
    private static void AssertPlaylistUriValidationClearedOnReopen(PlaylistUriCompletionRoute completionRoute)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel settings = owner.SettingDialog;
            var presentationPort = new RecordingPresentationPort();
            settings.AttachPresentationPort(presentationPort);
            Uri tableListUriBefore = settings.TableListURL;
            string mappingUriBefore = settings.PlaylistMd5UrlMappingTsvUri;
            SettingsWindow? firstPresentation = null;
            SettingsWindow? reopenedPresentation = null;
            try
            {
                firstPresentation = new SettingsWindow { DataContext = settings };
                presentationPort.CloseAction = firstPresentation!.CloseFromPresentation;
                windowTest.ShowAndWaitForContentRendered(firstPresentation!);
                SeedTransientPlaylistUriValidation(settings);

                var navigation = (ListBox)firstPresentation!.FindName("settingsNavigation");
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Name == "navigationPlaylist");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Name == "navigationGeneral");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Name == "navigationPlaylist");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(string.IsNullOrWhiteSpace(settings.TableListUriValidationMessage));
                Assert.IsFalse(string.IsNullOrWhiteSpace(settings.PlaylistMd5UrlMappingTsvUriValidationMessage));

                var firstClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler firstClosedHandler = (_, _) => firstClosed.TrySetResult();
                firstPresentation!.Closed += firstClosedHandler;
                try
                {
                    switch (completionRoute)
                    {
                        case PlaylistUriCompletionRoute.CancelButton:
                            ((Button)firstPresentation!.FindName("buttonCancel"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            break;
                        case PlaylistUriCompletionRoute.NativeClose:
                            firstPresentation!.Close();
                            break;
                        default:
                            Assert.Fail("Unsupported URI completion route: " + completionRoute);
                            break;
                    }
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(firstClosed.Task, "settings-first-presentation-closed");
                }
                finally { firstPresentation.Closed -= firstClosedHandler; }
                Assert.IsFalse(settings.IsEditCompletionInProgress);
                Assert.AreEqual(1, presentationPort.CloseRequestCount);
                Assert.AreEqual(
                    SettingsWindowCloseReason.UserClose,
                    firstPresentation!.CloseReason);
                Assert.IsNull(firstPresentation!.DataContext);
                Assert.IsFalse(string.IsNullOrWhiteSpace(settings.TableListUriValidationMessage),
                    "Closing a presentation must not hide the transient message before the next activation.");
                firstPresentation = null;

                var changedProperties = new List<string>();
                PropertyChangedEventHandler propertyChanged = (_, e) => changedProperties.Add(e.PropertyName ?? string.Empty);
                settings.PropertyChanged += propertyChanged;
                try
                {
                    reopenedPresentation = new SettingsWindow { DataContext = settings };
                    presentationPort.CloseAction = reopenedPresentation!.CloseFromPresentation;
                    windowTest.ShowAndWaitForContentRendered(reopenedPresentation!);
                }
                finally
                {
                    settings.PropertyChanged -= propertyChanged;
                }

                Assert.AreEqual(string.Empty, settings.TableListUriValidationMessage);
                Assert.AreEqual(string.Empty, settings.PlaylistMd5UrlMappingTsvUriValidationMessage);
                CollectionAssert.Contains(changedProperties, nameof(settings.TableListUriValidationMessage));
                CollectionAssert.Contains(changedProperties, nameof(settings.PlaylistMd5UrlMappingTsvUriValidationMessage));
                Assert.AreEqual(tableListUriBefore, settings.TableListURL);
                Assert.AreEqual(mappingUriBefore, settings.PlaylistMd5UrlMappingTsvUri);
                var reopenedClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler reopenedClosedHandler = (_, _) => reopenedClosed.TrySetResult();
                reopenedPresentation!.Closed += reopenedClosedHandler;
                try
                {
                    ((Button)reopenedPresentation!.FindName("buttonCancel"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(reopenedClosed.Task, "settings-reopened-presentation-closed");
                }
                finally { reopenedPresentation.Closed -= reopenedClosedHandler; }
                Assert.AreEqual(2, presentationPort.CloseRequestCount);
                Assert.AreEqual(SettingsWindowCloseReason.UserClose, reopenedPresentation!.CloseReason);
                Assert.IsNull(reopenedPresentation!.DataContext);
                reopenedPresentation = null;
            }
            finally
            {
                if (firstPresentation?.IsVisible == true)
                {
                    firstPresentation.CloseFromPresentation();
                }

                if (reopenedPresentation?.IsVisible == true)
                {
                    reopenedPresentation.CloseFromPresentation();
                }
            }
        });
    }

    /// <summary>一覧取得をworkerで保留しても実ページの切替・編集・通常Closeを処理し、取得完了を待たずにDataContextを解除します。</summary>
    /// <remarks>実DispatcherとBAMLの操作可否・Cancel/Close接続を確認し、保存値の条件網羅は音声一覧ownerテストに分担します。</remarks>
    [TestMethod]
    public void AudioListRefresh_KeepsNavigationEditingAndOrdinaryCloseResponsive()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousTheme = Settings.Default.AppearanceTheme;
            System.Globalization.CultureInfo? previousCulture = Resources.Culture;
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var catalog = new TestAudioDeviceCatalog
            {
                RefreshAction = () => { started.TrySetResult(); release.Wait(); }
            };
            Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(settings =>
            {
                settings.OperationModeLR2DB = false;
                settings.BMSRootPath = string.Empty;
                settings.StandaloneBmsRootPaths = string.Empty;
                settings.BMSInstallDir = string.Empty;
            });
            using SettingsDialogViewModel dialog = AudioDeviceListRefreshTests.CreateDialog(values, catalog,
                new AudioOutputSelection(AudioDriver.WasapiExclusive, "missing", "Missing device"));
            Logger logger = Ribbit.Logging.NLogWrapper.GetLogger(typeof(SettingsWindowPresentationTests));
            FieldInfo fileLoggerProvider = typeof(Ribbit.Logging.NLogWrapper)
                .GetField("_FileLogger", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertFailedException("File logger provider is unavailable.");
            object? previousFileLogger = fileLoggerProvider.GetValue(null);
            LoggingConfiguration? previousLogging = LogManager.Configuration;
            using var logs = new MemoryTarget { Layout = "${message}" };
            SettingsWindow? window = null;
            try
            {
                var configuration = new LoggingConfiguration();
                configuration.AddRule(LogLevel.Info, LogLevel.Fatal, logs, logger.Name);
                LogManager.Configuration = configuration;
                fileLoggerProvider.SetValue(null, (Func<Logger>)(() => logger));
                long requested = Stopwatch.GetTimestamp();
                window = new SettingsWindow { DataContext = dialog };
                window.SetPresentationRequestedTimestamp(requested);
                dialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
                {
                    if (request == "close") { window.CloseFromPresentation(); }
                }));
                window.Closed += (_, _) => closed.TrySetResult();
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.AreEqual(0, catalog.RefreshCount);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                navigation.SelectedItem = window.FindName("navigationAudio");
                window.UpdateLayout();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(started.Task, "audio-list-window-started");
                var page = (AudioSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                Assert.IsFalse(((Button)page.FindName("buttonPlayerTest")).IsEnabled);
                Assert.IsTrue(((ComboBox)page.FindName("comboBoxPlayerDriver")).IsEnabled);
                Assert.IsFalse(((Button)window.FindName("buttonOK")).IsEnabled, "初期設定の必須一覧が未設定なら開始は無効です。音声一覧取得は通常Closeを妨げません。");
                Assert.IsTrue(((Button)window.FindName("buttonCancel")).IsEnabled);
                navigation.SelectedItem = window.FindName("navigationGeneral");
                window.UpdateLayout();
                Assert.IsInstanceOfType<GeneralSettingsPage>(((ContentControl)window.FindName("settingsPageContent")).Content);
                dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
                window.Close();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "audio-list-ordinary-close");
                Assert.IsNull(window.DataContext);
                Assert.IsFalse(dialog.AudioSettingsCompletionTask.IsCompleted);
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-list-closed-consumer");
                Assert.IsNull(window.DataContext);
                Assert.AreEqual(1, catalog.RefreshCount);
                Assert.IsTrue(logs.Logs.Any(message => message.Contains("settings_dialog_first_render")
                    && message.Contains("requestToFirstRenderMs=")));
                Assert.IsTrue(logs.Logs.Any(message => message.Contains("settings_audio_device_list_worker")));
                Assert.IsTrue(logs.Logs.Any(message => message.Contains("settings_window_construct")));
                Assert.IsTrue(logs.Logs.Any(message => message.Contains("settings_page_construct_start")));
                Assert.IsTrue(logs.Logs.Any(message => message.Contains("settings_page_construct_end")));
            }
            finally
            {
                release.Set();
                try
                {
                    if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                    dialog.SetPresentationActive(false);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AudioSettingsCompletionTask, "audio-list-window-cleanup");
                }
                finally
                {
                    fileLoggerProvider.SetValue(null, previousFileLogger);
                    LogManager.Configuration = previousLogging;
                    Settings.Default.AppearanceTheme = previousTheme;
                    AppThemeService.ApplyTheme(previousTheme);
                    if (previousCulture is null) { Resources.Culture = null; }
                    else { ResourceService.Current.ChangeCulture(previousCulture.Name); }
                }
            }
        });
    }

    /// <summary>実アプリ由来データ削除の確認中は Close を拒否し、取消では Window と draft を保って schema 操作へ誤配送しないことを確認します。</summary>
    [TestMethod]
    public void AdvancedDangerZone_ApplicationDataActualButtonPreservesCancelContract()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2SongDBPath = "song.db";
            });
            var dialogs = new DangerDialogService { HoldConfirmation = true };
            var schemaDialog = new DangerSchemaDialogPort();
            var store = new DangerApplicationDataStore
            {
                Failure = null
            };
            DangerDialogContext context = CreateDangerDialog(values, dialogs, schemaDialog, store);
            bool draftValue = !context.Settings.ShowRecommUpdatedMsg;
            context.Settings.ShowRecommUpdatedMsg = draftValue;
            var presentation = new RecordingPresentationPort();
            context.Settings.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = context.Settings, RequestApplicationDataUninstall = (_, _) => throw new AssertFailedException("取消後の終了要求は不要です。") };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                ClickAdvancedDangerButton(window, Resources.Settings_uninstall_application_data);
                Assert.AreEqual(0, schemaDialog.CallCount);

                Assert.IsTrue(dialogs.ConfirmationStarted.Task.IsCompletedSuccessfully);
                Assert.AreEqual(1, dialogs.ConfirmationCount);
                StringAssert.Contains(dialogs.LastConfirmationRequest.MessageBoxText, Resources.Settings_candidates_discarded_on_exit);
                Assert.IsTrue(((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled, "無関係な通常項目は操作可能です。");
                window.Close();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, presentation.CloseRequestCount);
                dialogs.CompleteConfirmation(MessageBoxResult.Cancel);

                AwaitViewOperationTerminal(context.Settings);

                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(draftValue, context.Settings.ShowRecommUpdatedMsg);
                Assert.AreEqual(0, store.CallCount);
                Assert.AreEqual(0, dialogs.Messages.Count);

            }
            finally
            {
                dialogs.CompleteConfirmation(MessageBoxResult.Cancel);
                AwaitViewOperationTerminal(context.Settings);
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
                context.Settings.Dispose();
            }
        });
    }

    /// <summary>実 schema 削除ボタンは成功時だけ DB 削除・履歴無効化・再読込みへ進み、不正な結果は失敗通知にして DB と draft を保つことを確認します。</summary>
    [TestMethod]
    // 不正な削除結果と、TablesAndTriggers の成功を比較する。
    [DataRow(false)]
    [DataRow(true)]
    public void AdvancedDangerZone_SchemaUninstallActualButtonPreservesFailureAndSuccessOwnerContracts(bool success)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = CreateDangerSchemaScope(out Settings values, out string scoreDbPath);
            SettingsWindow? window = null;
            DangerDialogContext? context = null;
            try
            {
                var dialogs = new DangerDialogService();
                var schemaDialog = new DangerSchemaDialogPort
                {
                    ImmediateResult = success
                        ? Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers
                        : (Lr2PlayHistorySchemaUninstallMode)(-1)
                };
                var store = new DangerApplicationDataStore();
                context = CreateDangerDialog(values, dialogs, schemaDialog, store);
                bool draftValue = !context.Settings.ShowRecommUpdatedMsg;
                context.Settings.ShowRecommUpdatedMsg = draftValue;
                window = new SettingsWindow { DataContext = context.Settings };
                windowTest.ShowAndWaitForContentRendered(window);
                ClickAdvancedDangerButton(window, Resources.Lr2_play_history_schema_uninstall);
                AwaitViewOperationTerminal(context.Settings);

                Assert.AreEqual(1, schemaDialog.CallCount);
                Assert.AreEqual(0, store.CallCount);
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(draftValue, context.Settings.ShowRecommUpdatedMsg);
                if (success)
                {
                    Assert.AreEqual(1, context.PlayHistory.InvalidationCount);
                    Assert.AreEqual(1, context.ReloadCount());
                    AssertInstalledSchema(scoreDbPath, expectedInstalled: false);
                    Assert.AreEqual(Resources.Msg_success_lr2_play_history_schema_uninstall, dialogs.Messages.Single().MessageBoxText);
                }
                else
                {
                    Assert.AreEqual(0, context.PlayHistory.InvalidationCount);
                    Assert.AreEqual(0, context.ReloadCount());
                    AssertInstalledSchema(scoreDbPath, expectedInstalled: true);
                    StringAssert.Contains(dialogs.Messages.Single().MessageBoxText, Resources.Msg_error_unexpected);
                }
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                context?.Settings.Dispose();
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    /// <summary>有効なLR2環境のschema確認待ちでは再操作・撤去の無効表示と入口拒否を揃えます。通常Close要求と実Taskの終端を分け、取消後は同じDBで操作を再開します。</summary>
    [TestMethod]
    public void AdvancedDangerZone_SchemaUninstallActualButtonKeepsTaskUntilCancel()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = CreateDangerSchemaScope(out Settings values, out string scoreDbPath);
            SettingsWindow? window = null;
            DangerDialogContext? context = null;
            var schemaDialog = new DangerSchemaDialogPort { HoldResult = true };
            try
            {
                var dialogs = new DangerDialogService();
                var store = new DangerApplicationDataStore();
                context = CreateDangerDialog(values, dialogs, schemaDialog, store);
                bool draftValue = !context.Settings.ShowRecommUpdatedMsg;
                context.Settings.ShowRecommUpdatedMsg = draftValue;
                var presentation = new RecordingPresentationPort();
                context.Settings.AttachPresentationPort(presentation);
                window = new SettingsWindow { DataContext = context.Settings };
                windowTest.ShowAndWaitForContentRendered(window);
                ClickAdvancedDangerButton(window, Resources.Lr2_play_history_schema_uninstall);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(schemaDialog.Started.Task, "settings-schema-dialog-started");
                Assert.IsTrue(((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled, "無関係な通常項目は操作可能です。");
                Button schemaButton = FindDescendants<Button>(window).Single(button => Equals(button.Content, Resources.Lr2_play_history_schema_uninstall));
                Button uninstallButton = FindDescendants<Button>(window).Single(button => Equals(button.Content, Resources.Settings_uninstall_application_data));
                Assert.IsFalse(schemaButton.IsEnabled);
                Assert.IsFalse(uninstallButton.IsEnabled);
                Task original = context.Settings.SettingsOperationCompletionTask;
                schemaButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                uninstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1, schemaDialog.CallCount);
                Assert.AreEqual(0, dialogs.Messages.Count);
                Assert.AreSame(original, context.Settings.SettingsOperationCompletionTask);
                Assert.IsFalse(original.IsCompleted);
                window.Close();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(1, presentation.CloseRequestCount, "通常Closeは実処理の取消や完了とは別に受け付けます。");
                Assert.AreEqual(0, store.CallCount);

                schemaDialog.Complete(UiInteractionStatus.CancelledByUser);
                AwaitViewOperationTerminal(context.Settings);

                Assert.IsTrue(window.IsVisible);
                Assert.IsTrue(schemaButton.IsEnabled);
                Assert.IsTrue(uninstallButton.IsEnabled);
                Assert.AreEqual(draftValue, context.Settings.ShowRecommUpdatedMsg);
                Assert.AreEqual(0, context.PlayHistory.InvalidationCount);
                Assert.AreEqual(0, context.ReloadCount());
                AssertInstalledSchema(scoreDbPath, expectedInstalled: true);
            }
            finally
            {
                schemaDialog.Complete(UiInteractionStatus.CancelledByUser);
                if (context != null) { AwaitViewOperationTerminal(context.Settings); }
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                context?.Settings.Dispose();
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    /// <summary>実 schema 操作の確認取消では DB を変えず、承認では実 DB を導入して表示・再読込み・成功通知を更新し、未保存draftを保ちます。成功通知のFailed/Task faultでも成功済みDBと必要再読込みを保ち、重複通知しません。</summary>
    [TestMethod]
    // 確認取消と、承認後の実 schema 導入を比較する。
    [DataRow(false, "accepted")]
    [DataRow(true, "accepted")]
    [DataRow(true, "failed-result")]
    [DataRow(true, "task-fault")]
    public void GeneralSchema_InstallOrRepairActualButtonPreservesCancelAndSuccessOwnerContracts(bool accept, string noticeOutcome)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = CreateDangerSchemaScope(out Settings values, out string scoreDbPath);
            SettingsWindow? window = null;
            DangerDialogContext? context = null;
            try
            {
                Lr2PlayHistorySchemaCheckResult resetResult = new Lr2PlayHistorySchemaService().Uninstall(
                    scoreDbPath,
                    isLr2LinkedProfile: true,
                    Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers);
                Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, resetResult.Status);

                var dialogs = new DangerDialogService
                {
                    ImmediateConfirmationResult = accept ? MessageBoxResult.OK : MessageBoxResult.Cancel,
                    MessageResult = noticeOutcome == "failed-result"
                        ? UiDialogResult.Failed(new IOException("schema success notice failed"))
                        : UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                    MessageTaskFailure = noticeOutcome == "task-fault"
                        ? new IOException("schema success notice Task failed") : null
                };
                context = CreateDangerDialog(
                    values,
                    dialogs,
                    new DangerSchemaDialogPort(),
                    new DangerApplicationDataStore());
                bool draftValue = !context.Settings.ShowRecommUpdatedMsg;
                context.Settings.ShowRecommUpdatedMsg = draftValue;
                window = new SettingsWindow { DataContext = context.Settings };
                windowTest.ShowAndWaitForContentRendered(window);

                var generalPage = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                SettingsSection schemaSection = FindDescendants<SettingsSection>(generalPage)
                    .Single(section => Equals(section.Header, Resources.Lr2_play_history_schema_label));
                SettingsStatusBanner schemaBanner = FindDescendants<SettingsStatusBanner>(schemaSection)
                    .Single(candidate => candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                        == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaStatusText));
                Button installOrRepairButton = FindDescendants<Button>(schemaSection)
                    .Single(candidate => candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                        == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaInstallOrRepairButtonText));

                Assert.IsTrue(installOrRepairButton.IsEnabled,
                    "The compiled General page install/repair button must be enabled for a NotInstalled schema.");
                installOrRepairButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, installOrRepairButton));

                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialogs.ConfirmationStarted.Task, "settings-schema-confirmation");
                Assert.IsNotNull(dialogs.LastConfirmationRequest);
                StringAssert.Contains(dialogs.LastConfirmationRequest.MessageBoxText, scoreDbPath);
                AwaitViewOperationTerminal(context.Settings);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, dialogs.ConfirmationCount);
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(draftValue, context.Settings.ShowRecommUpdatedMsg);
                Assert.AreEqual(0, context.SettingsSession.SaveCount);

                if (accept)
                {
                    AssertInstalledSchema(scoreDbPath, expectedInstalled: true);
                    Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, context.Settings.Lr2PlayHistorySchemaStatusSnapshot.Status);
                    Assert.IsFalse(context.Settings.CanInstallOrRepairLr2PlayHistorySchema);
                    Assert.IsFalse(installOrRepairButton.IsEnabled);
                    AssertSchemaBannerState(schemaBanner, "i", "Information");
                    Assert.AreEqual(1, context.PlayHistory.InvalidationCount);
                    Assert.AreEqual("lr2_play_history_schema_install_or_repair", context.PlayHistory.LastInvalidationReason);
                    Assert.AreEqual(1, context.ReloadCount());
                    Assert.AreEqual(1, dialogs.Messages.Count);
                    Assert.AreEqual(Resources.Msg_success_lr2_play_history_schema_install_or_repair, dialogs.Messages.Single().MessageBoxText);
                }
                else
                {
                    AssertInstalledSchema(scoreDbPath, expectedInstalled: false);
                    Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, context.Settings.Lr2PlayHistorySchemaStatusSnapshot.Status);
                    Assert.IsTrue(context.Settings.CanInstallOrRepairLr2PlayHistorySchema);
                    Assert.IsTrue(installOrRepairButton.IsEnabled);
                    AssertSchemaBannerState(schemaBanner, "!", "Warning");
                    Assert.AreEqual(0, context.PlayHistory.InvalidationCount);
                    Assert.IsNull(context.PlayHistory.LastInvalidationReason);
                    Assert.AreEqual(0, context.ReloadCount());
                    Assert.AreEqual(0, dialogs.Messages.Count);
                }
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    if (context != null) { AwaitViewOperationTerminal(context.Settings); }
                    window.CloseForOwnerShutdown();
                }
                context?.Settings.Dispose();
                Directory.Delete(scope, recursive: true);
            }
        });
    }


    [TestInitialize]
    public void MaterializeCanonicalApplicationResources()
    {
        TestUiDispatcherHost.Invoke(EnsureCanonicalApplicationResources);
    }

    /// <summary>実同期ボタンの確認取消・実受付拒否・受理成功を分け、実受付前の画面/環境候補保持と、受理後閉鎖・強制要求・実Task終端の所有を確認します。</summary>
    /// <remarks>compiled buttonの接続をここで、実L/Pの競合と準備0をBmsLibraryLr2SongDbSyncTestsで分担します。</remarks>
    [DataTestMethod]
    [DataRow("cancel")]
    [DataRow("busy")]
    [DataRow("accepted")]
    public void SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow(string outcome)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateDangerSchemaScope(out Settings values, out _);
            try
            {
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
                using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
                var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var runtime = new RecordingManualResyncRuntime { Admitted = outcome == "accepted", AdmissionWait = releaseAdmission.Task, TerminalWait = releaseTerminal.Task };
                using SettingsDialogViewModel settings = CreateManualResyncSettingsDialog(owner, runtime, values);
                string draft = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "candidate")).FullName, "song.db");
                StartupLibraryConstructionTestSupport.CreateSongDatabase(draft);
                var dialogs = new DangerDialogService { HoldConfirmation = true };
                var ownerWindow = new Window { Width = 320, Height = 200, ShowInTaskbar = false };
                var window = new SettingsWindow(dialogs) { DataContext = settings };
                Task? terminal = null;
                try
                {
                    scope.PrepareForOwnedPresentation(ownerWindow);
                    ownerWindow.Show(); ownerWindow.UpdateLayout();
                    window.Owner = ownerWindow;
                    scope.ShowAndWaitForContentRendered(window);
                    settings.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), draft).GetAwaiter().GetResult();
                    settings.EnableBeatorajaBmtOutput = !values.EnableBeatorajaBmtOutput;
                    Button button = FindDescendants<Button>(window).Single(candidate => Equals(candidate.Content, Resources.Lr2_song_db_sync_data_resync));
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    terminal = settings.SettingsOperationCompletionTask;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialogs.ConfirmationStarted.Task, "manual confirmation");
                    StringAssert.Contains(dialogs.LastConfirmationRequest.MessageBoxText, Resources.Settings_lr2_detailed_paths);
                    Assert.AreSame(window, dialogs.LastConfirmationRequest.Owner);
                    Assert.IsTrue(window.IsVisible);
                    Assert.AreEqual(draft, settings.LR2SongDBPath);
                    Assert.AreEqual(0, runtime.QueueCount);
                    dialogs.CompleteConfirmation(outcome == "cancel" ? MessageBoxResult.Cancel : MessageBoxResult.OK);
                    if (outcome != "cancel")
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(runtime.QueueAccepted.Task, "manual runtime reached");
                        Assert.IsTrue(window.IsVisible, "runtimeへの委譲だけではまだ受理されていません。");
                        Assert.AreEqual(draft, settings.LR2SongDBPath);
                    }
                    releaseAdmission.TrySetResult();
                    if (outcome == "accepted")
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(runtime.AdmissionReported.Task, "manual accepted");
                        Assert.IsFalse(window.IsVisible);
                        Assert.AreEqual(Path.Combine(root, "LR2files", "Database", "song.db"), settings.LR2SongDBPath);
                        Assert.AreNotEqual(values.EnableBeatorajaBmtOutput, settings.EnableBeatorajaBmtOutput, "手動閉鎖でも無関係な候補を実処理へ混ぜません。");
                        Assert.IsTrue(settings.CanCancelBmtSettings, "手動同期と無関係なBMT候補の同期取消可否は保持します。");
                        Assert.IsFalse(terminal.IsCompleted, "閉鎖後も実同期を追跡します。");
                    }
                    releaseTerminal.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "manual terminal");
                    Assert.AreEqual(outcome == "cancel" ? 0 : 1, runtime.QueueCount);
                    if (outcome != "accepted")
                    {
                        Assert.IsTrue(window.IsVisible);
                        Assert.AreEqual(draft, settings.LR2SongDBPath);
                        Assert.IsTrue(settings.CanCancelBmtSettings);
                    }
                    Assert.AreEqual(Path.Combine(root, "LR2files", "Database", "song.db"), values.LR2SongDBPath, "候補を確定環境へ便乗保存しません。");
                    Assert.AreEqual(1, dialogs.ConfirmationCount);
                }
                finally
                {
                    dialogs.CompleteConfirmation(MessageBoxResult.Cancel);
                    releaseAdmission.TrySetResult(); releaseTerminal.TrySetResult();
                    try { if (terminal != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "manual cleanup"); } }
                    finally
                    {
                        try { window.CloseForOwnerShutdown(); }
                        finally { ownerWindow.Close(); }
                    }
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    /// <summary>実表示した分類と本文viewerはclient端へ届き、本文とheaderの16 DIPを一回だけ適用することを測定して二重insetを検出します。</summary>
    [TestMethod]
    public void SettingsWindow_SidebarReachesNativeClientEdgeWhilePageKeepsInnerSpacing()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                window.UpdateLayout();

                var client = (FrameworkElement)window.Content;
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var pageHeader = (ContentControl)window.FindName("settingsPageHeader");
                var body = (ScrollViewer)window.FindName("settingsPageScrollViewer");
                var page = (ContentControl)window.FindName("settingsPageContent");
                var presenter = (ScrollContentPresenter)body.Template.FindName("PART_ScrollContentPresenter", body);
                Rect navigationBounds = GetVisualBounds(client, navigation);
                Rect bodyBounds = GetVisualBounds(client, body);
                Rect headerBounds = GetVisualBounds(client, pageHeader);
                Rect pageBounds = GetVisualBounds(presenter, page);
                Assert.AreEqual(0d, navigationBounds.Left, 0.25d, "分類はclient左端へ届く。");
                Assert.AreEqual(client.ActualWidth, bodyBounds.Right, 0.25d, "本文viewerはclient右端へ届く。");
                Assert.AreEqual(16d, GetVisualBounds(body, presenter).Left, 0.25d, "本文左余白は一度だけ適用する。");
                Assert.AreEqual(0d, pageBounds.Left, 0.25d);
                Assert.AreEqual(presenter.ActualWidth, pageBounds.Right, 0.25d);
                Assert.AreEqual(headerBounds.Left, GetVisualBounds(client, page).Left, 0.25d);
                Assert.AreEqual(new Thickness(16), body.Padding);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }

                owner.SettingDialog.Dispose();
            }
        });
    }

    /// <summary>実Buttonの実行file選択を現在の右クリック行へ反映し、別のBrowseRequested入口の同期失敗・失敗通知の失敗でもdraftと選択を保持して再通知しません。</summary>
    [TestMethod]
    public void SettingsWindow_ExecutablePickerUpdatesSelectedRightClickDraft()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = "{\"webActions\":[],\"programActions\":[]}";
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
            }));
            var dialogs = new RecordingSettingsRouteDialogService
            {
                FileResult = new UiFilePickerResult(
                    UiDialogStatus.Accepted,
                    fileNames: [@"C:\Tools\Chart Viewer.exe"])
            };
            var window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog };
            dialogs.ExpectedOwner = window;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");

                owner.SettingDialog.RightClickActionSettingsEditor.AddProgramAction();
                navigation.SelectedItem = window.FindName("navigationRightClick");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();
                var rightClickPage = (RightClickSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                FindDescendants<Button>(rightClickPage).Single(button =>
                    AutomationProperties.GetAutomationId(button) == "RightClickProgramExecutablePicker")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, dialogs.FileRequests.Count);
                UiFilePickerRequest request = dialogs.FileRequests[0];
                Assert.AreSame(window, request.Owner);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.RightClick_executable_filter, request.Filter);
                Assert.AreEqual(string.Empty, request.FileName);
                RightClickProgramActionEditorRow selected = owner.SettingDialog
                    .RightClickActionSettingsEditor.SelectedProgramAction;
                Assert.AreEqual(@"C:\Tools\Chart Viewer.exe", selected.ExecutablePath);
                Assert.AreEqual("Chart Viewer", selected.Name);
                string previousUbmplay = owner.SettingDialog.uBMplayPath;
                dialogs.FileSynchronousFailure = new IOException("executable picker synchronous failure");
                dialogs.MessageResult = UiDialogResult.Failed(new IOException("executable notice failure"));
                navigation.SelectedItem = window.FindName("navigationPlayback");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var playbackPage = (PlaybackSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                var ubmplay = (RadioButton)playbackPage.FindName("radioButtonPlayuBMplay");
                typeof(RadioButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ubmplay, null);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();
                SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(playbackPage).Single(candidate =>
                    candidate.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path
                        == nameof(SettingsDialogViewModel.uBMplayPath));
                Assert.IsTrue(picker.IsVisible, "選択した外部playerの可視pickerから実BrowseRequestedへ進みます。");
                FindDescendants<Button>(picker).Single(button => Equals(button.Content, picker.BrowseText))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(previousUbmplay, owner.SettingDialog.uBMplayPath);
                Assert.AreSame(selected, owner.SettingDialog.RightClickActionSettingsEditor.SelectedProgramAction);
                Assert.AreEqual(@"C:\Tools\Chart Viewer.exe", selected.ExecutablePath);
                Assert.AreEqual(1, dialogs.MessageRequests.Count, "失敗通知の失敗を再通知しない。");
            }
            finally
            {
                try
                {
                    if (window.IsVisible) { window.CloseForOwnerShutdown(); }
                }
                finally { owner.SettingDialog.Dispose(); }
            }
        });
    }

    /// <summary>実復元ボタンを押すと確認なしで編集器を既定 JSON に変え、保存済み JSON は変更しないことを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_RightClickRestoreDefaultsIsEnabledForValidDraftAndDoesNotPrompt()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const string originalJson = "{\"webActions\":[{\"id\":\"custom\",\"name\":\"Custom\",\"urlTemplate\":\"https://example.test/{md5}\",\"enabled\":true,\"chartKind\":\"All\"}],\"programActions\":[{\"id\":\"viewer\",\"name\":\"Viewer\",\"executablePath\":\"C:\\\\Tools\\\\viewer.exe\",\"argumentTemplate\":\"{filePath}\",\"enabled\":true}]}";
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = originalJson;
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
            });
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
            using BeMusicSeeker.ViewModels.SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
            var dialogs = new RecordingSettingsRouteDialogService();
            var window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog };
            dialogs.ExpectedOwner = window;
            try
            {
                window.Measure(new Size(820, 760));
                window.Arrange(new Rect(0, 0, 820, 760));
                window.UpdateLayout();
                var navigation = (ListBox)window.FindName("settingsNavigation");
                navigation.SelectedItem = window.FindName("navigationRightClick");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (RightClickSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                Button restoreButton = FindDescendants<Button>(page).Single(button =>
                    AutomationProperties.GetAutomationId(button) == "RightClickRestoreDefaults");

                Assert.IsTrue(restoreButton.IsEnabled);
                restoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, restoreButton));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                RightClickActionSettingsEditor editor = owner.SettingDialog.RightClickActionSettingsEditor;
                Assert.IsFalse(editor.IsDirty);
                Assert.IsFalse(editor.IsInvalidPersistedSettings);
                Assert.IsTrue(editor.TryPrepareSave(out string restoredJson, out string error), error);
                Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, restoredJson);
                Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, settings.RightClickActionsJson);
                Assert.AreEqual(0, dialogs.ConfirmationRequests.Count);
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }


    /// <summary>URI の一時エラーを持つ未変更画面を取消ボタンで閉じ、再表示時だけ両エラーを通知付きで解除し保存 URI を保つことを確認します。</summary>
    [TestMethod]
    public void PlaylistUriValidation_CancelWithoutDraftChangesStartsNextPresentationClean()
    {
        AssertPlaylistUriValidationClearedOnReopen(PlaylistUriCompletionRoute.CancelButton);
    }

    /// <summary>URI の一時エラーを持つ未変更画面を native Close で閉じ、次の表示で両エラーを解除して以前の表示状態を引き継がないことを確認します。</summary>
    [TestMethod]
    public void PlaylistUriValidation_NativeCloseWithoutDraftChangesStartsNextPresentationClean()
    {
        AssertPlaylistUriValidationClearedOnReopen(PlaylistUriCompletionRoute.NativeClose);
    }

    /// <summary>履歴表示要求に設定 Window を owner として渡し、生成した履歴画面へ同じ設定を渡して draft を変更しないことを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_ReleaseNotesUsesInjectedOwnedWindowRouteWithoutChangingDraft()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var dialogs = new RecordingReleaseNotesDialogService();
            var window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog };
            dialogs.ExpectedOwner = window;
            string folderNameFormatBefore = owner.SettingDialog.FolderNameFormat;

            window.HandleShowReleaseNotesAsync().GetAwaiter().GetResult();

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreSame(owner.SettingDialog, dialogs.CreatedWindow!.DataContext);
            Assert.AreEqual(WindowStartupLocation.CenterOwner, dialogs.CreatedWindow.WindowStartupLocation);
            Assert.IsFalse(dialogs.CreatedWindow.ShowInTaskbar);
            Assert.AreEqual(folderNameFormatBefore, owner.SettingDialog.FolderNameFormat);
        });
    }

    /// <summary>導入先・検索 root・追加出力先・backup・restore・履歴 preset の各要求に正しい owner と選択条件を渡し、候補破棄確認とrestoreの取消／picker失敗を同じ注入先へ配送することを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_RemainingDialogRoutesUseInjectedServiceAndPreserveOutcomeContracts()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(Path.GetTempPath(), "bemusicseeker-settings-dialog-routes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            string installDirectory = Path.Combine(scope, "install");
            string searchRoot = Path.Combine(scope, "search-root");
            string additionalOutputBase = Path.Combine(scope, "additional-output");
            Directory.CreateDirectory(installDirectory);
            Directory.CreateDirectory(searchRoot);
            Directory.CreateDirectory(additionalOutputBase);

            SettingsWindow? window = null;
            try
            {
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            });
                var dialogs = new RecordingSettingsRouteDialogService(searchRoot, additionalOutputBase);
                var composition = new ApplicationComposition(
                    settingsEditSession: new NoOpSettingsEditSession(settings), settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                using BeMusicSeeker.ViewModels.SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
                window = new SettingsWindow(dialogs)
                {
                    DataContext = owner.SettingDialog,
                    PlaylistWorkspace = owner.PlaylistWorkspace
                };
                windowTest.ShowAndWaitForContentRendered(window);
                dialogs.ExpectedOwner = window;

                Assert.IsNotNull(window.PlaylistWorkspace.PlaylistTreeTables);
                window.HandleAddBmsInstallDirectory();
                window.HandleAddBmsSearchRootPathsAsync().GetAwaiter().GetResult();
                dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.HandleAddCustomFolderAdditionalOutputBase(), "additional-output-route-terminal");
                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count, "BMS候補から出力への切替は同じ設定確認サービスへ一回配送します。");
                StringAssert.Contains(dialogs.ConfirmationRequests[0].MessageBoxText, Resources.Standalone_BMSDirectories.Trim().TrimEnd(':', '：'));
                window.HandlePlaylistBackupAsync().GetAwaiter().GetResult();

                dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);
                window.HandlePlaylistRestoreAsync().GetAwaiter().GetResult();

                dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes);
                dialogs.FileResult = new UiFilePickerResult(
                    UiDialogStatus.Failed,
                    error: new InvalidOperationException("restore picker failed"));
                Assert.ThrowsException<InvalidOperationException>(
                    () => window.HandlePlaylistRestoreAsync().GetAwaiter().GetResult());

                window.HandleAddPlayHistoryFolderDisplayPreset();

                Assert.AreEqual(2, dialogs.FolderRequests.Count);
                Assert.IsTrue(dialogs.FolderRequests[0].Multiselect);
                Assert.IsTrue(dialogs.FolderRequests[1].Multiselect);
                Assert.IsTrue(dialogs.FolderRequests.All(request => ReferenceEquals(window, request.Owner)));
                Assert.AreEqual(1, dialogs.SaveFileRequests.Count);
                Assert.AreEqual("BeMusicSeeker_backup.sql", dialogs.SaveFileRequests[0].FileName);
                Assert.AreEqual(".sql", dialogs.SaveFileRequests[0].DefaultExtension);
                Assert.AreEqual(Resources.Sql_file_exts, dialogs.SaveFileRequests[0].Filter);
                Assert.IsTrue(dialogs.SaveFileRequests[0].AddExtension);
                Assert.AreSame(window, dialogs.SaveFileRequests[0].Owner);
                Assert.AreEqual(3, dialogs.ConfirmationRequests.Count);
                UiConfirmationRequest[] restoreConfirmations = dialogs.ConfirmationRequests
                    .Where(request => request.MessageBoxText.StartsWith(Resources.Confirm_RestorePlaylistBackup, StringComparison.Ordinal)).ToArray();
                Assert.AreEqual(2, restoreConfirmations.Length);
                Assert.IsTrue(restoreConfirmations.All(request => ReferenceEquals(window, request.Owner)));
                Assert.AreEqual(1, dialogs.FileRequests.Count);
                Assert.AreEqual(UiDialogStatus.Failed, dialogs.FileResult.Status);
                Assert.AreSame(window, dialogs.FileRequests[0].Owner);
                Assert.AreEqual(typeof(PlayHistoryFolderDisplayPresetEditDialog), dialogs.LastWindowType);
                Assert.AreSame(window, dialogs.LastWindowOwner);
                Assert.IsInstanceOfType<PlayHistoryFolderDisplayPresetEditDialog>(dialogs.LastCreatedWindow);
                CollectionAssert.DoesNotContain(owner.SettingDialog.AvailableBMSDirectories, searchRoot);
                CollectionAssert.AreEqual(owner.SettingDialog.ConfirmedBMSDirectories.ToArray(), owner.SettingDialog.AvailableBMSDirectories);
                CollectionAssert.Contains(owner.SettingDialog.CustomFolderAdditionalOutputBaseDirList, additionalOutputBase);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    /// <summary>出力への候補切替確認の表示不能をTaskの失敗として保ち、実Buttonでは一回通知して回収します。失敗では実設定・BMS候補・未受理出力を保持し、次の明示操作だけが保存します。</summary>
    /// <remarks>WindowとViewModelへ同じ確認サービスを構成し、OwnerUnavailableと元例外付きFailedを取消・承認から区別します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SettingsWindow_OutputCandidateConfirmationFailurePreservesCauseAndAllowsNextExplicitClick(bool failedWithException)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "settings-output-confirmation-failure-" + Guid.NewGuid().ToString("N"));
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            Exception? primaryFailure = null;
            var unhandled = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler onUnhandled = (_, args) => { unhandled.Add(args.Exception); args.Handled = true; };
            try
            {
                string install = Directory.CreateDirectory(Path.Combine(directory, "Install")).FullName;
                string staged = Directory.CreateDirectory(Path.Combine(directory, "Staged")).FullName;
                string additional = Directory.CreateDirectory(Path.Combine(directory, "Additional")).FullName;
                string settingsPath = Path.Combine(directory, "user.config");
                Settings settings = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                settings.OperationModeLR2DB = false;
                settings.BMSRootPath = install;
                settings.StandaloneBmsRootPaths = install;
                settings.BMSInstallDir = install;
                settings.LR2CustomFolderOutputBaseDir = string.Empty;
                settings.LR2CustomFolderOutputBaseDirRootType = string.Empty;
                settings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
                settings.Save();
                var original = new IOException("output confirmation presenter failed");
                var dialogs = new RecordingSettingsRouteDialogService
                {
                    ConfirmationResult = failedWithException ? UiDialogResult.Failed(original) : UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)
                };
                var composition = new ApplicationComposition(
                    settingsEditSession: new SettingsEditSession(settings), settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                owner = composition.CreateMainWindowViewModelForTest();
                window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog, PlaylistWorkspace = owner.PlaylistWorkspace };
                window.Dispatcher.UnhandledException += onUnhandled;
                windowTest.ShowAndWaitForContentRendered(window);
                dialogs.ExpectedOwner = window;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.AddBmsSearchRootPathsAsync([staged]), "output-confirmation-bms-input");
                byte[] before = File.ReadAllBytes(settingsPath);
                void AssertUnaccepted()
                {
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(settingsPath));
                    CollectionAssert.Contains(owner.SettingDialog.AvailableBMSDirectories, staged);
                    Assert.AreEqual(0, owner.SettingDialog.CustomFolderAdditionalOutputBaseDirList.Count);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(additional).Count());
                }
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [additional]));
                Exception? confirmationFailure = null;
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(window.HandleAddCustomFolderAdditionalOutputBase(), "output-confirmation-direct-failure"); }
                catch (Exception failure) { confirmationFailure = failure; }
                Assert.IsInstanceOfType<InvalidOperationException>(confirmationFailure);
                if (failedWithException) { Assert.AreSame(original, confirmationFailure.InnerException); }
                else { StringAssert.Contains(confirmationFailure.Message, nameof(UiDialogStatus.OwnerUnavailable)); }
                AssertUnaccepted();
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationPlaylist");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                ListBox list = FindDescendants<ListBox>(page).Single(item => item.Name == "additionalOutputList");
                Button add = FindDescendants<Button>((DependencyObject)list.Parent).Single(button => Equals(button.Content, Resources.Add));
                void ClickAndAwait()
                {
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [additional]));
                    Task click = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, add)));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(click, "output-confirmation-button-event");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "output-confirmation-button-terminal");
                }
                ClickAndAwait();
                AssertUnaccepted();
                Assert.AreEqual(1, dialogs.MessageRequests.Count, "実Buttonは表示失敗を取消に丸めず、一回だけ結果を通知します。");
                Assert.AreEqual(0, unhandled.Count);
                Assert.IsTrue(window.IsVisible);
                dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
                ClickAndAwait();
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                CollectionAssert.AreEqual(new[] { additional }, CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(fresh.LR2CustomFolderAdditionalOutputBaseDirs).ToArray());
                CollectionAssert.AreEqual(new[] { install }, owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(install, fresh.StandaloneBmsRootPaths);
                Assert.AreEqual(3, dialogs.ConfirmationRequests.Count);
                Assert.AreEqual(1, dialogs.MessageRequests.Count);
                Assert.AreEqual(0, unhandled.Count);
                Assert.IsTrue(dialogs.FolderRequests.All(request => ReferenceEquals(window, request.Owner)));
                Assert.IsTrue(window.IsVisible);
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                var cleanupFailures = new List<Exception>();
                void Cleanup(Action action) { try { action(); } catch (Exception failure) { cleanupFailures.Add(failure); } }
                if (window != null)
                {
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(owner?.SettingDialog.SettingsOperationCompletionTask ?? Task.CompletedTask, "output-confirmation-cleanup"));
                    window.Dispatcher.UnhandledException -= onUnhandled;
                    Cleanup(window.CloseForOwnerShutdown);
                }
                if (owner != null)
                {
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-confirmation-owner-cleanup"));
                    Cleanup(owner.SettingDialog.Dispose);
                }
                Cleanup(() => Directory.Delete(directory, recursive: true));
                if (cleanupFailures.Count > 0)
                {
                    var cleanupFailure = new AggregateException("output confirmation cleanup", cleanupFailures);
                    if (primaryFailure == null) { throw cleanupFailure; }
                    Console.WriteLine(cleanupFailure);
                }
            }
        });
    }

    /// <summary>実backupボタンからatomic writerの公開を失敗させ、旧bytes・一回の結果通知・grid再開と次の明示保存を保ちます。成功/失敗/取消でも別のBMT候補を保存・破棄しません。picker取消では通知・変更を始めません。</summary>
    /// <remarks>実BAMLのasyncイベントとWorkspace・ファイル公開を接続し、失敗ダイアログがさらに失敗してもdispatcherへ漏れないことを観測します。</remarks>
    [TestMethod]
    public void BackupPage_WriteFailureAndBrokenNoticeRetainBytesAndAcceptNextExplicitClick()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "settings-backup-boundary-" + Guid.NewGuid().ToString("N"));
            Logger logger = Ribbit.Logging.NLogWrapper.GetLogger(typeof(SettingsWindowPresentationTests));
            FieldInfo? fileLoggerProvider = typeof(Ribbit.Logging.NLogWrapper).GetField("_FileLogger", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(fileLoggerProvider);
            var previousFileLogger = (Func<Logger>?)fileLoggerProvider.GetValue(null);
            LoggingConfiguration? previousLogging = LogManager.Configuration;
            using var logs = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
            SettingsWindow? window = null;
            MainWindowViewModel? owner = null;
            var unhandled = new List<Exception>();
            var secondaryFailures = new List<(Exception Exception, string Route)>();
            var noticeFailure = new IOException("backup notice failed");
            DispatcherUnhandledExceptionEventHandler onUnhandled = (_, args) =>
            {
                unhandled.Add(args.Exception);
                args.Handled = true;
            };
            try
            {
                Directory.CreateDirectory(directory);
                var configuration = new LoggingConfiguration();
                configuration.AddRule(LogLevel.Error, LogLevel.Fatal, logs, logger.Name);
                LogManager.Configuration = configuration;
                fileLoggerProvider.SetValue(null, (Func<Logger>)(() => logger));
                string dbPath = Path.Combine(directory, "song.db");
                StartupLibraryConstructionTestSupport.CreateSongDatabase(dbPath);
                owner = MainWindowViewModelTestFactory.Create();
                PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateBackupWorkspace(
                    dbPath,
                    [new BMSTable { playlist_id = 1, name = "Saved backup", symbol = "B" }],
                    out BMSPlaylist playlist, out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
                    reloadFailureLog: (exception, route) => secondaryFailures.Add((exception, route)));
                playlist.CommitBMSTableHeaderToDB(playlist.BMSTables.Single());
                string destination = Path.Combine(directory, "backup.sql");
                byte[] oldBytes = System.Text.Encoding.UTF8.GetBytes("old backup bytes");
                File.WriteAllBytes(destination, oldBytes);
                var dialogs = new RecordingSettingsRouteDialogService
                {
                    SaveFileResult = new UiSaveFilePickerResult(UiDialogStatus.Accepted, destination),
                    MessageResult = UiDialogResult.Failed(noticeFailure)
                };
                window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog, PlaylistWorkspace = workspace };
                dialogs.ExpectedOwner = window;
                workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
                {
                    foreach (PlaylistOperationNotificationOwner.OperationNotification notice in request.Receipt.Notifications)
                    {
                        UiDialogResult result = dialogs.ShowMessageAsync(new UiMessageRequest(
                            notice.Message, notice.Caption, MessageBoxButton.OK, MessageBoxImage.Hand,
                            MessageBoxResult.OK, owner: window)).GetAwaiter().GetResult();
                        UiDialogRoute.ThrowIfNotShown(result, request.RouteName);
                    }
                };
                window.Dispatcher.UnhandledException += onUnhandled;
                windowTest.ShowAndWaitForContentRendered(window);
                bool candidate = !owner.SettingDialog.EnableBeatorajaBmtOutput;
                owner.SettingDialog.EnableBeatorajaBmtOutput = candidate;
                var grid = (Grid)window.FindName("settingDialogOperationGrid");
                using (var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    ClickBackupPageButton(window, Resources.Backup_playlist);
                    AwaitViewOperationTerminal(owner.SettingDialog);
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(destination));
                Assert.AreEqual(candidate, owner.SettingDialog.EnableBeatorajaBmtOutput);
                Assert.AreEqual(1, dialogs.MessageRequests.Count);
                Assert.AreEqual(1, notifications.Count);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                    notifications.Single().Receipt.Notifications.Single().Severity);
                Assert.IsTrue(grid.IsEnabled);
                LogManager.Flush();
                Assert.IsTrue(logs.Logs.Any(log => log.Contains("playlist backup path=" + destination, StringComparison.Ordinal)
                    && log.Contains(nameof(IOException), StringComparison.Ordinal)));
                Assert.AreEqual(1, secondaryFailures.Count);
                Assert.AreEqual("playlist_operation_notification_presentation_failed route=playlist backup notification", secondaryFailures.Single().Route);
                Assert.AreSame(noticeFailure, secondaryFailures.Single().Exception.InnerException);

                dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
                ClickBackupPageButton(window, Resources.Backup_playlist);
                AwaitViewOperationTerminal(owner.SettingDialog);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                StringAssert.Contains(File.ReadAllText(destination), "Saved backup");
                Assert.AreEqual(candidate, owner.SettingDialog.EnableBeatorajaBmtOutput);
                Assert.AreEqual(2, dialogs.SaveFileRequests.Count);
                Assert.AreEqual(2, dialogs.MessageRequests.Count);
                byte[] savedBytes = File.ReadAllBytes(destination);
                dialogs.SaveFileResult = new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser);
                ClickBackupPageButton(window, Resources.Backup_playlist);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                CollectionAssert.AreEqual(savedBytes, File.ReadAllBytes(destination));
                Assert.AreEqual(candidate, owner.SettingDialog.EnableBeatorajaBmtOutput);
                Assert.AreEqual(3, dialogs.SaveFileRequests.Count);
                Assert.AreEqual(2, dialogs.MessageRequests.Count);
                Assert.AreEqual(1, secondaryFailures.Count, "次の成功と取消では通知失敗の診断を重ねません。");
                Assert.AreEqual(0, unhandled.Count);
            }
            finally
            {
                try
                {
                    if (owner != null) { AwaitViewOperationTerminal(owner.SettingDialog); }
                }
                finally
                {
                    if (window != null) { window.Dispatcher.UnhandledException -= onUnhandled; }
                    try
                    {
                        if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                    }
                    finally
                    {
                        try { owner?.SettingDialog.Dispose(); }
                        finally
                        {
                            try { LogManager.Flush(); }
                            finally
                            {
                                try { fileLoggerProvider.SetValue(null, previousFileLogger); }
                                finally
                                {
                                    try { LogManager.Configuration = previousLogging; }
                                    finally
                                    {
                                        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });
    }

    /// <summary>実restoreボタンのL/P Busyと受付閉鎖ではdumpを読まず、必須UI完了失敗では確定DBを保持します。いずれも終了端末を呼ばず、次要求を自動再生しません。</summary>
    /// <remarks>Busyの入力を存在しないdumpにして副作用前の拒否を区別し、実DBとcompiledイベント・設定gridの実終端を観測します。</remarks>
    [DataTestMethod]
    [DataRow("picker-cancel")]
    [DataRow("library-busy")]
    [DataRow("playlist-busy")]
    [DataRow("admission-closed")]
    [DataRow("required-ui-failure")]
    [DataRow("required-publication-failure")]
    public void BackupPage_RestoreRefusalOrRequiredFailureNeverRequestsExit(string condition)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "settings-restore-refusal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SettingsWindow? window = null;
            MainWindowViewModel? owner = null;
            IDisposable? heldLease = null;
            bool committedFailure = condition is "required-ui-failure" or "required-publication-failure";
            try
            {
                string dbPath = Path.Combine(directory, "song.db");
                StartupLibraryConstructionTestSupport.CreateSongDatabase(dbPath);
                TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(dbPath,
                    MainWindowViewModelTestFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false));
                PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateBackupWorkspace(
                    dbPath, [new BMSTable { playlist_id = 1, name = "Old live", symbol = "O" }],
                    out BMSPlaylist playlist, out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
                    restoreUiApplyScheduler: condition == "required-ui-failure"
                        ? _ => Task.FromException(new InvalidOperationException("required restore UI failed")) : null,
                    libraryOverride: library);
                playlist.CommitBMSTableHeaderToDB(playlist.BMSTables.Single());
                if (condition == "required-publication-failure")
                {
                    workspace.PlaylistEntriesHydrationRequested += (_, _) =>
                        throw new IOException("required restore publication failed");
                }
                BMSTable oldTable = playlist.BMSTables.Single();
                int rejected = 0;
                workspace.MutationRejected += (_, _) => rejected++;
                if (condition == "library-busy")
                {
                    Assert.IsTrue(library.OperationAdmission.TryEnter(out heldLease));
                }
                else if (condition == "playlist-busy")
                {
                    Assert.IsTrue(playlist.TryEnterPlaylistMutation(out heldLease));
                }
                else if (condition == "admission-closed")
                {
                    library.OperationAdmission.CloseAdmission();
                }
                string dump = Path.Combine(directory, "restore.sql");
                if (committedFailure)
                {
                    File.WriteAllText(dump, PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(2, "Committed", "C"));
                }
                var dialogs = new RecordingSettingsRouteDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                    FileResult = condition == "picker-cancel" ? new UiFilePickerResult(UiDialogStatus.CancelledByUser) : new UiFilePickerResult(UiDialogStatus.Accepted, [dump])
                };
                int exitRequests = 0;
                owner = MainWindowViewModelTestFactory.Create();
                window = new SettingsWindow(dialogs, _ => exitRequests++)
                { DataContext = owner.SettingDialog, PlaylistWorkspace = workspace };
                dialogs.ExpectedOwner = window;
                windowTest.ShowAndWaitForContentRendered(window);
                bool bmtDraft = !owner.SettingDialog.EnableBeatorajaBmtOutput;
                owner.SettingDialog.EnableBeatorajaBmtOutput = bmtDraft;
                var grid = (Grid)window.FindName("settingDialogOperationGrid");
                ClickBackupPageButton(window, Resources.Backup_restore_playlist);
                AwaitViewOperationTerminal(owner.SettingDialog);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, exitRequests);
                Assert.AreEqual(bmtDraft, owner.SettingDialog.EnableBeatorajaBmtOutput);
                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
                StringAssert.Contains(dialogs.ConfirmationRequests.Single().MessageBoxText, Resources.Settings_candidates_discarded_on_exit);
                Assert.AreEqual(0, dialogs.MessageRequests.Count);
                Assert.IsTrue(window.IsVisible);
                Assert.IsTrue(grid.IsEnabled);
                Assert.AreEqual(SettingsWindowCloseReason.None, window.CloseReason);
                if (condition == "required-publication-failure")
                {
                    Assert.AreEqual(2, playlist.BMSTables.Single().playlist_id, "必要公開の後段失敗で採用済み集合を巻き戻さない。");
                }
                else { Assert.AreSame(oldTable, playlist.BMSTables.Single()); }
                Assert.AreEqual(committedFailure || condition == "picker-cancel" ? 0 : 1, rejected);
                Assert.AreEqual(committedFailure ? 1 : 0, notifications.Count);
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(committedFailure ? 2 : 1, db.Table<BMSTable>().Single().playlist_id);
                }
                heldLease?.Dispose();
                heldLease = null;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(1, dialogs.FileRequests.Count, "解放後に要求を自動再生しない。");
                Assert.AreEqual(0, exitRequests);
            }
            finally
            {
                heldLease?.Dispose();
                try
                {
                    if (owner != null) { AwaitViewOperationTerminal(owner.SettingDialog); }
                }
                finally
                {
                    try
                    {
                        if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                    }
                    finally
                    {
                        try { owner?.SettingDialog.Dispose(); }
                        finally { Directory.Delete(directory, recursive: true); }
                    }
                }
            }
        });
    }

    /// <summary>実restoreの必要UI待ち中はClose・Escを保留し、DB・必要UI公開が成功すれば、完了receiptと終了案内が失敗してもshell端末を一回呼び実Closeします。最終通知へL/Pを持ち越しません。</summary>
    [TestMethod]
    public void BackupPage_RestoreSuccessClosesOnceDespiteCompletionAndExitNoticeFailures()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "settings-restore-success-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SettingsWindow? window = null;
            MainWindowViewModel? owner = null;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var execute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduledApplies = new List<Task>();
            bool clicked = false;
            async Task ApplyAsync(Action action)
            {
                await execute.Task;
                action();
                executed.TrySetResult();
                await complete.Task;
            }
            try
            {
                string dbPath = Path.Combine(directory, "song.db");
                StartupLibraryConstructionTestSupport.CreateSongDatabase(dbPath);
                TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(dbPath,
                    MainWindowViewModelTestFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false));
                PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateBackupWorkspace(
                    dbPath, [new BMSTable { playlist_id = 1, name = "Old live", symbol = "O" }],
                    out BMSPlaylist playlist, out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
                    restoreUiApplyScheduler: action =>
                    {
                        Task operation = ApplyAsync(action);
                        lock (scheduledApplies) { scheduledApplies.Add(operation); }
                        accepted.TrySetResult();
                        return operation;
                    },
                    libraryOverride: library);
                string dump = Path.Combine(directory, "restore.sql");
                File.WriteAllText(dump, PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(2, "Restored", "R"));
                bool notificationAfterAdmissionRelease = false;
                workspace.PlaylistOperationNotificationPresentationRequested += (_, _) =>
                {
                    bool libraryAvailable = library.OperationAdmission.TryEnter(out IDisposable l);
                    bool playlistAvailable = playlist.TryEnterPlaylistMutation(out IDisposable p);
                    p?.Dispose();
                    l?.Dispose();
                    notificationAfterAdmissionRelease = libraryAvailable && playlistAvailable;
                    throw new IOException("completion notice failed");
                };
                var dialogs = new RecordingSettingsRouteDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                    FileResult = new UiFilePickerResult(UiDialogStatus.Accepted, [dump]),
                    MessageResult = UiDialogResult.Failed(new IOException("exit notice failed"))
                };
                int exits = 0;
                owner = MainWindowViewModelTestFactory.Create();
                window = new SettingsWindow(dialogs, source =>
                {
                    exits++;
                    source.CloseForOwnerShutdown();
                })
                { DataContext = owner.SettingDialog, PlaylistWorkspace = workspace };
                dialogs.ExpectedOwner = window;
                window.Closed += (_, _) => closed.TrySetResult();
                windowTest.ShowAndWaitForContentRendered(window);
                clicked = true;
                ClickBackupPageButton(window, Resources.Backup_restore_playlist);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(accepted.Task, closed.Task, "compiled restore UI accepted"),
                    "compiled restore UI accepted");
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(0, exits);
                Assert.AreEqual(0, notifications.Count);
                execute.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(executed.Task, closed.Task, "compiled restore UI execute"),
                    "compiled restore UI execute");
                Assert.AreEqual(2, playlist.BMSTables.Single().playlist_id);
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(0, exits, "必要UIの実完了前に終了を許可しない。");
                window.Close();
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(window.IsVisible, "復元の必要UI終端は通常CloseとEscで捨てません。");
                Assert.IsFalse(closed.Task.IsCompleted);
                Assert.IsFalse(owner.SettingDialog.SettingsOperationCompletionTask.IsCompleted);

                Assert.AreEqual(0, dialogs.MessageRequests.Count);
                complete.TrySetResult();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "compiled restore success Close");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(1, exits);
                Assert.IsTrue(notificationAfterAdmissionRelease);
                Assert.AreEqual(1, notifications.Count);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                    notifications.Single().Receipt.Notifications.Single().Severity);
                Assert.AreEqual(1, dialogs.MessageRequests.Count);
                Assert.AreEqual(Resources.Msg_ApplicationWillExit, dialogs.MessageRequests.Single().MessageBoxText);
                Assert.IsFalse(window.IsVisible);
                Assert.IsNull(window.DataContext);
                Assert.AreEqual(2, playlist.BMSTables.Single().playlist_id);
                using LR2SongDBExtended db = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
                Assert.AreEqual(2, db.Table<BMSTable>().Single().playlist_id);
            }
            finally
            {
                execute.TrySetResult();
                complete.TrySetResult();
                try
                {
                    Task[] applies;
                    lock (scheduledApplies) { applies = scheduledApplies.ToArray(); }
                    foreach (Task apply in applies)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "compiled restore finally UI terminal");
                    }
                    if (clicked && !closed.Task.IsCompleted)
                    {
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "compiled restore finally Close");
                    }
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
                finally
                {
                    try
                    {
                        if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                    }
                    finally
                    {
                        try { owner?.SettingDialog.Dispose(); }
                        finally { Directory.Delete(directory, recursive: true); }
                    }
                }
            }
        });
    }

    private static void ClickBackupPageButton(SettingsWindow window, string text)
    {
        ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationBackup");
        window.Measure(new Size(820, 760));
        window.Arrange(new Rect(0, 0, 820, 760));
        window.UpdateLayout();
        var page = (BackupSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
        FindDescendants<Button>(page).Single(button => Equals(button.Content, text))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>実 DB の restore が保存後の UI 反映で失敗しても設定 Window を閉じず、保存済み DB と失敗を保持することを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_RestoreFailureDoesNotAuthorizeCloseOrShutdown()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(
                Path.GetTempPath(),
                "bemusicseeker-settings-restore-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            string songDbPath = Path.Combine(scope, "song.db");
            string backupPath = Path.Combine(scope, "restore.sql");
            File.WriteAllText(backupPath,
                PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(2, "Committed restore", "R"));
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }

            SettingsWindow? window = null;
            MainWindowViewModel? owner = null;
            try
            {
                owner = MainWindowViewModelTestFactory.Create(MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = Path.GetTempPath();
                values.StandaloneBmsRootPaths = Path.GetTempPath();
                values.BMSInstallDir = Path.GetTempPath();
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
            }));
                PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateBackupWorkspace(
                    songDbPath,
                    [new BMSTable { playlist_id = 1, name = "Existing", symbol = "E" }],
                    out _,
                    out List<PlaylistOperationNotificationPresentationRequestedEventArgs> restoreNotifications,
                    restoreUiApplyScheduler: _ => throw new InvalidOperationException("restore UI completion failure"));
                var dialogs = new RecordingSettingsRouteDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes),
                    FileResult = new UiFilePickerResult(UiDialogStatus.Accepted, [backupPath])
                };
                window = new SettingsWindow(dialogs)
                {
                    DataContext = owner.SettingDialog,
                    PlaylistWorkspace = workspace
                };
                dialogs.ExpectedOwner = window;
                windowTest.ShowAndWaitForContentRendered(window);

                Task restoreTask = window.HandlePlaylistRestoreAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(restoreTask, "settings restore failure");
                Assert.IsTrue(restoreTask.IsCompletedSuccessfully);
                Assert.AreEqual(1, restoreNotifications.Count);
                StringAssert.Contains(restoreNotifications.Single().Receipt.Notifications.Single().Message,
                    "restore UI completion failure");
                Assert.AreEqual(0, dialogs.MessageRequests.Count, "下位の失敗receiptを再通知せず、終了案内も出さない。");
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(SettingsWindowCloseReason.None, window.CloseReason);
                using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(2, verify.Table<BMSTable>().Single().playlist_id);
                }
                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
                Assert.AreEqual(1, dialogs.FileRequests.Count);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                owner?.SettingDialog.Dispose();
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    /// <summary>実root追加Buttonから受理順と先頭選択を保ち、取消・閉じるでは無通知、picker FailedとTask faultでは一回ずつ通知してdraftと選択を保持します。</summary>
    [TestMethod]
    public void SettingsWindow_BmsSearchRootPickerPreservesAcceptedOrderSelectionAndFailureContract()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(Path.GetTempPath(), "bemusicseeker-settings-search-roots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            string firstPath = Path.Combine(scope, "first");
            string secondPath = Path.Combine(scope, "second");
            Directory.CreateDirectory(firstPath);
            Directory.CreateDirectory(secondPath);

            SettingsWindow? window = null;
            try
            {
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = string.Empty;
                values.StandaloneBmsRootPaths = string.Empty;
            });
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
                using BeMusicSeeker.ViewModels.SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
                var dialogs = new RecordingSettingsRouteDialogService();
                window = new SettingsWindow(dialogs)
                {
                    DataContext = owner.SettingDialog,
                    PlaylistWorkspace = owner.PlaylistWorkspace
                };
                windowTest.ShowAndWaitForContentRendered(window);
                dialogs.ExpectedOwner = window;

                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                void ClickAdd()
                {
                    ((Button)page.FindName("addBmsSearchRootButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
                string[] acceptedPaths = [firstPath, secondPath];
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(
                    UiDialogStatus.Accepted,
                    acceptedPaths));
                ClickAdd();

                Assert.AreEqual(1, dialogs.FolderRequests.Count);
                Assert.IsTrue(dialogs.FolderRequests[0].Multiselect);
                Assert.AreSame(window, dialogs.FolderRequests[0].Owner);
                CollectionAssert.AreEqual(acceptedPaths, owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(firstPath, owner.SettingDialog.SelectedBmsSearchRootPath);

                string[] rootsAfterAccepted = [.. owner.SettingDialog.AvailableBMSDirectories];
                string selectedAfterAccepted = owner.SettingDialog.SelectedBmsSearchRootPath;
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.CancelledByUser));
                ClickAdd();
                CollectionAssert.AreEqual(rootsAfterAccepted, owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(selectedAfterAccepted, owner.SettingDialog.SelectedBmsSearchRootPath);

                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.ClosedByUser));
                ClickAdd();
                CollectionAssert.AreEqual(rootsAfterAccepted, owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(selectedAfterAccepted, owner.SettingDialog.SelectedBmsSearchRootPath);

                var pickerFailure = new IOException("search root picker sentinel");
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Failed, error: pickerFailure));
                Assert.AreEqual(0, dialogs.MessageRequests.Count, "利用者の取消・閉鎖は通知しない。");
                ClickAdd();
                Assert.AreEqual(1, dialogs.MessageRequests.Count);
                dialogs.FolderTasks.Enqueue(Task.FromException<UiFolderPickerResult>(new IOException("root picker Task fault")));
                ClickAdd();
                Assert.AreEqual(2, dialogs.MessageRequests.Count);
                CollectionAssert.AreEqual(rootsAfterAccepted, owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(selectedAfterAccepted, owner.SettingDialog.SelectedBmsSearchRootPath);
                Assert.IsTrue(dialogs.FolderRequests.All(request => request.Multiselect));
                Assert.IsTrue(dialogs.FolderRequests.All(request => ReferenceEquals(window, request.Owner)));
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    /// <summary>root picker の gate 中でも dispatcher の別処理が進み、結果到達後にだけ一覧と選択へ反映することを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_BmsSearchRootPickerAwaitsWithoutBlockingDispatcher()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(Path.GetTempPath(), "bemusicseeker-settings-search-roots-pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            string firstPath = Path.Combine(scope, "first");
            string secondPath = Path.Combine(scope, "second");
            Directory.CreateDirectory(firstPath);
            Directory.CreateDirectory(secondPath);

            SettingsWindow? window = null;
            SettingsDialogViewModel? ownedSettings = null;
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            Task? route = null;
            TaskCompletionSource<UiFolderPickerResult>? completion = null;
            try
            {
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = string.Empty;
                values.StandaloneBmsRootPaths = string.Empty;
            });
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
                ownedSettings = owner.SettingDialog;
                var dialogs = new RecordingSettingsRouteDialogService();
                window = new SettingsWindow(dialogs)
                {
                    DataContext = owner.SettingDialog,
                    PlaylistWorkspace = owner.PlaylistWorkspace
                };
                windowTest.ShowAndWaitForContentRendered(window);
                dialogs.ExpectedOwner = window;

                completion = new TaskCompletionSource<UiFolderPickerResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                dialogs.FolderTasks.Enqueue(completion.Task);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));

                route = window.HandleAddBmsSearchRootPathsAsync();

                Assert.IsFalse(route.IsCompleted);
                bool dispatcherWorkCompleted = false;
                DispatcherOperation backgroundWork = window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => dispatcherWorkCompleted = true));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(backgroundWork.Task, "settings-pending-background-work");
                Assert.IsTrue(dispatcherWorkCompleted);
                Assert.IsFalse(route.IsCompleted);

                completion.TrySetResult(new UiFolderPickerResult(
                    UiDialogStatus.Accepted,
                    [firstPath, secondPath]));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(route, "settings-route-terminal");

                CollectionAssert.AreEqual(
                    new[] { firstPath, secondPath },
                    owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(firstPath, owner.SettingDialog.SelectedBmsSearchRootPath);
                Assert.IsTrue(dialogs.FolderRequests.Single().Multiselect);
                Assert.AreSame(window, dialogs.FolderRequests.Single().Owner);
            }
            finally
            {
                try
                {
                    completion?.TrySetResult(new UiFolderPickerResult(UiDialogStatus.CancelledByUser));
                    if (route != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(route, "settings-picker-finally-route"); }
                    if (window?.IsVisible == true)
                    {
                        window.CloseForOwnerShutdown();
                    }
                }
                finally
                {
                    ownedSettings?.Dispose();
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                    Directory.Delete(scope, recursive: true);
                }
            }
        });
    }

    /// <summary>表示中の言語変更で assembly の version／build 表示を新言語へ更新し、古い翻訳や別の識別値を表示しないことを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_AboutIdentityUpdatesVersionAndBuildWhenLanguageChanges()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousCulture = BeMusicSeeker.Properties.Resources.Culture?.Name ?? "ja-JP";
            SettingsWindow? window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
                using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
                window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    Width = 820,
                    Height = 600
                };

                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                navigation.SelectedItem = window.FindName("navigationAbout");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (AboutSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                var version = (TextBlock)page.FindName("textBlockVerNum");
                var build = (TextBlock)page.FindName("textBlockBuildNum");
                string englishVersion = version.Text;
                string englishBuild = build.Text;

                ResourceService.Current.ChangeCulture("ja-JP");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreNotEqual(englishVersion, version.Text);
                Assert.AreNotEqual(englishBuild, build.Text);
                var entryAssembly = Assembly.GetEntryAssembly();
                string assemblyVersion = entryAssembly?.GetName().Version?.ToString() ?? string.Empty;
                string? informationalVersion = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                string displayedVersion = string.IsNullOrWhiteSpace(informationalVersion) ? assemblyVersion : informationalVersion;
                Assert.AreEqual(string.Format(Resources.About_version_format, displayedVersion), version.Text);
                Assert.AreEqual(string.Format(Resources.About_build_format, assemblyVersion), build.Text);
                Assert.IsTrue(FindDescendants<Button>(page).Any(button =>
                    Equals(button.Content, Resources.About_release_notes_button)));
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }

                ResourceService.Current.ChangeCulture(previousCulture);
            }
        });
    }

    /// <summary>通常／初期設定の同じRadioButtonカードが全言語・最小幅で等幅二列・同高・単一名称となり、全表面の入力先と選択不可条件を保つことを確認します。</summary>
    /// <remarks>実Windowの測定・hit-test・動的テーマ反映を観測します。明示した選択とClickからの確認一回・取消復元はOperationModeRadio系列へ分担し、標準キー接続は静的確認、実OS入力は実機推奨へ分けます。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SettingsWindow_EnvironmentCardsFitAndKeepWholeSurfaceInputAcrossLanguages(bool initialEnvironment)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            CultureInfo previousCulture = Resources.Culture;
            ResourceDictionary[] previousDictionaries = Application.Current.Resources.MergedDictionaries.ToArray();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var state = new DangerStatePort(() => Task.CompletedTask) { HasActiveLibraryProfile = !initialEnvironment };
            DangerDialogContext context = CreateDangerDialog(owner,
                new DangerSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings(), []),
                MainWindowViewModelTestFactory.GetComposition(owner), new DangerDialogService(),
                new DangerSchemaDialogPort(), new DangerApplicationDataStore(), statePort: state);
            using SettingsDialogViewModel settings = context.Settings;
            var window = new SettingsWindow { DataContext = settings };
            window.Width = window.MinWidth;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.AreEqual(initialEnvironment, settings.IsInitialEnvironment);
                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                var standalone = (RadioButton)page.FindName("radioButtonNotUseLR2");
                var linked = (RadioButton)page.FindName("radioButtonUseLR2");
                int themeIndex = 0;
                foreach (string language in new[] { "en-US", "fr-FR", "ja-JP", "ko-KR", "zh-CN", "zh-TW" })
                {
                    ResourceService.Current.ChangeCulture(language);
                    AppThemeService.ApplyTheme(themeIndex++ % 2 == 0 ? AppThemeService.Light : AppThemeService.Dark);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    window.UpdateLayout();
                    Rect left = GetVisualBounds(page, standalone);
                    Rect right = GetVisualBounds(page, linked);
                    Assert.AreEqual(left.Width, right.Width, 0.5, language);
                    Assert.AreEqual(left.Height, right.Height, 0.5, language);
                    Assert.AreEqual(left.Top, right.Top, 0.5, language);
                    Assert.IsTrue(left.Right < right.Left && right.Right <= page.ActualWidth + 0.5, language);
                    foreach (RadioButton radio in new[] { standalone, linked })
                    {
                        Assert.IsTrue(radio.Focusable && radio.IsTabStop && radio.IsEnabled);
                        Assert.AreEqual(1, FindDescendants<TextBlock>(page).Count(text => text.IsVisible && Equals(text.Text, radio.Content)), language);
                        Assert.AreEqual(radio.Content, new RadioButtonAutomationPeer(radio).GetName());
                        TextBlock description = FindDescendants<TextBlock>(radio).Single(text => text.Text == AutomationProperties.GetHelpText(radio));
                        Assert.IsTrue(description.IsVisible);
                        Assert.AreEqual(TextWrapping.Wrap, description.TextWrapping);
                        Border chrome = FindDescendants<Border>(radio).Single(border => border.Child is Grid);
                        AssertBrushColor(radio, radio.IsChecked == true ? "App.ControlSelectedBrush" : "App.SurfaceBrush", chrome.Background);
                        AssertBrushColor(radio, radio.IsKeyboardFocused ? "App.InputFocusBorderBrush"
                            : radio.IsChecked == true ? "App.AccentBrush" : "App.BorderBrush", chrome.BorderBrush);
                        // 角丸の外側は入力面ではない。描画面の四辺・中央・説明も同じRadioButtonへ到達する。
                        foreach (Point point in new[] { new Point(radio.ActualWidth / 2, 2), new Point(2, radio.ActualHeight / 2),
                            new Point(radio.ActualWidth - 2, radio.ActualHeight / 2), new Point(radio.ActualWidth / 2, radio.ActualHeight - 2),
                            new Point(radio.ActualWidth / 2, radio.ActualHeight / 2), description.TranslatePoint(new Point(description.ActualWidth / 2, description.ActualHeight / 2), radio) })
                        {
                            var hit = radio.InputHitTest(point) as DependencyObject;
                            while (hit is Visual && !ReferenceEquals(hit, radio)) { hit = VisualTreeHelper.GetParent(hit); }
                            Assert.AreSame(radio, hit, language + " " + point);
                        }
                    }
                }
                state.SetLibraryOperationInProgress(true);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(settings.IsEnvironmentSelectionEnabled);
                Assert.IsFalse(standalone.IsEnabled);
                Assert.IsFalse(linked.IsEnabled);
                state.SetLibraryOperationInProgress(false);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(standalone.IsEnabled && linked.IsEnabled);
            }
            finally
            {
                if (window.IsVisible) { window.CloseForOwnerShutdown(); }
                if (previousCulture != null) { ResourceService.Current.ChangeCulture(previousCulture.Name); }
                else { Resources.Culture = null; }
                Application.Current.Resources.MergedDictionaries.Clear();
                foreach (ResourceDictionary dictionary in previousDictionaries) { Application.Current.Resources.MergedDictionaries.Add(dictionary); }
            }
        });
    }

    /// <summary>General の standalone 選択、Playback の internal／LR2 説明と Backup の保存先注意が実ページの対象controlへ届くことを確認します。</summary>
    /// <remarks>実Windowのページ遷移とcontrolへのBinding反映を確認し、保存や削除範囲の判断はBackupTestsへ分担します。</remarks>
    [TestMethod]
    public void SettingsWindow_OperationAndPlayerDescriptionsAppearOnTheirOwningPages()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousCulture = BeMusicSeeker.Properties.Resources.Culture?.Name ?? "ja-JP";
            SettingsWindow? window = null;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
                using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
                window = new SettingsWindow { DataContext = owner.SettingDialog };
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var pageHost = (ContentControl)window.FindName("settingsPageContent");

                var generalPage = (GeneralSettingsPage)pageHost.Content;
                var standalone = (RadioButton)generalPage.FindName("radioButtonNotUseLR2");
                Assert.AreEqual(Resources.Operation_Mode_Standalone, standalone.Content);
                Assert.AreEqual(Resources.Operation_Mode_Standalone, new RadioButtonAutomationPeer(standalone).GetName());

                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Name == "navigationPlayback");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var playbackPage = (PlaybackSettingsPage)pageHost.Content;
                var internalPlayer = (RadioButton)playbackPage.FindName("radioButtonInternalPlayer");
                var lr2Player = (RadioButton)playbackPage.FindName("radioButtonPlayLR2body");
                Assert.AreEqual(Resources.Player_Internal_desc, AutomationProperties.GetHelpText(internalPlayer));
                Assert.AreEqual(Resources.Settings_player_lr2_description, AutomationProperties.GetHelpText(lr2Player));
                foreach (RadioButton player in FindDescendants<RadioButton>(playbackPage))
                {
                    Assert.AreEqual(1, FindDescendants<TextBlock>(playbackPage).Count(text => text.IsVisible && Equals(text.Text, player.Content)));
                    Assert.IsTrue(FindDescendants<TextBlock>(playbackPage).Any(text => text.IsVisible && text.Text == AutomationProperties.GetHelpText(player)));
                }

                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Name == "navigationBackup");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var backupPage = (BackupSettingsPage)pageHost.Content;
                SettingsPathPicker destination = FindDescendants<SettingsPathPicker>(backupPage)
                    .Single(picker => Equals(picker.Label, Resources.Backup_lr2backup_saveto));
                Assert.AreEqual(Resources.Backup_destination_description, destination.Description);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                ResourceService.Current.ChangeCulture(previousCulture);
            }
        });
    }





    /// <summary>実削除ボタンから成功・終了通知、設定 Window の Close、標準 shell の停止準備・保存・終了要求まで順に接続されることを確認します。</summary>
    [TestMethod]
    public void AdvancedDangerZone_ApplicationDataActualButtonCompletesProductionShellShutdownRoute()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2SongDBPath = "song.db";
            });
            var events = new List<string>();
            var dialogs = new DangerDialogService(events);
            var schemaDialog = new DangerSchemaDialogPort();
            var store = new DangerApplicationDataStore(events);
            var lifetime = new DangerApplicationLifetime(events);
            var settingsSession = new DangerSettingsEditSession(values, events);
            var composition = new ApplicationComposition(
                settingsEditSession: settingsSession,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                restartFailureDialogs: dialogs);
            MainWindowViewModel shellViewModel = composition.CreateMainWindowViewModelForTest();
            DangerDialogContext context = CreateDangerDialog(
                shellViewModel,
                settingsSession,
                composition,
                dialogs,
                schemaDialog,
                store,
                applicationLifetime: lifetime);
            SettingsWindow? window = null;
            MainWindow? owner = null;
            MainWindowTestLifetime? ownership = null;
            Exception? bodyFailure = null;
            try
            {
                // 未表示 shell も本番 BAML と標準終了接続を使い、共有 resource と native cleanup を寿命所有者で回収する。
                ownership = new MainWindowTestLifetime(shellViewModel, lifetime.ShutdownRequested.Task);
                owner = ownership.CreateWindow(() => new MainWindow(
                    shellViewModel,
                        createdWindow =>
                        {
                            window = createdWindow;
                            createdWindow.DataContext = context.Settings;
                        }));
                window = owner.CreateSettingsWindowForPresentation();
                Assert.IsNotNull(window);
                Assert.IsFalse(owner.IsVisible);
                Assert.IsFalse(window.IsVisible);
                Assert.IsNull(window.Owner);
                Assert.AreSame(context.Settings, window.DataContext);
                window.Closing += (_, _) => events.Add("settings-closing");
                window.Closed += (_, _) => events.Add("settings-closed");
                ClickAdvancedDangerButton(window, Resources.Settings_uninstall_application_data);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(lifetime.ShutdownRequested.Task, "settings-application-uninstall-terminal");

                Assert.AreEqual(0, schemaDialog.CallCount);
                Assert.AreEqual(1, store.CallCount);
                Assert.IsFalse(window.IsVisible);
                Assert.AreEqual(SettingsWindowCloseReason.OwnerShutdown, window.CloseReason);
                Assert.IsNull(window.DataContext);
                Assert.AreSame(shellViewModel, owner.DataContext);
                Assert.IsTrue(shellViewModel.ShellShutdownWorkflow.IsShutdownPrepared);
                Assert.IsTrue(shellViewModel.ShellShutdownWorkflow.IsCloseAllowed);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Assert.AreEqual(1, lifetime.CoordinatedShutdownCount);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "confirm", "settings-closing", "settings-closed", "shutdown-mark:window_close",
                        "settings-save", "store", "message-success", "message-exit", "shutdown-request"
                    },
                    events);
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    if (window?.IsVisible == true)
                    {
                        window.CloseForOwnerShutdown();
                    }
                    context.Settings.Dispose();
                }
                catch (Exception cleanupFailure) when (bodyFailure != null)
                {
                    bodyFailure.Data["SettingsContextCleanupFailure"] = cleanupFailure.ToString();
                }
                finally
                {
                    ownership?.DisposeAfterBodyFailure(bodyFailure);
                }
            }
        });
    }

    /// <summary>一般ページの言語選択でWindowと分類名を更新し、未訪問ページの初回生成にも反映します。取消では保存済み言語と同じBindingへ戻します。</summary>
    [TestMethod]
    public void SettingsWindow_LanguageChangeUpdatesTitleAndAdvancedCategoryAutomationText()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousCulture = BeMusicSeeker.Properties.Resources.Culture?.Name ?? "ja-JP";
            string previousTheme = Settings.Default.AppearanceTheme;
            SettingsWindow? window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(settings =>
                {
                    settings.Lang = "en-US";
                    settings.LangDisplayName = "English";
                });
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
                using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
                window = new SettingsWindow { DataContext = owner.SettingDialog };
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var advanced = (ListBoxItem)window.FindName("navigationAdvanced");

                Assert.AreEqual(Resources.Settings_window_title, window.Title);
                Assert.AreEqual(Resources.Advanced_settings, advanced.Content);
                Assert.AreEqual(Resources.Advanced_settings, AutomationProperties.GetName(advanced));
                string originalTitle = window.Title;
                object originalAdvancedLabel = advanced.Content;

                var generalPage = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                ((ComboBox)generalPage.FindName("languageComboBox")).SelectedItem = "Default(日本語)";
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(Resources.Settings_window_title, window.Title);
                Assert.AreEqual(Resources.Advanced_settings, advanced.Content);
                Assert.AreEqual(Resources.Advanced_settings, AutomationProperties.GetName(advanced));
                Assert.AreNotEqual(originalTitle, window.Title);
                Assert.AreNotEqual(originalAdvancedLabel, advanced.Content);
                Assert.AreNotEqual(window.Title, advanced.Content,
                    "The settings window identity must not reuse the Advanced category label.");
                navigation.SelectedItem = window.FindName("navigationAbout");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var aboutPage = (AboutSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                Button releaseNotes = FindDescendants<Button>(aboutPage).Single(button =>
                    Equals(button.Content, Resources.About_release_notes_button));
                string japaneseReleaseNotes = (string)releaseNotes.Content;
                ((Button)window.FindName("buttonCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual("ja-JP", values.Lang);
                Assert.AreEqual("Default(日本語)", owner.SettingDialog.Language);
                Assert.AreEqual(Resources.Settings_window_title, window.Title);
                Assert.AreEqual(Resources.About_release_notes_button, releaseNotes.Content);
                Assert.AreEqual(japaneseReleaseNotes, releaseNotes.Content);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                ResourceService.Current.ChangeCulture(previousCulture);
                Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
            }
        });
    }

    /// <summary>null／空／空白 status は表示せず、非空への復帰で本文と Automation を更新します。呼出側の明示非表示と構造化 content を上書きしないことも確認します。</summary>
    [TestMethod]
    public void SettingsStatusBanner_CoercesBlankContentAndRecoversBindingsWithoutOverridingVisibility()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            var source = new StatusBannerBindingSource();
            var banner = new SettingsStatusBanner { Icon = "!", Status = "Warning" };
            banner.SetBinding(ContentControl.ContentProperty, new Binding(nameof(StatusBannerBindingSource.Message))
            {
                Source = source,
                Mode = BindingMode.OneWay
            });
            ((Grid)window.FindName("settingDialogRootGrid")).Children.Add(banner);
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);

                source.Message = string.Empty;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);
                source.Message = " \t ";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);

                source.Message = "Device ready";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Visibility.Visible, banner.Visibility);
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(banner);
                Assert.IsNotNull(peer);
                Assert.AreEqual("Device ready", peer.GetName());
                Assert.AreEqual("Warning", peer.GetItemStatus());
                Assert.AreEqual(AutomationLiveSetting.Polite, peer.GetLiveSetting());
                SettingsStatusIcon decorativeIcon = FindDescendants<SettingsStatusIcon>(banner).Single();
                Assert.IsNull(UIElementAutomationPeer.CreatePeerForElement(decorativeIcon),
                    "The redundant icon must not create a standalone automation element.");
                Assert.IsFalse(EnumerateAutomationDescendants(peer)
                    .OfType<UIElementAutomationPeer>()
                    .Any(child => ReferenceEquals(child.Owner, decorativeIcon)),
                    "The banner automation subtree must not contain the decorative icon element.");
                Assert.IsFalse(EnumerateAutomationDescendants(peer).Any(child => child.GetName() == "!"),
                    "The banner automation subtree must expose the message, not the redundant icon glyph.");

                source.Message = "   ";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);
                Assert.AreEqual(string.Empty, peer.GetName());
                Assert.AreEqual(string.Empty, peer.GetItemStatus());
                TextBlock icon = FindDescendants<TextBlock>(banner).Single(text => text.Text == "!");
                Assert.IsFalse(icon.IsVisible);

                source.Message = "Recovered";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Visibility.Visible, banner.Visibility);
                Assert.AreEqual("Recovered", peer.GetName());

                banner.Visibility = Visibility.Collapsed;
                source.Message = "Still explicitly collapsed";
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);
                banner.Visibility = Visibility.Visible;
                Assert.AreEqual(Visibility.Visible, banner.Visibility);

                BindingOperations.ClearBinding(banner, ContentControl.ContentProperty);
                var structuredContent = new TextBlock { Text = "Structured status" };
                banner.Content = structuredContent;
                Assert.AreEqual(Visibility.Visible, banner.Visibility,
                    "Non-string content must remain available to the caller's presentation.");
                Assert.AreSame(structuredContent, banner.Content);
                Assert.IsTrue(FindDescendants<TextBlock>(banner).Contains(structuredContent));
                banner.Visibility = Visibility.Collapsed;
                banner.Content = new TextBlock { Text = "Updated structured status" };
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    /// <summary>DB と config の両 path status は本文を Automation に渡し、装飾 glyph を独立した読み上げ要素として重複させないことを確認します。</summary>
    [TestMethod]
    public void GeneralPathStatusBanners_OwnOneVisualGlyphAndExposeMessageAutomation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                Width = 820,
                Height = 600
            };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                foreach (Expander details in FindDescendants<Expander>(page).ToArray()) { details.IsExpanded = true; }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                string[] statusProperties =
                [
                    nameof(SettingsDialogViewModel.Lr2SongDbPathStatusText),
                    nameof(SettingsDialogViewModel.Lr2ConfigPathStatusText)
                ];

                foreach (string statusProperty in statusProperties)
                {
                    SettingsStatusBanner banner = FindDescendants<SettingsStatusBanner>(page).Single(candidate =>
                        candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path == statusProperty);
                    string? message = banner.Content as string;
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message), statusProperty);
                    Assert.IsFalse(message!.StartsWith("✓", StringComparison.Ordinal),
                        statusProperty + " must leave the visual success glyph to the banner icon.");
                    Assert.IsFalse(message!.StartsWith("!", StringComparison.Ordinal),
                        statusProperty + " must leave the visual warning glyph to the banner icon.");

                    SettingsStatusIcon icon = FindDescendants<SettingsStatusIcon>(banner).Single();
                    Assert.IsFalse(string.IsNullOrWhiteSpace(icon.Text), statusProperty);
                    Assert.AreEqual(banner.Icon, icon.Text, statusProperty);

                    AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(banner);
                    Assert.IsNotNull(peer, statusProperty);
                    Assert.AreEqual(message, peer.GetName(), statusProperty);
                    Assert.AreEqual(banner.Status, peer.GetItemStatus(), statusProperty);
                    Assert.IsNull(UIElementAutomationPeer.CreatePeerForElement(icon), statusProperty);
                    Assert.IsFalse(EnumerateAutomationDescendants(peer)
                        .OfType<UIElementAutomationPeer>()
                        .Any(child => ReferenceEquals(child.Owner, icon)), statusProperty);
                    Assert.IsFalse(EnumerateAutomationDescendants(peer).Any(child => child.GetName() == banner.Icon), statusProperty);
                }
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    /// <summary>General と Advanced の実 banner が Installed→Repairable→Installed に追従し、古い警告 glyph／Automation 状態を残さないことを確認します。</summary>
    [TestMethod]
    public void Lr2SchemaBanners_ActualPagesRevertSeverityAndAutomationStatusAfterRepairBecomesUnavailable()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            SchemaPresentationContext? context = null;
            Settings? values = null;
            string? scope = null;
            string? scoreDbPath = null;
            SettingsWindow? window = null;
            ExceptionDispatchInfo? bodyFailure = null;
            Exception? cleanupFailure = null;
            try
            {
                // context 生成に失敗しても外側で実 DB 領域を回収する。
                scope = CreateDangerSchemaScope(out values, out scoreDbPath);
                context = CreateSchemaPresentationContext(scope, values, scoreDbPath);
                window = new SettingsWindow { DataContext = context.Settings };
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                // 同じ schema 状態を表示する General と Advanced の両配送先を確認する。
                foreach (string categoryName in new[] { "navigationGeneral", "navigationAdvanced" })
                {
                    navigation.SelectedItem = window.FindName(categoryName);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    var page = (UserControl)((ContentControl)window.FindName("settingsPageContent")).Content;
                    SettingsStatusBanner banner = FindDescendants<SettingsStatusBanner>(page).Single(candidate =>
                        candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                        == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaStatusText));

                    SetSchemaPresentationStatus(context.StatePort, context.ScoreDbPath, Lr2PlayHistorySchemaStatus.Installed);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertSchemaBannerState(banner, "i", "Information");

                    SetSchemaPresentationStatus(context.StatePort, context.ScoreDbPath, Lr2PlayHistorySchemaStatus.Repairable);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertSchemaBannerState(banner, "!", "Warning");

                    SetSchemaPresentationStatus(context.StatePort, context.ScoreDbPath, Lr2PlayHistorySchemaStatus.Installed);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertSchemaBannerState(banner, "i", "Information");
                }
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    try
                    {
                        window.CloseForOwnerShutdown();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }

                try
                {
                    context?.Settings.Dispose();
                    context?.Owner.SettingDialog.Dispose();
                }
                catch (Exception exception) { cleanupFailure ??= exception; }

                if (scope != null)
                {
                    try
                    {
                        Directory.Delete(scope, recursive: true);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
            }

            if (bodyFailure != null)
            {
                if (cleanupFailure != null)
                {
                    bodyFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] = cleanupFailure.ToString();
                }

                bodyFailure.Throw();
            }

            if (cleanupFailure != null)
            {
                windowTest.RegisterCleanupFailureForTesting(cleanupFailure);
            }
        });
    }

    /// <summary>未実行で音声テスト status が null の実 Audio ページに空 banner を表示しないことを確認します。</summary>
    [TestMethod]
    public void AudioSettingsPage_NullDeviceTestStatusDoesNotRenderAnEmptyBanner()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = CreateOwnerForPresentationWithoutNativeCapabilityQuery();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAudio");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (AudioSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                SettingsStatusBanner status = FindDescendants<SettingsStatusBanner>(page).Single(candidate =>
                    candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                    == nameof(SettingsDialogViewModel.AudioDeviceTestStatusMessage));

                Assert.IsNull(owner.SettingDialog.AudioDeviceTestStatusMessage);
                Assert.AreEqual(Visibility.Collapsed, status.Visibility);
                Assert.IsFalse(status.IsVisible);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }



    /// <summary>実MainWindow Closeは受理済み音声テストまたは一覧取得の実終端まで保存とruntime停止を待ち、閉じた設定画面へ結果を戻しません。</summary>
    /// <remarks>実shellのClose接続を観測し、native列挙自体のlease保証は既存音声runtimeテストに委ねます。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MainWindow_OwnerShutdownWaitsForAcceptedAudioWorkBeforeRuntimeShutdown(bool enumerateDevices)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            BassAudioRuntime.Initialize();
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = string.Empty;
                values.StandaloneBmsRootPaths = string.Empty;
                values.BMSInstallDir = string.Empty;
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
            });
            var events = new List<string>();
            var lifetime = new DangerApplicationLifetime(events, firstStartup: true);
            var settingsSession = new DangerSettingsEditSession(settings, events);
            var composition = new ApplicationComposition(
                settingsEditSession: settingsSession,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
            // 起動検証を設定入力待ちまで完了させる。終了検査中に別の起動通知モーダルを開かない。
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                viewModel.ShellActivationWorkflow.ActivateRenderedShell(() => { }, _ => { }, () => false),
                "owner-shutdown-settings-required");
            viewModel.SettingDialog.Dispose();
            var runtime = new GatedAudioDeviceTestRuntime();
            using var releaseList = new ManualResetEventSlim();
            var listStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var catalog = new TestAudioDeviceCatalog
            {
                RefreshAction = () => { listStarted.TrySetResult(); releaseList.Wait(); }
            };
            var testReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                runtime);
            workflow.OperationReleased += () => testReleased.TrySetResult();
            var dialog = new SettingsDialogViewModel(
                viewModel,
                viewModel.PlaylistWorkspace,
                viewModel.PlaylistWorkspace,
                viewModel.PlayHistory,
                viewModel.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                viewModel.Lr2SongDbSyncWorkflow,
                settingsSession,
                schemaDialogs: new UiDialogCoordinator(),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                audioDeviceTestWorkflow: workflow,
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: catalog,
                audioSettingsGateway: new TestAudioSettingsGateway());
            typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SettingDialog))!
                .SetValue(viewModel, dialog);

            Window? previousMainWindow = Application.Current.MainWindow;
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            SettingsWindow? settingsWindow = null;
            MainWindow? mainWindow = null;
            Task? audioTestTask = null;
            Task? interactionTask = null;
            var reopenedAudioPresentation = new TaskCompletionSource<SettingsWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            int audioPresentationCount = 0;
            ExceptionDispatchInfo? bodyFailure = null;
            Exception? cleanupFailure = null;
            try
            {
                mainWindow = ownership.CreateWindow(() => new MainWindow(viewModel, createdSettingsWindow =>
                {
                    settingsWindow = createdSettingsWindow;
                    int presentationNumber = ++audioPresentationCount;
                    windowTest.PrepareForOwnedPresentation(createdSettingsWindow);
                    createdSettingsWindow.ContentRendered += (_, _) =>
                    {
                        if (presentationNumber > 1)
                        {
                            reopenedAudioPresentation.TrySetResult(createdSettingsWindow);
                            return;
                        }
                        // OnContentRenderedがpresentationを有効化してから、本番と同じ表示中の要求を受理します。
                        createdSettingsWindow.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                            (Action)(() => interactionTask = ExerciseOwnerShutdownAsync()));
                    };
                }));
                windowTest.PrepareForOwnedPresentation(mainWindow);
                Application.Current.MainWindow = mainWindow;
                mainWindow.Show();
                mainWindow.UpdateLayout();

                dialog.OpenCommand.Execute();
                if (!enumerateDevices) { dialog.RequestOpen(deferPresentation: true); }
                Assert.IsNotNull(interactionTask);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(interactionTask, "owner-shutdown-audio-interaction");

                async Task ExerciseOwnerShutdownAsync()
                {
                    try
                    {
                        MainWindow shell = mainWindow ?? throw new AssertFailedException("The owner window was not created.");
                        SettingsWindow presentedSettings = settingsWindow ?? throw new AssertFailedException("The settings window was not created.");
                        if (enumerateDevices)
                        {
                            ((ListBox)presentedSettings.FindName("settingsNavigation")).SelectedItem = presentedSettings.FindName("navigationAudio");
                            presentedSettings.UpdateLayout();
                            audioTestTask = dialog.AudioSettingsCompletionTask;
                            await listStarted.Task;
                        }
                        else
                        {
                            audioTestTask = dialog.RunAudioDeviceTestAsync();
                            await Task.WhenAny(runtime.Started.Task, audioTestTask);
                            Assert.IsTrue(runtime.Started.Task.IsCompleted,
                                "The accepted test ended before the native runtime gate was reached.");
                            await runtime.Started.Task;
                            Task accepted = dialog.SettingsOperationCompletionTask;
                            var oldClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            presentedSettings.Closed += (_, _) => oldClosed.TrySetResult();
                            presentedSettings.Close();
                            await oldClosed.Task;
                            Assert.IsNull(presentedSettings.DataContext);
                            Assert.IsFalse(accepted.IsCompleted);
                            presentedSettings = await reopenedAudioPresentation.Task;
                            Assert.AreSame(accepted, dialog.SettingsOperationCompletionTask);
                            Assert.IsFalse(dialog.IsAudioOutputSelectionEnabled);
                            Assert.IsFalse(dialog.IsAudioDeviceTestAvailable);
                            await dialog.RunAudioDeviceTestAsync();
                            Assert.AreSame(accepted, dialog.SettingsOperationCompletionTask);
                        }
                        shell.Close();
                        Assert.AreEqual(SettingsWindowCloseReason.OwnerShutdown, presentedSettings.CloseReason);
                        Assert.IsFalse(presentedSettings.IsVisible);
                        Assert.IsNull(presentedSettings.DataContext);
                        await viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                        await viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                        Assert.IsFalse(audioTestTask.IsCompleted);
                        Assert.AreEqual(!enumerateDevices, dialog.IsAudioDeviceTestInProgress);
                        Assert.IsFalse(BassAudioRuntime.OperationGate.AdmissionClosed);
                        Assert.IsFalse(lifetime.ShutdownRequested.Task.IsCompleted);
                        Assert.AreEqual(0, settingsSession.SaveCount);

                        runtime.Release();
                        releaseList.Set();
                        if (!enumerateDevices) { await testReleased.Task; }
                        await audioTestTask;
                        await lifetime.ShutdownRequested.Task;
                        Assert.IsNull(dialog.AudioDeviceTestStatusMessage);
                        Assert.IsFalse(dialog.IsAudioDeviceTestInProgress);
                        Assert.IsTrue(lifetime.ShutdownRequestCount > 0);
                        Assert.IsTrue(settingsSession.SaveCount > 0);
                    }
                    finally
                    {
                        runtime.Release();
                        runtime.ReleaseSecond();
                        releaseList.Set();
                        if (settingsWindow?.IsVisible == true)
                        {
                            settingsWindow.CloseForOwnerShutdown();
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                runtime.Release();
                runtime.ReleaseSecond();
                releaseList.Set();
                try
                {
                    if (audioTestTask != null)
                    {
                        try
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(audioTestTask, "shutdown-audio-test-cleanup");
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure ??= exception;
                        }
                    }
                    if (settingsWindow?.IsVisible == true)
                    {
                        settingsWindow.CloseForOwnerShutdown();
                    }
                    if (interactionTask != null)
                    {
                        try
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(interactionTask, "shutdown-interaction-cleanup");
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure ??= exception;
                        }
                    }
                    ownership?.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
                finally
                {
                    ownership?.DisposeAfterBodyFailure(bodyFailure?.SourceException ?? cleanupFailure);
                    Application.Current.MainWindow = previousMainWindow;
                }
            }

            if (bodyFailure != null)
            {
                bodyFailure.Throw();
            }
            if (cleanupFailure != null)
            {
                windowTest.RegisterCleanupFailureForTesting(cleanupFailure);
            }
        });
    }

    /// <summary>狭い実 Audio ページでも長い status を複数行で表示し、Automation は省略せず全文と状態を保持することを確認します。</summary>
    [TestMethod]
    public void AudioSettingsPage_LongDeviceTestStatusWrapsAtMinimumWindowWidthAndKeepsFullAutomationName()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            const string longMessage = "The selected audio device could not be initialized because its current output format is unavailable. Choose another device or format, then run the audio test again.";
            MainWindowViewModel owner = CreateOwnerForPresentationWithoutNativeCapabilityQuery();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            window.Width = window.MinWidth;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAudio");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                var page = (AudioSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                SettingsStatusBanner banner = FindDescendants<SettingsStatusBanner>(page).Single(candidate =>
                    candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                    == nameof(SettingsDialogViewModel.AudioDeviceTestStatusMessage));
                banner.DataContext = new { AudioDeviceTestStatusMessage = longMessage };
                TestUiDispatcherHost.ProcessQueuedPresentation();
                TextBlock message = FindDescendants<TextBlock>(banner).Single(text => text.Text == longMessage);
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(banner);

                Assert.AreEqual(window.MinWidth, window.ActualWidth, 0.25d);
                Assert.IsTrue(message.ActualHeight > message.FontSize * 1.5d,
                    $"The long audio status remained one line at the minimum window width ({message.ActualWidth} x {message.ActualHeight}).");
                Assert.IsNotNull(peer);
                Assert.AreEqual(longMessage, peer.GetName());
                Assert.AreEqual("Information", peer.GetItemStatus());
                Assert.AreEqual(AutomationLiveSetting.Polite, peer.GetLiveSetting());
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    /// <summary>各意味のある navigation 項目を選ぶと対応ページを同じ draft で表示し、header が選択ラベルへ追従することを確認して誤配送を検出します。</summary>
    [TestMethod]
    public void SettingsWindow_SelectionControlsPageVisibilityAndHeader()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = CreateOwnerForPresentationWithoutNativeCapabilityQuery();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var window = new SettingsWindow();
            SettingsDialogViewModel sharedDataContext = owner.SettingDialog;
            window.DataContext = sharedDataContext;
            var navigation = (ListBox)window.FindName("settingsNavigation");
            var header = (ContentControl)window.FindName("settingsPageHeader");
            var content = (ContentControl)window.FindName("settingsPageContent");
            (string Navigation, Type Page)[] pageRoutes =
            [
                ("navigationGeneral", typeof(GeneralSettingsPage)),
                ("navigationAppearance", typeof(AppearanceSettingsPage)),
                ("navigationPlayback", typeof(PlaybackSettingsPage)),
                ("navigationAudio", typeof(AudioSettingsPage)),
                ("navigationRecording", typeof(RecordingSettingsPage)),
                ("navigationPlaylist", typeof(PlaylistSettingsPage)),
                ("navigationInstall", typeof(InstallSettingsPage)),
                ("navigationBackup", typeof(BackupSettingsPage)),
                ("navigationAdvanced", typeof(AdvancedSettingsPage)),
                ("navigationRightClick", typeof(RightClickSettingsPage)),
                ("navigationAbout", typeof(AboutSettingsPage))
            ];

            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                // navigation の表示順ではなく、意味のある各項目の配送先を照合する。
                foreach ((string categoryName, Type pageType) in pageRoutes)
                {
                    navigation.SelectedItem = window.FindName(categoryName);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(pageType, content.Content.GetType());
                    Assert.AreSame(sharedDataContext, ((FrameworkElement)content.Content).DataContext);
                    Assert.AreEqual(((ListBoxItem)navigation.SelectedItem).Content, header.Content);
                }
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }



    /// <summary>後から実Appearanceページで追加テーマを選び、未訪問ページの初期生成、即時適用と閉鎖後の保存、Binding維持を確認します。</summary>
    [TestMethod]
    public void SettingsWindow_AppearanceThemeBindingSurvivesDeferredPageConnectionAndClose()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousTheme = Settings.Default.AppearanceTheme;
            SettingsWindow? window = null;
            try
            {
                Settings.Default.AppearanceTheme = AppThemeService.Light;
                AppThemeService.ApplyTheme(AppThemeService.Light);
                MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create(Settings.Default);
                using BeMusicSeeker.ViewModels.SettingsDialogViewModel mainViewModelSettingsLifetime = mainViewModel.SettingDialog;
                SettingsDialogViewModel settings = mainViewModel.SettingDialog;
                window = new SettingsWindow
                {
                    DataContext = settings,
                    Width = 820,
                    Height = 600
                };

                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var pageHost = (ContentControl)window.FindName("settingsPageContent");
                Assert.IsInstanceOfType<GeneralSettingsPage>(pageHost.Content);

                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Name == "navigationAppearance");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var appearancePage = (AppearanceSettingsPage)pageHost.Content;
                Assert.AreSame(settings, appearancePage.DataContext);
                var selector = (ComboBox)appearancePage.FindName("appearanceThemeComboBox");
                BindingExpression themeBinding = selector.GetBindingExpression(Selector.SelectedValueProperty);
                Assert.IsNotNull(themeBinding);
                Assert.AreEqual(BindingStatus.Active, themeBinding.Status);
                Assert.AreEqual(BindingMode.TwoWay, themeBinding.ParentBinding.Mode);
                Assert.AreEqual(AppThemeService.Light, selector.SelectedValue);
                Assert.AreEqual(AppThemeService.AvailableThemes.Count, selector.Items.Count);
                Assert.AreEqual(appearancePage.ActualWidth, selector.ActualWidth, 1d, "テーマ選択は言語選択と同様に本文幅まで広がります。");
                Color lightTextColor = GetApplicationBrushColor("App.TextBrush");

                selector.SelectedItem = selector.Items.Cast<SettingsDialogViewModel.AppearanceThemeOption>()
                    .Single(option => option.Key == AppThemeService.GreenTerminal);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(AppThemeService.GreenTerminal, selector.SelectedValue);
                Assert.AreEqual(AppThemeService.GreenTerminal, settings.AppearanceTheme);
                Assert.AreEqual(AppThemeService.GreenTerminal, Settings.Default.AppearanceTheme);
                Assert.AreEqual(AppThemeService.GreenTerminal, GetAppliedApplicationTheme());
                Assert.IsTrue(BindingOperations.IsDataBound(selector, Selector.SelectedValueProperty));

                settings.UsePlayeruBMplay = true;
                navigation.SelectedItem = window.FindName("navigationPlayback");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var playbackPage = (PlaybackSettingsPage)pageHost.Content;
                var playerSelector = (RadioButton)playbackPage.FindName("radioButtonPlayuBMplay");
                Assert.AreSame(settings, playbackPage.DataContext);
                Assert.AreEqual(true, playerSelector.IsChecked, "初回生成時に現在の編集値を表示します。");
                Assert.AreNotEqual(lightTextColor, GetApplicationBrushColor("App.TextBrush"));
                AssertBrushColor(playbackPage, "App.TextBrush", playerSelector.Foreground);
                navigation.SelectedItem = window.FindName("navigationAppearance");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreSame(appearancePage, pageHost.Content);

                ((Button)window.FindName("buttonCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(AppThemeService.GreenTerminal, settings.AppearanceTheme);
                Assert.AreEqual(AppThemeService.GreenTerminal, Settings.Default.AppearanceTheme);
                Assert.AreEqual(AppThemeService.GreenTerminal, GetAppliedApplicationTheme());
                Assert.AreEqual(AppThemeService.GreenTerminal, selector.SelectedValue);
                Assert.IsTrue(BindingOperations.IsDataBound(selector, Selector.SelectedValueProperty));
                Assert.AreEqual(BindingStatus.Active, selector.GetBindingExpression(Selector.SelectedValueProperty)?.Status);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }

                Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
            }
        });
    }

    /// <summary>実履歴 Window の初期 theme と live 変更を native titlebar と本文へ渡し、閉じた各 Window が後の変更を受信しないことを確認します。</summary>
    [TestMethod]
    public void ThemedWindows_ApplyInitialAndLiveThemeAndDetachNativeTitleBarOnClose()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousTheme = Settings.Default.AppearanceTheme;
            ReleaseNotesWindow? releaseNotes = null;
            ReleaseNotesWindow? darkReleaseNotes = null;
            try
            {
                Settings.Default.AppearanceTheme = AppThemeService.Light;
                AppThemeService.ApplyTheme(AppThemeService.Light);
                var gateway = new RecordingNativeWindowTitleBarGateway();
                releaseNotes = new ReleaseNotesWindow(gateway, new AppNativeWindowTitleBarThemeSource());

                windowTest.ShowAndWaitForContentRendered(releaseNotes);

                Assert.AreEqual(1, gateway.ApplyCount);
                Assert.IsFalse(gateway.LastAppearance.UseDarkMode);
                Assert.AreEqual(GetApplicationBrushColor("App.DialogBackgroundBrush"), ((SolidColorBrush)releaseNotes.Background).Color);
                Assert.AreEqual(GetApplicationBrushColor("App.TextBrush"), ((SolidColorBrush)releaseNotes.Foreground).Color);

                Settings.Default.AppearanceTheme = AppThemeService.Dark;
                AppThemeService.ApplyTheme(AppThemeService.Dark);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(2, gateway.ApplyCount);
                Assert.IsTrue(gateway.LastAppearance.UseDarkMode);
                Assert.AreEqual(GetApplicationBrushColor("App.DialogBackgroundBrush"), ((SolidColorBrush)releaseNotes.Background).Color);

                releaseNotes.Close();
                releaseNotes = null;
                var darkGateway = new RecordingNativeWindowTitleBarGateway();
                darkReleaseNotes = new ReleaseNotesWindow(darkGateway, new AppNativeWindowTitleBarThemeSource());
                windowTest.ShowAndWaitForContentRendered(darkReleaseNotes);
                Assert.AreEqual(1, darkGateway.ApplyCount);
                Assert.IsTrue(darkGateway.LastAppearance.UseDarkMode, "A window opened under Dark must apply the dark title bar initially.");
                darkReleaseNotes.Close();
                darkReleaseNotes = null;

                Settings.Default.AppearanceTheme = AppThemeService.Light;
                AppThemeService.ApplyTheme(AppThemeService.Light);
                Assert.AreEqual(2, gateway.ApplyCount, "A closed themed window must detach its native theme subscription.");
                Assert.AreEqual(1, darkGateway.ApplyCount, "Every closed themed window must detach exactly once.");

            }
            finally
            {
                releaseNotes?.Close();
                darkReleaseNotes?.Close();
                Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
            }
        });
    }

    /// <summary>titlebar の意味 resource が欠ける／brush でない場合は該当 key を含む失敗を返し、別色へフォールバックして不備を隠さないことを確認します。</summary>
    [TestMethod]
    public void NativeTitleBarThemeSource_MissingOrNonSolidSemanticResource_Propagates()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string resourceKey = $"{nameof(NativeTitleBarThemeSource_MissingOrNonSolidSemanticResource_Propagates)}.{Guid.NewGuid():N}";
            ResourceDictionary resources = Application.Current.Resources;
            MethodInfo? resolverMethod = typeof(AppNativeWindowTitleBarThemeSource).GetMethod(
                "GetRequiredSolidColorBrush",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                [typeof(string)],
                modifiers: null);
            Assert.IsNotNull(resolverMethod);
            Func<string, SolidColorBrush> resolveRequiredBrush =
                resolverMethod!.CreateDelegate<Func<string, SolidColorBrush>>();
            Assert.IsFalse(resources.Contains(resourceKey));
            try
            {
                resources[resourceKey] = "not a brush";
                InvalidOperationException nonSolidError = Assert.ThrowsException<InvalidOperationException>(
                    () => _ = resolveRequiredBrush(resourceKey));
                StringAssert.Contains(nonSolidError.Message, "must be a SolidColorBrush");
                StringAssert.Contains(nonSolidError.Message, resourceKey);

                resources.Remove(resourceKey);
                InvalidOperationException missingError = Assert.ThrowsException<InvalidOperationException>(
                    () => _ = resolveRequiredBrush(resourceKey));
                StringAssert.Contains(missingError.Message, "is unavailable");
                StringAssert.Contains(missingError.Message, resourceKey);
            }
            finally
            {
                resources.Remove(resourceKey);
            }
        });
    }

    /// <summary>実 Slider の値が main と playlist summary の実 table へ preview され、既定復元と取消で両 table が対応する設定値へ戻ることを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_AppearanceControlsPersistThroughMainViewSettingsAndClose()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            double previousFontSize = Settings.Default.CustomTableFontSize;
            double previousRowHeight = Settings.Default.CustomTableRowHeight;
            double previousHeaderHeight = Settings.Default.CustomTableHeaderHeight;
            SettingsWindow? window = null;
            MainWindow? mainWindow = null;
            MainWindowViewModel? owner = null;
            CustomTableView? mainTable = null;
            CustomTableView? playlistSummaryTable = null;
            MainWindowTestLifetime? ownership = null;
            ExceptionDispatchInfo? bodyFailure = null;
            Exception? cleanupFailure = null;
            DangerApplicationLifetime? startupLifetime = null;
            try
            {
                var startupEvents = new List<string>();
                startupLifetime = new DangerApplicationLifetime(startupEvents, firstStartup: true);
                var startupSettingsSession = new DangerSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings(), startupEvents);
                var composition = new ApplicationComposition(
                    settingsEditSession: startupSettingsSession,
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: startupLifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                owner = composition.CreateMainWindowViewModelForTest();
                ownership = new MainWindowTestLifetime(owner, startupLifetime.ShutdownRequested.Task);
                mainWindow = ownership.CreateWindow(() => new MainWindow(owner));
                windowTest.PrepareForOwnedPresentation(mainWindow);
                mainWindow.Show();
                mainWindow.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                mainTable = (CustomTableView)mainWindow.FindName("customTableView");
                playlistSummaryTable = (CustomTableView)mainWindow.FindName("customTablePlaylistSummary");
                Assert.IsNotNull(mainTable);
                Assert.IsNotNull(playlistSummaryTable);

                SettingsDialogViewModel settings = owner.SettingDialog;
                double savedFontSize = settings.CustomTableFontSize;
                double savedRowHeight = settings.CustomTableRowHeight;
                double savedHeaderHeight = settings.CustomTableHeaderHeight;
                window = new SettingsWindow { DataContext = settings };
                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAppearance");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (AppearanceSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;

                var sliders = FindDescendants<Slider>(page)
                    .ToDictionary(
                        slider => slider.GetBindingExpression(RangeBase.ValueProperty)?.ParentBinding.Path?.Path
                            ?? throw new AssertFailedException("Appearance slider must bind to a settings property."),
                        StringComparer.Ordinal);
                CollectionAssert.AreEquivalent(
                    new[] { "CustomTableFontSize", "CustomTableRowHeight", "CustomTableHeaderHeight" },
                    sliders.Keys.ToArray());

                sliders["CustomTableFontSize"].Value = 14d;
                sliders["CustomTableRowHeight"].Value = 31d;
                sliders["CustomTableHeaderHeight"].Value = 37d;
                Assert.AreEqual(savedFontSize, owner.ViewSettings.CustomTableFontSize);
                foreach (Slider slider in sliders.Values)
                { slider.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, Source = slider }); }
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(14d, owner.ViewSettings.CustomTableFontSize);
                Assert.AreEqual(31d, owner.ViewSettings.CustomTableRowHeight);
                Assert.AreEqual(37d, owner.ViewSettings.CustomTableHeaderHeight);
                Assert.AreEqual(14d, mainTable!.TextFontSize);
                Assert.AreEqual(31d, mainTable!.RowHeight);
                Assert.AreEqual(37d, mainTable!.HeaderHeight);
                Assert.AreEqual(14d, playlistSummaryTable!.TextFontSize);
                Assert.AreEqual(31d, playlistSummaryTable!.RowHeight);
                Assert.AreEqual(37d, playlistSummaryTable!.HeaderHeight);

                Button reset = FindDescendants<Button>(page)
                    .Single(button => Equals(button.Content, Resources.Appearance_table_reset_defaults));
                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, owner.ViewSettings.CustomTableFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, owner.ViewSettings.CustomTableRowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, owner.ViewSettings.CustomTableHeaderHeight);
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, mainTable!.TextFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, mainTable!.RowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, mainTable!.HeaderHeight);
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, playlistSummaryTable!.TextFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, playlistSummaryTable!.RowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, playlistSummaryTable!.HeaderHeight);

                ((Button)window.FindName("buttonCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, owner.ViewSettings.CustomTableFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, owner.ViewSettings.CustomTableRowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, owner.ViewSettings.CustomTableHeaderHeight);
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, mainTable!.TextFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, mainTable!.RowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, mainTable!.HeaderHeight);
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, playlistSummaryTable!.TextFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, playlistSummaryTable!.RowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, playlistSummaryTable!.HeaderHeight);

            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    try
                    {
                        window.CloseForOwnerShutdown();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
                try
                {
                    Settings.Default.CustomTableFontSize = previousFontSize;
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
                try
                {
                    Settings.Default.CustomTableRowHeight = previousRowHeight;
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
                try
                {
                    Settings.Default.CustomTableHeaderHeight = previousHeaderHeight;
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
                if (mainWindow != null && owner != null)
                {
                    try
                    {
                        ownership?.Dispose();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
                try
                {
                    ownership?.DisposeAfterBodyFailure(bodyFailure?.SourceException ?? cleanupFailure);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }

            if (bodyFailure != null)
            {
                if (cleanupFailure != null)
                {
                    bodyFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] = cleanupFailure.ToString();
                }

                bodyFailure.Throw();
            }

            if (cleanupFailure != null)
            {
                windowTest.RegisterCleanupFailureForTesting(cleanupFailure);
            }
        });
    }

    /// <summary>実 Playback ページを開くだけでは選択を変えず、radio click は対応 player と詳細だけを表示して非表示 player の保存 path を変更しないことを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_PlaybackPageAttachAndRadioClickPresentOnlySelectedPlayerWithoutChangingHiddenDrafts()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownedDialogLifetime = mainViewModel.SettingDialog;
            SettingsDialogViewModel settings = mainViewModel.SettingDialog;
            bool[] beforeFlags = [settings.UseInternalPlayer, settings.UsePlayeruBMplay, settings.UsePlayerBMIIDXView, settings.UsePlayerLR2body];
            string[] beforePaths = [settings.uBMplayPath, settings.BMIIDXViewPath, settings.LR2RootPath];
            var window = new SettingsWindow
            {
                DataContext = settings,
                Width = 820,
                Height = 600
            };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationPlayback");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (PlaybackSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                RadioButton[] radios =
                [
                    (RadioButton)page.FindName("radioButtonInternalPlayer"),
                    (RadioButton)page.FindName("radioButtonPlayuBMplay"),
                    (RadioButton)page.FindName("radioButtonPlayBMIIDXView"),
                    (RadioButton)page.FindName("radioButtonPlayLR2body")
                ];
                CollectionAssert.AreEqual(beforeFlags, new[] { settings.UseInternalPlayer, settings.UsePlayeruBMplay, settings.UsePlayerBMIIDXView, settings.UsePlayerLR2body });
                Assert.AreEqual(1, radios.Count(radio => radio.IsChecked == true));

                SettingsPathPicker[] details = FindDescendants<SettingsPathPicker>(page).ToArray();
                Assert.AreEqual(3, details.Length);
                Assert.AreEqual(beforeFlags[0] ? 0 : 1, details.Count(detail => detail.IsVisible));
                int targetIndex = beforeFlags[1] ? 2 : 1;
                typeof(RadioButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(radios[targetIndex], null);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                bool[] expectedFlags = [false, targetIndex == 1, targetIndex == 2, targetIndex == 3];
                CollectionAssert.AreEqual(expectedFlags, new[] { settings.UseInternalPlayer, settings.UsePlayeruBMplay, settings.UsePlayerBMIIDXView, settings.UsePlayerLR2body });
                CollectionAssert.AreEqual(beforePaths, new[] { settings.uBMplayPath, settings.BMIIDXViewPath, settings.LR2RootPath });
                Assert.AreEqual(1, details.Count(detail => detail.IsVisible));
                SettingsPathPicker expectedDetail = details.Single(detail =>
                    detail.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path
                    == (targetIndex == 1 ? "uBMplayPath" : "BMIIDXViewPath"));
                Assert.IsTrue(expectedDetail.IsVisible);
                Assert.IsTrue(details.Where(detail => !ReferenceEquals(detail, expectedDetail)).All(detail => !detail.IsVisible));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
                settings.Dispose();
            }
        });
    }

    /// <summary>同じ LR2 root を picker から選び直して標準 config に戻すと、通知と実 TextBox が個別 path から標準 path へ更新されることを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_GeneralConfigPathBindingUpdatesWhenSameRootRestoresStandardTuple()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingsRootReselect_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            SettingsDialogViewModel? settings = null;
            SettingsWindow? window = null;
            try
            {
                string root = Path.Combine(scope, "root");
                string standardSong = Path.Combine(root, "LR2files", "Database", "song.db");
                string standardConfig = Path.Combine(root, "LR2files", "Config", "config.xml");
                string customConfig = Path.Combine(scope, "custom", "config.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(standardSong)!);
                Directory.CreateDirectory(Path.GetDirectoryName(standardConfig)!);
                Directory.CreateDirectory(Path.GetDirectoryName(customConfig)!);
                File.WriteAllBytes(standardSong, []);
                File.WriteAllText(standardConfig, "<config><system /><jukebox /></config>");
                File.WriteAllText(customConfig, "<config><system /><jukebox /></config>");
                File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
                MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
                using SettingsDialogViewModel ownedDialogLifetime = mainViewModel.SettingDialog;
                settings = mainViewModel.SettingDialog;
                settings.LR2RootPath = root;
                settings.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), customConfig).GetAwaiter().GetResult();
                window = new SettingsWindow
                {
                    DataContext = settings,
                    PlaybackPanel = mainViewModel.PlaybackPanel,
                    Width = 820,
                    Height = 600
                };
                windowTest.ShowAndWaitForContentRendered(window);
                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                foreach (Expander details in FindDescendants<Expander>(page).ToArray()) { details.IsExpanded = true; }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                SettingsPathPicker configPathPicker = FindDescendants<SettingsPathPicker>(page).Single(picker =>
                    picker.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == nameof(settings.LR2ConfigXmlPath));
                TextBox configPathTextBox = FindDescendants<TextBox>(configPathPicker).Single();
                Assert.AreEqual(customConfig, configPathTextBox.Text);
                bool configPathNotified = false;
                settings.PropertyChanged += (_, args) => configPathNotified |= args.PropertyName == nameof(settings.LR2ConfigXmlPath);

                settings.SetRootFolderPathFromPicker(nameof(settings.LR2RootPath), root);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.IsTrue(configPathNotified);
                Assert.AreEqual(standardConfig, settings.LR2ConfigXmlPath);
                Assert.AreEqual(standardConfig, configPathTextBox.Text);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                settings?.Dispose();
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    /// <summary>整理対象の実ページでは見出しが一回となり、囲み削除・見出し非表示後も翻訳済み入力名を保持することを確認します。</summary>
    /// <remarks>既存の具体的な項目名の保証に、単純囲み・一覧・検証欄の移行先を加えます。保存・検証判断は既存の機能別ケースへ分担します。</remarks>
    [TestMethod]
    public void SettingsPages_HeadingsAppearOnceAndEditorsKeepLocalizedAutomationNames()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel mainViewModel = CreateOwnerForPresentationWithoutNativeCapabilityQuery();
            using SettingsDialogViewModel ownedDialogLifetime = mainViewModel.SettingDialog;
            var window = new SettingsWindow
            {
                DataContext = mainViewModel.SettingDialog,
                PlaybackPanel = mainViewModel.PlaybackPanel,
                Width = 820,
                Height = 600
            };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var host = (ContentControl)window.FindName("settingsPageContent");
                var assertions = new List<(string Navigation, Func<FrameworkElement, Control> Find, string Name)>
                {
                    ("navigationGeneral", page => FindDescendants<ComboBox>(page).Single(control => control.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == "Language"), Resources.Language),
                    ("navigationGeneral", page => (ListBox)page.FindName("bmsSearchRootPathListBox"), Resources.Standalone_BMSDirectories),
                    ("navigationAppearance", page => (ComboBox)page.FindName("appearanceThemeComboBox"), Resources.Appearance_theme),
                    ("navigationAppearance", page => FindDescendants<Slider>(page).Single(control => control.GetBindingExpression(RangeBase.ValueProperty)?.ParentBinding.Path?.Path == "CustomTableFontSize"), Resources.Appearance_table_font_size),
                    ("navigationPlayback", page => FindDescendants<ComboBox>(page).Single(control => control.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path?.Path == "LR2bodyResolution"), Resources.Player_LR2_wsize),
                    ("navigationAudio", page => FindDescendants<ComboBox>(page).Single(control => control.GetBindingExpression(Selector.SelectedIndexProperty)?.ParentBinding.Path?.Path == "PlayerDriverIndex"), Resources.Device_setting_driver),
                    ("navigationAudio", page => (Slider)page.FindName("audioVolumeSlider"), Resources.Device_setting_volume),
                    ("navigationInstall", page => FindDescendants<ComboBox>(page).Single(control => control.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == "BMSInstallDir"), Resources.Install_Dst),
                    ("navigationPlaylist", page => (TextBox)page.FindName("tableListUrlTextBox"), Resources.Playlist_table_uri),
                    ("navigationPlaylist", page => FindDescendants<TextBox>(page).Single(control => control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == "PlaylistMd5UrlMappingTsvUri"), Resources.Playlist_md5_url_mapping_tsv_uri),
                    ("navigationRecording", page => (ComboBox)page.FindName("comboBoxEncoder"), Resources.Record_setting_filetype),
                    ("navigationRecording", page => FindDescendants<TextBox>(page).Single(control => control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == "EncodeFileNameFormat"), Resources.Record_setting_filename),
                    ("navigationRightClick", page => (ListBox)page.FindName("webActionsListBox"), Resources.RightClick_web_actions),
                    ("navigationRightClick", page => (ListBox)page.FindName("programActionsListBox"), Resources.RightClick_program_actions)
                };
                foreach ((string categoryName, Func<FrameworkElement, Control> find, string expectedName) in assertions)
                {
                    navigation.SelectedItem = window.FindName(categoryName);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Control editor = find((FrameworkElement)host.Content);
                    // 新しいページのtemplateと入力名の接続を実体化してからAutomationを観測する。
                    window.UpdateLayout();
                    AutomationPeer peer = editor switch
                    {
                        ComboBox comboBox => new ComboBoxAutomationPeer(comboBox),
                        Slider slider => new SliderAutomationPeer(slider),
                        TextBox textBox => new TextBoxAutomationPeer(textBox),
                        ListBox listBox => new ListBoxAutomationPeer(listBox),
                        _ => throw new AssertFailedException("Unsupported representative editor: " + editor.GetType().Name)
                    };
                    Assert.AreEqual(expectedName, peer.GetName(), categoryName);
                }
                foreach ((string categoryName, string[] headings) in new[]
                {
                    ("navigationGeneral", new[] { Resources.Language, Resources.Standalone_BMSDirectories, Resources.Lr2_play_history_schema_label }),
                    ("navigationAppearance", new[] { Resources.Appearance_theme }),
                    ("navigationAudio", new[] { Resources.Device_setting_volume }),
                    ("navigationInstall", new[] { Resources.Settings_install_destination_section }),
                    ("navigationPlaylist", new[] { Resources.Playlist_table_uri, Resources.Playlist_md5_url_mapping_tsv_uri }),
                    ("navigationRecording", new[] { Resources.Record_setting_filename }),
                    ("navigationRightClick", new[] { Resources.RightClick_web_actions, Resources.RightClick_program_actions })
                })
                {
                    navigation.SelectedItem = window.FindName(categoryName);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    foreach (string heading in headings)
                    {
                        SettingsSection section = FindDescendants<SettingsSection>((FrameworkElement)host.Content)
                            .Single(section => Equals(section.Header, heading));
                        Assert.AreEqual(1, FindDescendants<TextBlock>(section).Count(text => text.IsVisible && text.Text == heading), categoryName + ": " + heading);
                    }
                }
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    /// <summary>見出しの表示切替は入力名を保持し、非表示の占有領域だけを除き、説明と警告／エラーを残すことを確認します。</summary>
    /// <remarks>実テンプレートの測定が必要ですがWindowは不要です。Fieldと派生ListEditorを同じ条件で確認し、名前所有の詳細は既存の各ケースへ分担します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SettingsField_HiddenHeaderPreservesMeaningDescriptionAndValidationWithoutEmptySpace(bool listEditor)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Grid host = CreateSettingsControlHost();
            SettingsField field = listEditor ? new SettingsListEditor() : new SettingsField();
            var editor = new TextBox { Text = "Existing value" };
            field.Header = "Localized input name";
            field.Content = editor;
            field.ValidationMessage = "Validation details";
            field.ValidationStatus = "Warning";
            host.Children.Add(field);
            Layout();
            double visibleHeight = field.DesiredSize.Height;
            Assert.IsTrue(field.IsHeaderVisible);
            Assert.AreEqual(1, FindDescendants<TextBlock>(field).Count(text => text.Visibility == Visibility.Visible && text.Text == field.Header.ToString()));

            field.IsHeaderVisible = false;
            Layout();
            Assert.IsTrue(field.DesiredSize.Height < visibleHeight);
            Assert.AreEqual(0d, editor.TransformToAncestor(field).Transform(new Point()).Y, 0.5);
            Assert.AreEqual("Localized input name", new TextBoxAutomationPeer(editor).GetName());
            Assert.IsFalse(FindDescendants<TextBlock>(field).Any(text => text.Visibility == Visibility.Visible && text.Text == field.Header.ToString()));
            foreach (string status in new[] { "Warning", "Error" })
            {
                field.Description = "Helpful explanation";
                field.ValidationStatus = status;
                Layout();
                Assert.IsTrue(FindDescendants<TextBlock>(field).Any(text => text.Visibility == Visibility.Visible && text.Text == field.Description));
                Assert.AreEqual(status, AutomationProperties.GetItemStatus(field));
                Assert.AreEqual(field.ValidationMessage, AutomationProperties.GetHelpText(field));
                TextBlock validation = FindDescendants<TextBlock>(field).Single(text => text.Text == field.ValidationMessage);
                Assert.AreEqual(Visibility.Visible, validation.Visibility);
                AssertBrushColor(field, status == "Warning" ? "App.WarningTextBrush" : "App.ErrorTextBrush", validation.Foreground);
            }
            field.IsHeaderVisible = true;
            Layout();
            Assert.AreEqual(1, FindDescendants<TextBlock>(field).Count(text => text.Visibility == Visibility.Visible && text.Text == field.Header.ToString()));

            void Layout()
            {
                host.Measure(new Size(500, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, 500, host.DesiredSize.Height));
                host.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
            }
        });
    }

    /// <summary>Automation 名を持たない editor は可視／非表示のfield headerを名前に使い、その変更にも追従して古い名前を残さないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SettingsField_UnnamedEditorFallbackTracksHeaderChanges(bool isHeaderVisible)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { IsHeaderVisible = isHeaderVisible, Header = "Initial localized label", Content = editor };
            var peer = new ComboBoxAutomationPeer(editor);

            Assert.AreEqual("Initial localized label", peer.GetName());

            field.Header = "Updated localized label";

            Assert.AreEqual("Updated localized label", peer.GetName());
        });
    }

    /// <summary>editor 側の明示名と binding 名がある場合は field header を変更しても上書きせず、binding も保つことを確認します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SettingsField_ExplicitAndBoundEditorNamesAreNotClobbered(bool isHeaderVisible)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var explicitEditor = new ComboBox();
            AutomationProperties.SetName(explicitEditor, "Explicit localized name");
            var explicitField = new SettingsField { IsHeaderVisible = isHeaderVisible, Header = "Field fallback", Content = explicitEditor };
            var explicitPeer = new ComboBoxAutomationPeer(explicitEditor);

            var boundEditor = new ComboBox();
            BindingOperations.SetBinding(
                boundEditor,
                AutomationProperties.NameProperty,
                new Binding { Source = "Bound localized name" });
            var boundField = new SettingsField { IsHeaderVisible = isHeaderVisible, Header = "Field fallback", Content = boundEditor };
            var boundPeer = new ComboBoxAutomationPeer(boundEditor);

            explicitField.Header = "Changed fallback";
            boundField.Header = "Changed fallback";

            Assert.AreEqual("Explicit localized name", explicitPeer.GetName());
            Assert.AreEqual("Bound localized name", boundPeer.GetName());
            Assert.IsNotNull(BindingOperations.GetBindingExpression(boundEditor, AutomationProperties.NameProperty));
        });
    }

    /// <summary>呼出側が fallback と同じ値を明示名として引き取った後は、header 変更と detach でその明示値を消去しないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SettingsField_CallerLocalTakeoverMatchingFallbackSurvivesHeaderChangeAndDetach(bool isHeaderVisible)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { IsHeaderVisible = isHeaderVisible, Header = "A", Content = editor };
            BindingExpressionBase ownedExpression = BindingOperations.GetBindingExpressionBase(
                editor,
                AutomationProperties.NameProperty);
            Assert.IsNotNull(ownedExpression);
            Assert.AreEqual("A", new ComboBoxAutomationPeer(editor).GetName());

            AutomationProperties.SetName(editor, "A");
            field.Header = "B";
            field.Content = null;

            Assert.AreEqual("A", AutomationProperties.GetName(editor));
            Assert.AreEqual("A", editor.ReadLocalValue(AutomationProperties.NameProperty));
            Assert.IsNull(BindingOperations.GetBindingExpressionBase(editor, AutomationProperties.NameProperty));
        });
    }

    /// <summary>呼出側が同じ表示値の別 binding へ置き換えた後は、header 変更と detach でもその binding を保持することを確認します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SettingsField_CallerBindingTakeoverMatchingFallbackSurvivesHeaderChangeAndDetach(bool isHeaderVisible)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { IsHeaderVisible = isHeaderVisible, Header = "A", Content = editor };
            BindingExpressionBase ownedExpression = BindingOperations.GetBindingExpressionBase(
                editor,
                AutomationProperties.NameProperty);
            Assert.IsNotNull(ownedExpression);
            Assert.AreEqual("A", new ComboBoxAutomationPeer(editor).GetName());

            BindingExpressionBase callerExpression = BindingOperations.SetBinding(
                editor,
                AutomationProperties.NameProperty,
                new Binding { Source = "A", Mode = BindingMode.OneWay });
            Assert.AreNotSame(ownedExpression, callerExpression);
            Assert.AreSame(
                callerExpression,
                BindingOperations.GetBindingExpressionBase(editor, AutomationProperties.NameProperty));

            field.Header = "B";
            field.Content = null;

            Assert.AreSame(
                callerExpression,
                BindingOperations.GetBindingExpressionBase(editor, AutomationProperties.NameProperty));
            Assert.AreEqual("A", AutomationProperties.GetName(editor));
        });
    }

    /// <summary>fallback と同値の明示名へ変更してすぐ detach しても、field が所有中と誤認して値を消去しないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SettingsField_CallerLocalTakeoverMatchingFallbackSurvivesImmediateDetach(bool isHeaderVisible)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { IsHeaderVisible = isHeaderVisible, Header = "A", Content = editor };
            Assert.IsNotNull(BindingOperations.GetBindingExpressionBase(editor, AutomationProperties.NameProperty));

            AutomationProperties.SetName(editor, "A");
            field.Content = null;

            Assert.AreEqual("A", editor.ReadLocalValue(AutomationProperties.NameProperty));
            Assert.AreEqual("A", AutomationProperties.GetName(editor));
        });
    }

    /// <summary>fallback と同値の呼出側 binding へ変更してすぐ detach しても、その binding と名前を消去しないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SettingsField_CallerBindingTakeoverMatchingFallbackSurvivesImmediateDetach(bool isHeaderVisible)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { IsHeaderVisible = isHeaderVisible, Header = "A", Content = editor };
            BindingExpressionBase ownedExpression = BindingOperations.GetBindingExpressionBase(
                editor,
                AutomationProperties.NameProperty);
            BindingExpressionBase callerExpression = BindingOperations.SetBinding(
                editor,
                AutomationProperties.NameProperty,
                new Binding { Source = "A", Mode = BindingMode.OneWay });
            Assert.AreNotSame(ownedExpression, callerExpression);

            field.Content = null;

            Assert.AreSame(
                callerExpression,
                BindingOperations.GetBindingExpressionBase(editor, AutomationProperties.NameProperty));
            Assert.AreEqual("A", AutomationProperties.GetName(editor));
        });
    }

    /// <summary>DB／config の実 browse は警告確認の承認後だけ picker へ進み、取消では標準 pair を保ち、選択受理では対象だけを個別 path へ更新することを確認します。</summary>
    [DataTestMethod]
    // 各対象で確認取消・picker 取消・選択成功を同じ実 button から分岐させる。
    [DataRow(nameof(SettingsDialogViewModel.LR2SongDBPath), false, false)]
    [DataRow(nameof(SettingsDialogViewModel.LR2SongDBPath), true, false)]
    [DataRow(nameof(SettingsDialogViewModel.LR2SongDBPath), true, true)]
    [DataRow(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), false, false)]
    [DataRow(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), true, false)]
    [DataRow(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), true, true)]
    public void SettingsWindow_GeneralLr2IndividualPathPickersRequireConfirmationBeforeFileSelection(
        string propertyName,
        bool acceptConfirmation,
        bool acceptFileSelection)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingsLr2IndividualRoute_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            SettingsWindow? window = null;
            SettingsDialogViewModel? settings = null;
            try
            {
                string root = Path.Combine(scope, "root");
                string standardSong = Path.Combine(root, "LR2files", "Database", "song.db");
                string standardConfig = Path.Combine(root, "LR2files", "Config", "config.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(standardSong)!);
                Directory.CreateDirectory(Path.GetDirectoryName(standardConfig)!);
                File.WriteAllBytes(standardSong, []);
                File.WriteAllText(standardConfig, "<config><system /><jukebox /></config>");
                File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);

                string individualPath = propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath)
                    ? Path.Combine(scope, "individual", "songs.db")
                    : Path.Combine(scope, "individual", "config.xmh");
                Directory.CreateDirectory(Path.GetDirectoryName(individualPath)!);
                if (propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath))
                {
                    File.WriteAllBytes(individualPath, []);
                }
                else
                {
                    File.WriteAllText(individualPath, "<config><system /><jukebox /></config>");
                }

                MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
                using SettingsDialogViewModel ownedDialogLifetime = mainViewModel.SettingDialog;
                settings = mainViewModel.SettingDialog;
                MainWindowViewModelTestFactory.GetComposition(mainViewModel).SettingsEditSession.Values.LR2RootPath = root;
                MainWindowViewModelTestFactory.GetComposition(mainViewModel).SettingsEditSession.Values.LR2SongDBPath = standardSong;
                MainWindowViewModelTestFactory.GetComposition(mainViewModel).SettingsEditSession.Values.LR2ConfigXmlPath = standardConfig;
                settings.LR2RootPath = root;
                settings.OperationModeLR2DB = true;
                Assert.AreEqual(standardSong, settings.LR2SongDBPath);
                Assert.AreEqual(standardConfig, settings.LR2ConfigXmlPath);

                var dialogs = new RecordingSettingsRouteDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(
                        acceptConfirmation ? MessageBoxResult.OK : MessageBoxResult.Cancel),
                    FileResult = acceptFileSelection
                        ? new UiFilePickerResult(UiDialogStatus.Accepted, [individualPath])
                        : new UiFilePickerResult(UiDialogStatus.CancelledByUser)
                };
                window = new SettingsWindow(dialogs)
                {
                    DataContext = settings,
                    Width = 820,
                    Height = 600
                };
                dialogs.ExpectedOwner = window;
                windowTest.ShowAndWaitForContentRendered(window);

                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                Expander pathDetails = FindDescendants<Expander>(page).Single(details => details.Content is DependencyObject contents
                    && FindDescendants<SettingsPathPicker>(contents).Any(candidate =>
                        candidate.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.LR2SongDBPath)));
                Assert.IsFalse(pathDetails.IsExpanded);
                pathDetails.IsExpanded = true;
                window.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                SettingsPathPicker[] lr2PathPickers = FindDescendants<SettingsPathPicker>(page)
                    .Where(picker => picker.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path
                        is nameof(SettingsDialogViewModel.LR2RootPath)
                            or nameof(SettingsDialogViewModel.LR2SongDBPath)
                            or nameof(SettingsDialogViewModel.LR2ConfigXmlPath))
                    .ToArray();
                Assert.AreEqual(3, lr2PathPickers.Length);

                SettingsPathPicker picker = lr2PathPickers.Single(candidate =>
                    candidate.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == propertyName);
                picker.ApplyTemplate();
                picker.UpdateLayout();
                Assert.IsTrue(picker.IsPathReadOnly);
                Assert.IsTrue(FindDescendants<TextBox>(picker).Single().IsReadOnly);
                Button browseButton = FindDescendants<Button>(picker)
                    .Single(button => Equals(button.Content, picker.BrowseText));
                Assert.IsTrue(browseButton.IsEnabled);

                browseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
                UiConfirmationRequest confirmation = dialogs.ConfirmationRequests.Single();
                Assert.AreSame(window, confirmation.Owner);
                Assert.AreEqual(Resources.Settings_lr2_individual_path_warning, confirmation.MessageBoxText);
                Assert.AreEqual(Resources.Warning, confirmation.Caption);
                Assert.AreEqual(MessageBoxButton.OKCancel, confirmation.Button);
                Assert.AreEqual(MessageBoxImage.Warning, confirmation.Icon);
                Assert.AreEqual(MessageBoxResult.Cancel, confirmation.DefaultResult);

                if (!acceptConfirmation)
                {
                    Assert.AreEqual(0, dialogs.FileRequests.Count);
                    Assert.AreEqual(standardSong, settings.LR2SongDBPath);
                    Assert.AreEqual(standardConfig, settings.LR2ConfigXmlPath);
                    Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, settings.Lr2SongDbPathStatusText);
                    Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, settings.Lr2ConfigPathStatusText);
                    return;
                }

                Assert.AreEqual(1, dialogs.FileRequests.Count);
                UiFilePickerRequest fileRequest = dialogs.FileRequests.Single();
                Assert.AreSame(window, fileRequest.Owner);
                Assert.AreEqual(
                    Path.GetDirectoryName(propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath) ? standardSong : standardConfig),
                    fileRequest.InitialDirectory);
                if (!acceptFileSelection)
                {
                    Assert.AreEqual(standardSong, settings.LR2SongDBPath);
                    Assert.AreEqual(standardConfig, settings.LR2ConfigXmlPath);
                    Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, settings.Lr2SongDbPathStatusText);
                    Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, settings.Lr2ConfigPathStatusText);
                    return;
                }

                if (propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath))
                {
                    StringAssert.Contains(fileRequest.Filter, "*.db");
                    Assert.AreEqual(individualPath, settings.LR2SongDBPath);
                    Assert.AreEqual(standardConfig, settings.LR2ConfigXmlPath);
                    Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, settings.Lr2SongDbPathStatusText);
                    Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, settings.Lr2ConfigPathStatusText);
                }
                else
                {
                    StringAssert.Contains(fileRequest.Filter, "config.xm?");
                    Assert.AreEqual(standardSong, settings.LR2SongDBPath);
                    Assert.AreEqual(individualPath, settings.LR2ConfigXmlPath);
                    Assert.AreEqual(Resources.Settings_path_detected_from_lr2_root, settings.Lr2SongDbPathStatusText);
                    Assert.AreEqual(Resources.Settings_path_detected_from_individual_setting, settings.Lr2ConfigPathStatusText);
                }
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                settings?.Dispose();
                Directory.Delete(scope, recursive: true);
            }
        });
    }


    /// <summary>個別 path の警告確認を gate で保持しても dispatcher の別処理を進め、取消後は file picker を開始せず実 Task を完了することを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_Lr2IndividualPathRouteAwaitsConfirmationWithoutBlockingDispatcher()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownedDialogLifetime = mainViewModel.SettingDialog;
            SettingsDialogViewModel settings = mainViewModel.SettingDialog;
            settings.OperationModeLR2DB = true;
            var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = new RecordingSettingsRouteDialogService();
            dialogs.ConfirmationTasks.Enqueue(confirmation.Task);
            var window = new SettingsWindow(dialogs)
            {
                DataContext = settings,
                Width = 820,
                Height = 600
            };
            dialogs.ExpectedOwner = window;
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            Task? route = null;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);

                route = window.HandleBrowseLr2SongDbPathAsync();

                Assert.IsFalse(route.IsCompleted);
                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
                Assert.AreEqual(0, dialogs.FileRequests.Count);

                bool dispatcherWorkCompleted = false;
                DispatcherOperation backgroundWork = window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => dispatcherWorkCompleted = true));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(backgroundWork.Task, "settings-pending-background-work");
                Assert.IsTrue(dispatcherWorkCompleted);
                Assert.IsFalse(route.IsCompleted);

                confirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(route, "settings-route-terminal");
                Assert.IsTrue(route.IsCompletedSuccessfully);
                Assert.AreEqual(0, dialogs.FileRequests.Count);
            }
            finally
            {
                confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
                if (route != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(route, "settings-confirmation-finally-route"); }
                SynchronizationContext.SetSynchronizationContext(previousContext);
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    /// <summary>確認／picker の表示失敗を一回だけ通知し、通知自体の失敗もログへ分けて dispatcher に未処理例外や再帰通知を出さないことを確認します。</summary>
    [DataTestMethod]
    // 確認／picker 失敗の両段階で、失敗通知の成立／不成立を比較する。
    [DataRow(false, false, 1)]
    [DataRow(false, true, 2)]
    [DataRow(true, false, 1)]
    [DataRow(true, true, 2)]
    public void SettingsWindow_Lr2IndividualPathDialogFailureIsReportedOnceWithoutUnhandledException(
        bool failFilePicker,
        bool notificationFails,
        int expectedLogCount)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            LoggingConfiguration? originalConfiguration = LogManager.Configuration;
            var logTarget = new MemoryTarget { Layout = "${message}|${exception:format=message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Error, LogLevel.Fatal, logTarget);
            LogManager.Configuration = configuration;
            SettingsWindow? window = null;
            try
            {
                MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
                using SettingsDialogViewModel ownedDialogLifetime = mainViewModel.SettingDialog;
                SettingsDialogViewModel settings = mainViewModel.SettingDialog;
                settings.OperationModeLR2DB = true;
                var dialogs = new RecordingSettingsRouteDialogService
                {
                    ConfirmationResult = failFilePicker
                        ? UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
                        : UiDialogResult.Failed(new InvalidOperationException("confirmation failed")),
                    FileResult = new UiFilePickerResult(
                        UiDialogStatus.Failed,
                        error: new InvalidOperationException("file picker failed")),
                    MessageResult = notificationFails
                        ? UiDialogResult.Failed(new InvalidOperationException("notification failed"))
                        : UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
                };
                window = new SettingsWindow(dialogs)
                {
                    DataContext = settings,
                    Width = 820,
                    Height = 600
                };
                dialogs.ExpectedOwner = window;
                var unhandled = new List<Exception>();
                DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
                {
                    unhandled.Add(args.Exception);
                    args.Handled = true;
                };
                window.Dispatcher.UnhandledException += handler;
                try
                {
                    windowTest.ShowAndWaitForContentRendered(window);
                    var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                    foreach (Expander details in FindDescendants<Expander>(page).ToArray()) { details.IsExpanded = true; }
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(page).Single(candidate =>
                        candidate.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path
                            == nameof(SettingsDialogViewModel.LR2SongDBPath));
                    FindDescendants<Button>(picker)
                        .Single(button => Equals(button.Content, picker.BrowseText))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
                finally
                {
                    window.Dispatcher.UnhandledException -= handler;
                }

                LogManager.Flush();
                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
                Assert.AreEqual(failFilePicker ? 1 : 0, dialogs.FileRequests.Count);
                Assert.AreEqual(1, dialogs.MessageRequests.Count, "Failure notification must not recurse.");
                UiMessageRequest notification = dialogs.MessageRequests.Single();
                Assert.AreSame(window, notification.Owner);
                Assert.AreEqual(Resources.Msg_error_unexpected, notification.MessageBoxText);
                Assert.IsFalse(
                    notification.MessageBoxText.Contains("LR2 song database picker", StringComparison.Ordinal),
                    "Internal route names must remain in diagnostics instead of leaking into localized UI text.");
                Assert.AreEqual(0, unhandled.Count);
                Assert.AreEqual(expectedLogCount, logTarget.Logs.Count);
                StringAssert.Contains(logTarget.Logs[0], "LR2 song database picker failed");
                if (notificationFails)
                {
                    StringAssert.Contains(logTarget.Logs[1], "failure notification was not shown");
                }
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                LogManager.Flush();
                LogManager.Configuration = originalConfiguration;
            }
        });
    }

    /// <summary>Show 後の ContentRendered が未配送でも owner Close で設定を detach し、実遅延 callback が来ても再接続や未処理例外を起こさないことを確認します。</summary>
    [TestMethod]
    public void SettingsWindow_OwnerShutdownBeforeContentRenderedCannotReactivateDetachedPresentation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownedDialogLifetime = mainViewModel.SettingDialog;
            SettingsDialogViewModel settings = mainViewModel.SettingDialog;
            var window = new SettingsWindow
            {
                DataContext = settings,
                Width = 820,
                Height = 600
            };
            var dispatcherFailures = new List<Exception>();
            var contentRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherUnhandledExceptionEventHandler unhandledHandler = (_, args) =>
            {
                dispatcherFailures.Add(args.Exception);
                args.Handled = true;
            };
            EventHandler contentRenderedHandler = (_, _) => contentRendered.TrySetResult();
            window.Dispatcher.UnhandledException += unhandledHandler;
            window.ContentRendered += contentRenderedHandler;
            try
            {
                windowTest.PrepareForOwnedPresentation(window);
                window.Show();
                Assert.IsFalse(contentRendered.Task.IsCompleted,
                    "Show must return before the queued ContentRendered callback in this lifecycle scenario.");

                window.CloseForOwnerShutdown();
                Assert.AreEqual(SettingsWindowCloseReason.OwnerShutdown, window.CloseReason);
                Assert.IsNull(window.DataContext, "Owner shutdown must detach the shared settings DataContext.");
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(contentRendered.Task,
                    "owner-shutdown-delayed-content-rendered");

                Assert.IsTrue(contentRendered.Task.IsCompletedSuccessfully,
                    "The actual delayed ContentRendered callback must be observed after close.");
                Assert.AreEqual(0, dispatcherFailures.Count,
                    "A delayed ContentRendered callback must not escape through Dispatcher.UnhandledException.");
                Assert.IsNull(window.DataContext);
            }
            finally
            {
                window.ContentRendered -= contentRenderedHandler;
                window.Dispatcher.UnhandledException -= unhandledHandler;
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    private static string CreateDangerSchemaScope(out Settings values, out string scoreDbPath)
    {
        string scope = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingsDanger_" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(scope, "LR2files", "Config", "config.xml");
        scoreDbPath = Path.Combine(scope, "LR2files", "Database", "Score", "player1.db");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scoreDbPath)!);
        File.WriteAllText(configPath, "<config><system/><jukebox/><player><id>player1</id></player></config>");
        using (var database = new SQLiteConnection(scoreDbPath))
        {
            database.CreateTable<LR2ScoreDB.score>();
            database.CreateTable<LR2ScoreDB.player>();
        }
        new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
        values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2RootPath = scope;
                values.LR2ConfigXmlPath = configPath;
                values.LR2SongDBPath = Path.Combine(scope, "LR2files", "Database", "song.db");
            });
        return scope;
    }

    private static DangerDialogContext CreateDangerDialog(
        Settings values,
        DangerDialogService dialogs,
        DangerSchemaDialogPort schemaDialog,
        DangerApplicationDataStore store)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        owner.SettingDialog.Dispose();
        var composition = new ApplicationComposition(
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        return CreateDangerDialog(
            owner,
            new DangerSettingsEditSession(values, []),
            composition,
            dialogs,
            schemaDialog,
            store);
    }

    private static DangerDialogContext CreateDangerDialog(
        MainWindowViewModel owner,
        DangerSettingsEditSession settingsEditSession,
        ApplicationComposition composition,
        DangerDialogService dialogs,
        DangerSchemaDialogPort schemaDialog,
        DangerApplicationDataStore store,
        IApplicationLifetimePort? applicationLifetime = null,
        DangerWorkspacePort? workspace = null,
        DangerStatePort? statePort = null,
        Action<Exception>? reportFailure = null)
    {
        int reloadCount = 0;
        statePort ??= new DangerStatePort(
            reloadScoresOnly: () =>
            {
                reloadCount++;
                return Task.CompletedTask;
            });
        workspace ??= new DangerWorkspacePort();
        var playHistory = new DangerPlayHistoryPort();
        var settings = new SettingsDialogViewModel(
            statePort,
            workspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            composition,
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsEditSession,
            reportApplyFailure: reportFailure,
            schemaDialogs: dialogs,
            schemaWindowDialogs: schemaDialog,
            applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(dialogs, store),
            applicationLifetime: applicationLifetime ?? TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
        return new DangerDialogContext(
            settings,
            settingsEditSession,
            playHistory,
            () => reloadCount);
    }

    private static ShellActivationWorkflowOwner CreateNoOpShellActivationWorkflow()
    {
        var startupUpdate = new StartupUpdateWorkflowOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            _ => Task.FromResult(string.Empty),
            _ => throw new InvalidOperationException("The no-update fixture cannot prepare an updater."),
            () => { },
            _ => { },
            action => Task.Run(action),
            action => action());
        return new ShellActivationWorkflowOwner(
            startupUpdate,
            new ElevatedProcessWarningWorkflowOwner(() => false),
            () => Task.FromResult(true));
    }

    private static void ClickAdvancedDangerButton(SettingsWindow window, string content)
    {
        ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAdvanced");
        TestUiDispatcherHost.ProcessQueuedPresentation();
        var page = (AdvancedSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
        Button button = FindDescendants<Button>(page).Single(candidate => Equals(candidate.Content, content));
        Assert.IsTrue(button.IsEnabled, content + " button must be enabled for the test fixture.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static void SetSchemaPresentationStatus(
        SchemaPresentationStatePort statePort,
        string scoreDbPath,
        Lr2PlayHistorySchemaStatus status)
    {
        var snapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(new Lr2PlayHistorySchemaCheckResult
        {
            Status = status,
            ScoreDbPath = scoreDbPath,
            Message = status.ToString()
        });
        statePort.NotifyLr2PlayHistorySchemaStatusChanged(snapshot);
        Assert.IsNotNull(statePort.LastSettingsDialog);
        Assert.AreEqual(
            status == Lr2PlayHistorySchemaStatus.Repairable,
            statePort.LastSettingsDialog.CanInstallOrRepairLr2PlayHistorySchema);
    }

    private static SchemaPresentationContext CreateSchemaPresentationContext(
        string scope,
        Settings values,
        string scoreDbPath)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        var settingsSession = new NoOpSettingsEditSession(values);
        var composition = new ApplicationComposition(
            settingsEditSession: settingsSession,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var statePort = new SchemaPresentationStatePort();
        var settings = new SettingsDialogViewModel(
            statePort,
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            owner.LibraryFolderTree,
            composition,
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsSession,
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
        statePort.LastSettingsDialog = settings;
        return new SchemaPresentationContext(scoreDbPath, owner, settings, statePort);
    }

    private sealed class SchemaPresentationContext
    {
        internal SchemaPresentationContext(
            string scoreDbPath,
            MainWindowViewModel owner,
            SettingsDialogViewModel settings,
            SchemaPresentationStatePort statePort)
        {
            ScoreDbPath = scoreDbPath;
            Owner = owner;
            Settings = settings;
            StatePort = statePort;
        }

        internal string ScoreDbPath { get; }

        internal MainWindowViewModel Owner { get; }

        internal SettingsDialogViewModel Settings { get; }

        internal SchemaPresentationStatePort StatePort { get; }
    }

    private static void AssertSchemaBannerState(SettingsStatusBanner banner, string icon, string status)
    {
        Assert.AreEqual(icon, banner.Icon);
        Assert.AreEqual(status, banner.Status);
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(banner);
        Assert.IsNotNull(peer);
        Assert.AreEqual(status, peer.GetItemStatus());
    }

    private static IEnumerable<AutomationPeer> EnumerateAutomationDescendants(AutomationPeer parent)
    {
        foreach (AutomationPeer child in parent.GetChildren() ?? [])
        {
            yield return child;
            foreach (AutomationPeer descendant in EnumerateAutomationDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>compiled schema操作の必須処理後に画面の操作受付が復帰した通知を待ちます。</summary>
    private static void AwaitViewOperationTerminal(SettingsDialogViewModel settings)
    {
        TestUiDispatcherHost.AwaitTaskOnDispatcher(settings.SettingsOperationCompletionTask, "settings-view-operation-terminal");
    }

    private static void AssertInstalledSchema(string scoreDbPath, bool expectedInstalled)
    {
        using var database = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        int objectCount = database.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM sqlite_master WHERE name LIKE 'bms_lr2_%' OR name LIKE 'idx_bms_lr2_%';");
        Assert.AreEqual(expectedInstalled, objectCount > 0);
    }


    private static void SeedTransientPlaylistUriValidation(SettingsDialogViewModel settings)
    {
        // URI 入力の拒否は別ケースが分担する。一時検証状態を与え、表示終了と再表示による解除だけを実 Window で観測する。
        typeof(SettingsDialogViewModel)
            .GetField("tableListUriValidationMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, "invalid table URI");
        typeof(SettingsDialogViewModel)
            .GetField("playlistMd5UrlMappingTsvUriValidationMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, "invalid mapping URI");
    }

    private static string GetAppliedApplicationTheme()
    {
        const string prefix = "/BeMusicSeeker;component/Themes/";
        string[] sources = Application.Current.Resources.MergedDictionaries
            .Select(dictionary => dictionary.Source?.OriginalString)
            .Where(value => AppThemeService.AvailableThemes.Any(theme =>
                string.Equals(value, prefix + theme + ".xaml", StringComparison.OrdinalIgnoreCase)))
            .Select(value => value!)
            .ToArray();
        Assert.AreEqual(1, sources.Length, "登録済みテーマの辞書がちょうど一つ適用されている必要があります。");
        return Path.GetFileNameWithoutExtension(sources[0]);
    }

    /// <summary>LR2 root・導入先・二種の出力先が、実 VM の検証 message／status を compiled ページへ受け取り、項目間の誤配線や検証表示 style の不採用を検出します。</summary>
    /// <remarks>BAML の接続を Window 表示なしで観測します。未設定時の判断条件は下位の設定検証、色・説明・Automation の表示は SettingsValidationPresentation が分担します。</remarks>
    [TestMethod]
    public void SettingsPages_RequiredValidationBindingsUseSharedWarningAndErrorPresentation()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(settings =>
            {
                settings.OperationModeLR2DB = true;
                settings.LR2RootPath = string.Empty;
                settings.BMSInstallDir = string.Empty;
                settings.LR2CustomFolderOutputBaseDir = string.Empty;
                settings.LR2CustomFolderOutputBaseDirRootType = string.Empty;
            });
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
            SettingsDialogViewModel settings = owner.SettingDialog;
            UserControl[] pages = [new GeneralSettingsPage(), new InstallSettingsPage(), new PlaylistSettingsPage()];
            try
            {
                foreach (UserControl page in pages)
                {
                    page.DataContext = settings;
                    page.ApplyTemplate();
                    page.Measure(new Size(900, double.PositiveInfinity));
                    page.Arrange(new Rect(0, 0, 900, page.DesiredSize.Height));
                    page.UpdateLayout();
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();

                // 入力プロパティで配送先を特定し、各配送先の公開検証プロパティと実 VM 出力を照合する。
                foreach ((UserControl page, string path, string message, string status) in new[]
                {
                    (pages[0], nameof(SettingsDialogViewModel.LR2RootPath), nameof(SettingsDialogViewModel.Lr2RootPathValidationMessage), nameof(SettingsDialogViewModel.Lr2RootPathValidationStatus)),
                    (pages[2], nameof(SettingsDialogViewModel.LR2CustomFolderOutputDir), nameof(SettingsDialogViewModel.CustomFolderOutputDirValidationMessage), nameof(SettingsDialogViewModel.CustomFolderOutputDirValidationStatus)),
                    (pages[2], nameof(SettingsDialogViewModel.LR2CustomFolderAsRootOutputDir), nameof(SettingsDialogViewModel.CustomFolderRootOutputDirValidationMessage), nameof(SettingsDialogViewModel.CustomFolderRootOutputDirValidationStatus))
                })
                {
                    SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(page).Single(candidate =>
                        candidate.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == path);
                    AssertValidationBinding(picker, SettingsPathPicker.ValidationMessageProperty, message);
                    AssertValidationBinding(picker, SettingsPathPicker.ValidationStatusProperty, status);
                    AssertStyleIsAdopted(picker, typeof(SettingsPathPicker));
                }
                ComboBox installCombo = FindDescendants<ComboBox>(pages[1]).Single(candidate =>
                    candidate.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BMSInstallDir));
                SettingsField installField = FindDescendants<SettingsField>(pages[1]).Single(field =>
                    FindDescendants<ComboBox>(field).Contains(installCombo));
                AssertValidationBinding(installField, SettingsField.ValidationMessageProperty, nameof(SettingsDialogViewModel.BmsInstallDirValidationMessage));
                AssertValidationBinding(installField, SettingsField.ValidationStatusProperty, nameof(SettingsDialogViewModel.BmsInstallDirValidationStatus));
                AssertStyleIsAdopted(installField, typeof(SettingsField));
                AssertStyleIsAdopted(installCombo, "SettingsValidatedComboBoxStyle");

                void AssertValidationBinding(FrameworkElement control, DependencyProperty property, string sourceProperty)
                {
                    BindingExpression? expression = control.GetBindingExpression(property);
                    Assert.IsNotNull(expression, sourceProperty);
                    Assert.AreEqual(BindingStatus.Active, expression.Status, sourceProperty);
                    Assert.AreSame(settings, expression.DataItem, sourceProperty);
                    Assert.AreEqual(sourceProperty, expression.ParentBinding.Path?.Path);
                    PropertyInfo? outputProperty = typeof(SettingsDialogViewModel).GetProperty(sourceProperty);
                    Assert.IsNotNull(outputProperty, sourceProperty);
                    object? output = outputProperty.GetValue(settings);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(output as string), sourceProperty);
                    Assert.AreEqual(output, control.GetValue(property), sourceProperty);
                }
                static void AssertStyleIsAdopted(FrameworkElement control, object key)
                {
                    var expected = control.TryFindResource(key) as Style;
                    Assert.IsNotNull(expected, key.ToString());
                    bool adopted = false;
                    for (Style? candidate = control.Style; candidate != null; candidate = candidate.BasedOn)
                    {
                        adopted |= ReferenceEquals(candidate, expected);
                    }
                    Assert.IsTrue(adopted, key.ToString());
                }
            }
            finally
            {
                foreach (UserControl page in pages) { page.DataContext = null; }
                settings.Dispose();
            }
        });
    }

    /// <summary>警告／エラーを入力した実 picker と editor に本文、Automation 状態／説明、意味 palette が届き、色だけに依存した通知にならないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SettingsValidationPresentation_ExposesWarningAndErrorWithoutRelyingOnColorAlone()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Grid host = CreateSettingsControlHost();
            var panel = new StackPanel();
            host.Children.Add(panel);
            var warningPicker = new SettingsPathPicker
            {
                Label = "LR2 directory",
                Path = string.Empty,
                ValidationMessage = "Configure the LR2 directory.",
                ValidationStatus = "Warning"
            };
            var errorPicker = new SettingsPathPicker
            {
                Label = "Playlist output",
                Path = string.Empty,
                ValidationMessage = "Select an output directory.",
                ValidationStatus = "Error"
            };
            var errorField = new SettingsField
            {
                Header = "Install destination",
                ValidationMessage = "Select an install destination.",
                ValidationStatus = "Error"
            };
            var errorEditor = new ComboBox
            {
                Style = (Style)host.FindResource("SettingsValidatedComboBoxStyle")
            };
            errorField.Content = errorEditor;
            panel.Children.Add(warningPicker);
            panel.Children.Add(errorPicker);
            panel.Children.Add(errorField);
            var window = new Window { Content = host, Width = 640, Height = 320 };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                window.UpdateLayout();

                TextBox warningEditor = FindDescendants<TextBox>(warningPicker).Single();
                Assert.AreEqual("Warning", AutomationProperties.GetItemStatus(warningEditor));
                Assert.AreEqual(warningPicker.ValidationMessage, AutomationProperties.GetHelpText(warningEditor));
                Assert.AreEqual("Warning", AutomationProperties.GetItemStatus(warningPicker));
                Assert.AreEqual(warningPicker.ValidationMessage, AutomationProperties.GetHelpText(warningPicker));
                AssertBrushColor(warningPicker, "App.WarningTextBrush", warningEditor.BorderBrush);
                Assert.IsTrue(FindDescendants<TextBlock>(warningPicker).Any(text =>
                    text.Text == warningPicker.ValidationMessage && text.Visibility == Visibility.Visible));

                TextBox errorPathEditor = FindDescendants<TextBox>(errorPicker).Single();
                Assert.AreEqual("Error", AutomationProperties.GetItemStatus(errorPathEditor));
                Assert.AreEqual(errorPicker.ValidationMessage, AutomationProperties.GetHelpText(errorPathEditor));
                AssertBrushColor(errorPicker, "App.ErrorTextBrush", errorPathEditor.BorderBrush);
                Assert.IsTrue(FindDescendants<TextBlock>(errorPicker).Any(text =>
                    text.Text == errorPicker.ValidationMessage && text.Visibility == Visibility.Visible));

                Assert.AreEqual("Error", AutomationProperties.GetItemStatus(errorField));
                Assert.AreEqual(errorField.ValidationMessage, AutomationProperties.GetHelpText(errorField));
                Assert.AreEqual("Error", AutomationProperties.GetItemStatus(errorEditor));
                Assert.AreEqual(errorField.ValidationMessage, AutomationProperties.GetHelpText(errorEditor));
                AssertBrushColor(errorField, "App.ErrorTextBrush", errorEditor.BorderBrush);
                Assert.IsTrue(FindDescendants<TextBlock>(errorField).Any(text =>
                    text.Text == errorField.ValidationMessage && text.Visibility == Visibility.Visible));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>実 Window を最小幅で表示して全 navigation を巡り、page と editor の表示領域が viewport から横にはみ出さないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SettingsWindow_AllPagesFitActualMinimumWidthViewport()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = CreateOwnerForPresentationWithoutNativeCapabilityQuery();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            window.Width = window.MinWidth;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var scroller = (ScrollViewer)window.FindName("settingsPageScrollViewer");
                var content = (ContentControl)window.FindName("settingsPageContent");

                for (int categoryIndex = 0; categoryIndex < navigation.Items.Count; categoryIndex++)
                {
                    navigation.SelectedIndex = categoryIndex;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    window.UpdateLayout();

                    var page = (FrameworkElement)content.Content;
                    string pageName = page.GetType().Name;
                    Assert.IsTrue(scroller.ViewportWidth > 0, pageName);
                    Assert.IsTrue(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5,
                        $"{pageName} produced horizontal extent {scroller.ExtentWidth} in viewport {scroller.ViewportWidth}.");
                    Assert.AreEqual(0d, scroller.ScrollableWidth, 0.5, pageName);
                    Assert.IsTrue(content.ActualWidth <= scroller.ViewportWidth + 0.5, pageName);
                    Assert.IsTrue(page.ActualWidth <= content.ActualWidth + 0.5, pageName);

                    IEnumerable<FrameworkElement> widthOwners = FindDescendants<FrameworkElement>(page)
                        .Where(element => element != page
                            && element.IsDescendantOf(page)
                            && (element is Grid
                                or SettingsSection
                                or SettingsField
                                or SettingsOptionRow
                                or SettingsCheckBoxOption
                                or SettingsPathPicker
                                or SettingsListEditor
                                or TextBox
                                or ComboBox
                                or ListBox
                                or Slider));
                    foreach (FrameworkElement element in widthOwners)
                    {
                        Rect bounds = element.TransformToAncestor(page)
                            .TransformBounds(new Rect(new Point(), element.RenderSize));
                        Assert.IsTrue(bounds.Left >= -0.5 && bounds.Right <= page.ActualWidth + 0.5,
                            $"{pageName}.{element.GetType().Name} bounds {bounds} exceed page width {page.ActualWidth}.");
                    }
                }
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    /// <summary>実 path editor と browse ボタンへラベルを渡し、実 Audio banner の glyph と live 通知が入力内容を表示することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void RedesignedSettingsPages_UseAccessibleReusablePresentationContracts()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = CreateOwnerForPresentationWithoutNativeCapabilityQuery();
            using SettingsDialogViewModel ownedDialogLifetime = owner.SettingDialog;
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel,
                PlaylistWorkspace = owner.PlaylistWorkspace,
                Width = 820,
                Height = 760
            };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var pageHost = (ContentControl)window.FindName("settingsPageContent");

                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Name == "navigationGeneral");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var generalPage = (FrameworkElement)pageHost.Content;
                SettingsPathPicker pathPicker = FindDescendants<SettingsPathPicker>(generalPage)
                    .First(picker => !string.IsNullOrWhiteSpace(picker.Label));
                TextBox pathEditor = FindDescendants<TextBox>(pathPicker).Single();
                Button browseButton = FindDescendants<Button>(pathPicker)
                    .Single(button => Equals(button.Content, pathPicker.BrowseText));
                Assert.AreEqual(pathPicker.Label, AutomationProperties.GetName(pathEditor));
                Assert.AreEqual(pathPicker.BrowseText, AutomationProperties.GetName(browseButton));

                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Name == "navigationAudio");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var audioPage = (FrameworkElement)pageHost.Content;
                SettingsStatusBanner statusBanner = FindDescendants<SettingsStatusBanner>(audioPage).First();
                statusBanner.Icon = "!";
                statusBanner.Content = "Rendered status message";
                window.UpdateLayout();
                SettingsStatusIcon statusIcon = FindDescendants<SettingsStatusIcon>(statusBanner).Single();
                Assert.AreEqual(statusBanner.Icon, statusIcon.Text);
                Assert.AreEqual(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(statusBanner));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }


    /// <summary>実確定ボタンからの通常保存で、実Close・modal復帰と同じMainWindowからの再表示後も同じFolder Task・制限・親L/Pを保持し、Shell終了が実終端を待ちます。</summary>
    [TestMethod]
    public void MainWindow_NormalSettingsCloseEndsModalBeforeActualFileDiffTerminates()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string root = Path.Combine(Path.GetTempPath(), "BmsSavedSettings-" + Guid.NewGuid().ToString("N"));
            string added = Path.Combine(root, "added");
            Directory.CreateDirectory(added);
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new List<string>();
            var failures = new List<Exception>();
            var lifetime = new DangerApplicationLifetime(events, firstStartup: false);
            Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(value =>
            {
                value.OperationModeLR2DB = false;
                value.BMSRootPath = root; value.StandaloneBmsRootPaths = root; value.BMSInstallDir = root;
                value.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
                value.EnablePlaylistUrlCompletion = false; value.ScanBmsFilesOnStartup = false; value.SkipInitPlaylistLoad = true;
                value.UseBeatorajaScoreDb = false; value.EnableBeatorajaBmtOutput = false; value.UseExternalPanelImage = false;
                value.UsePlayeruBMplay = false; value.UsePlayerLR2body = false; value.UsePlayerBMIIDXView = false;
                value.IsLR2BackupEnabled = false;
            });
            var scanner = new SettingDialogEditCompletionTests.SettingsChartFileScanner(CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), []));
            var composition = new ApplicationComposition(settingsEditSession: new DangerSettingsEditSession(values, events),
                reportSettingsApplyFailure: failures.Add, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator());
            MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
            owner.StartupUpdateWorkflow.NotifyClosing();
            var ownership = new MainWindowTestLifetime(owner, lifetime.ShutdownRequested.Task);
            Window? previousMain = Application.Current.MainWindow;
            MainWindow? main = null;
            SettingsWindow? settings = null;
            Task? apply = null;
            int settingsPresentationCount = 0;
            bool closed = false;
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ExceptionDispatchInfo? failure = null;
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellActivationWorkflow.ActivateRenderedShell(() => { }, _ => { }, () => false), "normal-settings-shell-initialization");
                Assert.IsTrue(owner.IsInitializationCompleted);
                string chart = Path.Combine(added, "normal.bms");
                File.WriteAllText(chart, "#PLAYER 1\n#TITLE Modal Folder\n#BPM 120\n#00111:01\n");
                var next = CapturedChartFileScanner.FromFixture([chart], new Dictionary<string, IEnumerable<string>> { [added] = [] }, [root]);
                next.ScanObserved = () => { entered.TrySetResult(); release.Wait(); };
                scanner.Current = next;
                main = ownership.CreateWindow(() => new MainWindow(owner, window =>
                {
                    settings = window;
                    settingsPresentationCount++;
                    windowTest.PrepareForOwnedPresentation(window);
                    window.Closed += (_, _) => closed = true;
                    window.ContentRendered += async (_, _) =>
                    {
                        try
                        {
                            if (apply != null)
                            {
                                Assert.AreSame(apply, owner.SettingDialog.SettingsOperationCompletionTask);
                                Assert.IsFalse(apply.IsCompleted);
                                Assert.IsFalse(owner.SettingDialog.IsEditCompletionEnabled);
                                Button confirm = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:bms"));
                                Assert.IsFalse(confirm.IsEnabled);
                                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationRecording");
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                float quality = owner.SettingDialog.EncoderQuality == 0.5f ? 0.6f : 0.5f;
                                owner.SettingDialog.EncoderQuality = quality;
                                Assert.AreEqual(quality, values.EncoderQuality);
                                Assert.AreSame(apply, owner.SettingDialog.SettingsOperationCompletionTask);
                                window.Close();
                                return;
                            }
                            await owner.SettingDialog.AddBmsSearchRootPathsAsync([added]);
                            FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:bms"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            apply = owner.SettingDialog.SettingsOperationCompletionTask;
                            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, apply, "same-settings-scan");
                            Assert.IsFalse(closed);
                            Assert.IsTrue(window.IsVisible);
                            Assert.AreSame(owner.SettingDialog, window.DataContext);
                            Assert.IsFalse(apply.IsCompleted);
                            Assert.IsTrue(composition.OperationAdmission.IsActive);
                            Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                            bool ordinary = !owner.SettingDialog.ShowRecommUpdatedMsg;
                            owner.SettingDialog.ShowRecommUpdatedMsg = ordinary;
                            Assert.AreEqual(ordinary, values.ShowRecommUpdatedMsg);
                            window.Close();
                            // モーダルの復帰を先に観測し、実Folder Taskの入力gateをここでは解放しません。
                        }
                        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); window.CloseForOwnerShutdown(); }
                        finally { observed.TrySetResult(); }
                    };
                }));
                Application.Current.MainWindow = main;
                windowTest.PrepareForOwnedPresentation(main);
                main.Show(); main.UpdateLayout();
                if (owner.ProgressHub.IsStartupBackgroundInitializationActive)
                {
                    // 手動Applyのfixture準備として起動後続を回収し、設定受付の解放を別操作のL取得と混同しない。
                    var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    PropertyChangedEventHandler finished = (_, args) =>
                    {
                        if (args.PropertyName == nameof(OperationProgressHubViewModel.IsStartupBackgroundInitializationActive)
                            && !owner.ProgressHub.IsStartupBackgroundInitializationActive) { terminal.TrySetResult(); }
                    };
                    owner.ProgressHub.PropertyChanged += finished;
                    try
                    {
                        if (owner.ProgressHub.IsStartupBackgroundInitializationActive)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal.Task, "normal-settings-startup-followup-terminal-before-apply");
                        }
                    }
                    finally { owner.ProgressHub.PropertyChanged -= finished; }
                }
                Visibility previousOverlay = main.PlaybackOverlayVisibility;
                owner.SettingDialog.OpenCommand.Execute();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(observed.Task, "same-settings-operation-and-close");
                failure?.Throw();
                Assert.IsTrue(closed);
                Assert.IsNotNull(settings);
                Assert.IsNull(settings.DataContext);
                Assert.IsFalse(settings.IsVisible);
                Assert.AreEqual(previousOverlay, main.PlaybackOverlayVisibility);
                Assert.IsNotNull(apply);
                Assert.IsFalse(apply.IsCompleted, "通常Closeとmodal復帰は受理済み実Taskの終端ではありません。");
                Assert.IsTrue(composition.OperationAdmission.IsActive);
                Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                owner.SettingDialog.OpenCommand.Execute();
                failure?.Throw();
                Assert.AreEqual(2, settingsPresentationCount, "同じMainWindowが処理中にも新しい設定Windowを表示します。");
                Assert.IsNotNull(settings);
                Assert.IsNull(settings.DataContext);
                Assert.IsFalse(apply.IsCompleted);
                main.Close();
                Assert.IsFalse(lifetime.ShutdownRequested.Task.IsCompleted, "閉じた設定Windowへの参照を失ってもShellは同じTaskをjoinします。");
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "normal-settings-file-diff-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(lifetime.ShutdownRequested.Task, "normal-settings-shell-terminal");
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.IsFalse(composition.OperationAdmission.IsActive);
                Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
            }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally
            {
                release.Set();
                try
                {
                    if (settings?.IsVisible == true) { settings.CloseForOwnerShutdown(); }
                    try { if (apply != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "normal-settings-accepted-cleanup"); } }
                    finally { ownership.DisposeAfterBodyFailure(failure?.SourceException); }
                }
                finally { Application.Current.MainWindow = previousMain; Directory.Delete(root, recursive: true); }
            }
            failure?.Throw();
        });
    }

    /// <summary>設定を二回開くと標準 coordinator が同じ owner／設定を fresh modal に接続し、閉じるたび main の再生 overlay を元に戻すことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void MainWindow_PresentsFreshOwnedModalThroughCoordinator()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var startupEvents = new List<string>();
            var startupLifetime = new DangerApplicationLifetime(startupEvents, firstStartup: true);
            Settings startupSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            var startupSettingsSession = new DangerSettingsEditSession(startupSettings, startupEvents);
            var composition = new ApplicationComposition(
                settingsEditSession: startupSettingsSession,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: startupLifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, startupLifetime.ShutdownRequested.Task);
            MainWindow? owner = null;
            var settingsWindows = new List<SettingsWindow>();
            Exception? interactionFailure = null;
            bool settingsPresented = false;
            ExceptionDispatchInfo? bodyFailure = null;
            Exception? cleanupFailure = null;

            void QueueSettingsPresentationAndClose()
            {
                owner!.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    (Action)(() =>
                    {
                        SettingsWindow? settingsWindow = settingsWindows.LastOrDefault();
                        try
                        {
                            Assert.IsNotNull(settingsWindow, "The settings window factory did not create a window before the modal dispatcher turn.");
                            Assert.IsTrue(settingsWindow!.IsVisible);
                            Assert.AreSame(owner, settingsWindow!.Owner);
                            Assert.AreSame(viewModel.SettingDialog, settingsWindow!.DataContext);
                            Assert.AreSame(viewModel.PlaybackPanel, settingsWindow!.PlaybackPanel);
                            Assert.AreSame(viewModel.PlaylistWorkspace, settingsWindow!.PlaylistWorkspace);
                            Assert.AreEqual(Visibility.Visible, owner!.PlaybackOverlayVisibility);
                            settingsPresented = true;
                        }
                        catch (Exception exception)
                        {
                            interactionFailure = exception;
                        }
                        finally
                        {
                            if (settingsWindow?.IsVisible == true)
                            {
                                settingsWindow.CloseFromPresentation();
                            }
                        }
                    }));
            }

            try
            {
                owner = ownership.CreateWindow(() => new MainWindow(
                    viewModel,
                    settingsWindow =>
                    {
                        settingsWindows.Add(settingsWindow);
                        windowTest.PrepareForOwnedPresentation(settingsWindow);
                    }));
                // main は保存位置を SourceInitialized で復元する。modal の owner と実終了経路を寿命所有者で回収する。
                windowTest.PrepareForOwnedPresentation(owner!);
                owner!.Show();
                owner!.UpdateLayout();
                Visibility previousOverlayVisibility = owner!.PlaybackOverlayVisibility;

                QueueSettingsPresentationAndClose();
                viewModel.SettingDialog.OpenCommand.Execute();
                if (interactionFailure != null)
                {
                    throw new AssertFailedException("The first composed settings modal interaction failed.", interactionFailure);
                }
                Assert.IsTrue(settingsPresented);
                Assert.AreEqual(previousOverlayVisibility, owner!.PlaybackOverlayVisibility);

                settingsPresented = false;
                QueueSettingsPresentationAndClose();
                viewModel.SettingDialog.OpenCommand.Execute();
                if (interactionFailure != null)
                {
                    throw new AssertFailedException("The second composed settings modal interaction failed.", interactionFailure);
                }
                Assert.IsTrue(settingsPresented);
                Assert.AreEqual(previousOverlayVisibility, owner!.PlaybackOverlayVisibility);
                Assert.AreEqual(2, settingsWindows.Count);
                Assert.AreNotSame(settingsWindows[0], settingsWindows[1]);
                Assert.IsTrue(settingsWindows.All(window => window.CloseReason == SettingsWindowCloseReason.Presentation));
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                try
                {
                    foreach (SettingsWindow settingsWindow in settingsWindows.Where(window => window.IsVisible))
                    {
                        settingsWindow.CloseFromPresentation();
                    }

                    if (owner != null)
                    {
                        ownership?.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
                finally
                {
                    try
                    {
                        ownership?.DisposeAfterBodyFailure(bodyFailure?.SourceException ?? cleanupFailure);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
            }

            if (bodyFailure != null)
            {
                if (cleanupFailure != null)
                {
                    bodyFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] = cleanupFailure.ToString();
                }

                bodyFailure.Throw();
            }

            if (cleanupFailure != null)
            {
                windowTest.RegisterCleanupFailureForTesting(cleanupFailure);
            }
        });
    }

    /// <summary>設定 Window 生成 hook が失敗しても元例外と overlay を保持し、次の明示要求を別 modal で表示・終了できることを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void MainWindow_SettingsPresentationFailureRestoresPlaybackOverlayVisibility()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var startupEvents = new List<string>();
            var startupLifetime = new DangerApplicationLifetime(startupEvents, firstStartup: true);
            var startupSettingsSession = new DangerSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings(), startupEvents);
            var composition = new ApplicationComposition(
                settingsEditSession: startupSettingsSession,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: startupLifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, startupLifetime.ShutdownRequested.Task);
            MainWindow? owner = null;
            SettingsWindow? createdSettingsWindow = null;
            SettingsWindow? presentedSettingsWindow = null;
            Exception? interactionFailure = null;
            bool secondPresentationObserved = false;
            int settingsWindowCreationCount = 0;
            var sentinel = new InvalidOperationException("settings presentation sentinel");
            ExceptionDispatchInfo? bodyFailure = null;
            Exception? cleanupFailure = null;

            try
            {
                owner = ownership.CreateWindow(() => new MainWindow(
                    viewModel,
                    settingsWindow =>
                    {
                        settingsWindowCreationCount++;
                        if (settingsWindowCreationCount == 1)
                        {
                            createdSettingsWindow = settingsWindow;
                            throw sentinel;
                        }

                        presentedSettingsWindow = settingsWindow;
                        windowTest.PrepareForOwnedPresentation(settingsWindow);
                        owner!.Dispatcher.BeginInvoke(
                            DispatcherPriority.Loaded,
                            (Action)(() =>
                            {
                                try
                                {
                                    Assert.IsTrue(settingsWindow.IsVisible);
                                    Assert.AreSame(owner, settingsWindow.Owner);
                                    Assert.AreSame(viewModel.SettingDialog, settingsWindow.DataContext);
                                    Assert.AreEqual(Visibility.Visible, owner!.PlaybackOverlayVisibility);
                                    secondPresentationObserved = true;
                                }
                                catch (Exception exception)
                                {
                                    interactionFailure = exception;
                                }
                                finally
                                {
                                    if (settingsWindow.IsVisible)
                                    {
                                        settingsWindow.CloseFromPresentation();
                                    }
                                }
                            }));
                    }));
                windowTest.PrepareForOwnedPresentation(owner!);
                owner!.Show();
                owner!.UpdateLayout();
                Visibility previousOverlayVisibility = owner!.PlaybackOverlayVisibility;

                InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                    () => viewModel.SettingDialog.OpenCommand.Execute());

                StringAssert.Contains(failure.Message, "Settings window failed: Failed");
                Assert.AreSame(sentinel, failure.InnerException);
                Assert.AreEqual(previousOverlayVisibility, owner!.PlaybackOverlayVisibility);
                Assert.IsNotNull(createdSettingsWindow);
                Assert.IsFalse(createdSettingsWindow!.IsVisible);

                viewModel.SettingDialog.OpenCommand.Execute();
                if (interactionFailure != null)
                {
                    throw new AssertFailedException("The settings modal did not recover after its first factory failure.", interactionFailure);
                }

                Assert.IsTrue(secondPresentationObserved);
                Assert.IsNotNull(presentedSettingsWindow);
                Assert.AreNotSame(createdSettingsWindow, presentedSettingsWindow);
                Assert.AreEqual(SettingsWindowCloseReason.Presentation, presentedSettingsWindow!.CloseReason);
                Assert.IsNull(presentedSettingsWindow!.DataContext);
                Assert.AreEqual(previousOverlayVisibility, owner!.PlaybackOverlayVisibility);
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                try
                {
                    if (createdSettingsWindow?.IsVisible == true)
                    {
                        createdSettingsWindow.CloseFromPresentation();
                    }

                    if (owner != null)
                    {
                        ownership?.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
                finally
                {
                    try
                    {
                        ownership?.DisposeAfterBodyFailure(bodyFailure?.SourceException ?? cleanupFailure);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
            }

            if (bodyFailure != null)
            {
                if (cleanupFailure != null)
                {
                    bodyFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] = cleanupFailure.ToString();
                }

                bodyFailure.Throw();
            }

            if (cleanupFailure != null)
            {
                windowTest.RegisterCleanupFailureForTesting(cleanupFailure);
            }
        });
    }

    /// <summary>取消可能な設定 Window を native Close すると、取消の完了を presentation へ一回要求することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void NativeClose_WhenCancellationIsEnabled_RequestsCancelCompletion()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownedDialogLifetime = viewModel.SettingDialog;
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };

            window.Close();
            window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));

            Assert.AreEqual(1, presentation.CloseRequestCount);
        });
    }

    /// <summary>通常Close/Escは未確定BMT候補を破棄して保存済み項目を保ち、実Windowの閉鎖・再表示でも同じ未完了Taskと操作制限を維持します。実MainWindow接続はFileDiff系列が分担します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void RunViewOperation_NormalCloseAndEscapePreserveTaskAcrossReopen(bool escape)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(values);
            using SettingsDialogViewModel ownedDialogLifetime = viewModel.SettingDialog;
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };
            SettingsWindow? reopened = null;
            presentation.CloseAction = window.CloseFromPresentation;
            windowTest.ShowAndWaitForContentRendered(window);
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            var operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? operation = null;
            try
            {
                bool confirmedBmtOutput = values.EnableBeatorajaBmtOutput;
                viewModel.SettingDialog.EnableBeatorajaBmtOutput = !confirmedBmtOutput;
                Assert.IsTrue(viewModel.SettingDialog.CanCancelBmtSettings);
                float savedQuality = viewModel.SettingDialog.EncoderQuality == 0.5f ? 0.6f : 0.5f;
                viewModel.SettingDialog.EncoderQuality = savedQuality;
                operation = window.RunViewOperationAsync(() => operationCompletion.Task);

                Task terminal = viewModel.SettingDialog.SettingsOperationCompletionTask;
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                if (escape)
                {
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                }
                else { window.Close(); }
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "settings-normal-close");
                Assert.AreEqual(1, presentation.CloseRequestCount);
                Assert.IsNull(window.DataContext);
                Assert.AreEqual(confirmedBmtOutput, viewModel.SettingDialog.EnableBeatorajaBmtOutput, "実閉鎖では未確定のBMT候補を最新確定値へ戻します。");
                Assert.AreEqual(confirmedBmtOutput, values.EnableBeatorajaBmtOutput);
                Assert.AreEqual(savedQuality, values.EncoderQuality, "通常閉鎖で保存済みの通常項目を戻しません。");
                Assert.IsFalse(operation.IsCompleted);
                reopened = new SettingsWindow { DataContext = viewModel.SettingDialog };
                presentation.CloseAction = reopened.CloseFromPresentation;
                windowTest.ShowAndWaitForContentRendered(reopened);
                Assert.AreSame(terminal, viewModel.SettingDialog.SettingsOperationCompletionTask);
                Assert.IsFalse(terminal.IsCompleted);
                Assert.AreEqual(confirmedBmtOutput, viewModel.SettingDialog.EnableBeatorajaBmtOutput);
                Assert.AreEqual(savedQuality, viewModel.SettingDialog.EncoderQuality);
                Assert.IsFalse(viewModel.SettingDialog.IsEditCompletionEnabled);
                ((ListBox)reopened.FindName("settingsNavigation")).SelectedItem = reopened.FindName("navigationRecording");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                float quality = viewModel.SettingDialog.EncoderQuality == 0.5f ? 0.6f : 0.5f;
                viewModel.SettingDialog.EncoderQuality = quality;
                Assert.AreEqual(quality, values.EncoderQuality);

                operationCompletion.SetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "settings-view-operation-terminal");
                Assert.IsTrue(operation.IsCompletedSuccessfully);

                int started = 0;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(reopened.RunViewOperationAsync(() => { started++; return Task.CompletedTask; }), "settings-next-explicit-operation");
                Assert.AreEqual(1, started);
            }
            finally
            {
                operationCompletion.TrySetResult();
                try
                {
                    if (operation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "settings-view-operation-finally"); }
                }
                finally
                {
                    if (window.IsVisible) { window.CloseForOwnerShutdown(); }
                    if (reopened?.IsVisible == true) { reopened.CloseForOwnerShutdown(); }
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
        });
    }

    /// <summary>BMTの実P受付を保持するcompiledボタン操作中も独立した画像確定と通常Closeは可能で、ownerのjoinだけはBMTの実終端を待ちます。</summary>
    /// <remarks>全表の実ファイル投影はSettingDialogEditCompletionTestsが分担し、このケースはP内の外部処理だけをgateで固定して実Windowの配送・別カテゴリ保存・寿命を保証します。</remarks>
    [TestMethod]
    public void BmtButtonWhileOutputIsPending_AllowsIndependentImageConfirmationAndPreservesOwnerJoin()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = Path.Combine(Path.GetTempPath(), "SettingsIndependent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string image = Path.Combine(root, "panel.png");
            File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jCZYAAAAASUVORK5CYII="));
            string configPath = Path.Combine(root, "user.config");
            Settings values = PortableSettingsPersistenceTests.OpenSettings(configPath);
            values.OperationModeLR2DB = false;
            values.EnableBeatorajaBmtOutput = false;
            values.UseExternalPanelImage = false;
            values.StagefilePath = image;
            values.Save();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
            owner.SettingDialog.Dispose();
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new DangerWorkspacePort { ExportOperation = () => { entered.TrySetResult(); return release.Task; } };
            DangerDialogContext context = CreateDangerDialog(owner, new DangerSettingsEditSession(values, []), composition,
                new DangerDialogService(), new DangerSchemaDialogPort(), new DangerApplicationDataStore(), workspace: workspace);
            using SettingsDialogViewModel settings = context.Settings;
            var window = new SettingsWindow { DataContext = settings };
            settings.AttachPresentationPort(new RecordingPresentationPort { CloseAction = window.CloseFromPresentation });
            Task? join = null;
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                var general = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                FindDescendants<Button>(general).Single(button => Equals(button.Tag, "confirm:bmt"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(entered.Task, "BMT-section-accepted-P");
                join = settings.SettingsOperationCompletionTask;
                Assert.IsFalse(join.IsCompleted);
                Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                Assert.IsTrue(settings.IsAudioOutputSelectionEnabled);
                Assert.IsFalse(settings.IsEditCompletionEnabled);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAppearance");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var appearance = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                FindDescendants<CheckBox>(appearance).Single(check => check.Name == "checkBoxUseExternalPanelImage")
                    .SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Assert.IsTrue(values.UseExternalPanelImage);
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(configPath);
                Assert.IsTrue(fresh.UseExternalPanelImage, "独立した局所確定はP待機中も実providerへ保存します。");
                window.Close();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsNull(window.DataContext);
                Assert.IsFalse(join.IsCompleted, "表示の失効は受理済みTaskの終端ではありません。");
            }
            finally
            {
                release.TrySetResult();
                if (join != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(join, "BMT-owner-join"); }
                if (window.IsVisible) { window.CloseForOwnerShutdown(); }
                Directory.Delete(root, recursive: true);
            }
            Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
        });
    }

    /// <summary>BMTの失敗を通知し、通常閉鎖・再表示で自動実行せず、次の明示更新と既存の復元確認へ進みます。</summary>
    /// <remarks>実providerとcompiledボタン・通常閉鎖/再表示を接続します。Scoreの実モデル反映とBMTの実ファイル投影はEditCompletion系列が分担し、ここでは実処理portの失敗・成功を固定します。</remarks>
    [TestMethod]
    public void BmtProcessingFailure_AllowsExplicitCurrentUpdateAndRestoreAfterReopen()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = Path.Combine(Path.GetTempPath(), "SettingsSectionRetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string config = Path.Combine(root, "user.config");
            Settings values = PortableSettingsPersistenceTests.OpenSettings(config);
            values.OperationModeLR2DB = false;
            values.BMSRootPath = root;
            values.StandaloneBmsRootPaths = root;
            values.BMSInstallDir = root;
            values.UseBeatorajaScoreDb = true;
            values.EnableBeatorajaBmtOutput = false;
            values.KeepBeatorajaBmtFilesWhenOutputDisabled = true;
            values.Save();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
            owner.SettingDialog.Dispose();
            bool fail = true;
            int operations = 0;
            Task<StartupInitializationResult> Reload(StartupProgressOperationKind kind)
            {
                operations++;
                return Task.FromResult(new StartupInitializationResult(fail ? StartupInitializationOutcome.OperationFailed : StartupInitializationOutcome.Succeeded,
                    Failure: fail ? new IOException("section processing failed") : null, OperationKind: kind));
            }
            var statePort = new DangerStatePort(() => Reload(StartupProgressOperationKind.ScoreOnly))
            { ReloadFileDiff = () => Reload(StartupProgressOperationKind.ReloadFileDiff) };
            var workspace = new DangerWorkspacePort
            { ExportOperation = () => { operations++; return fail ? Task.FromException(new IOException("BMT processing failed")) : Task.CompletedTask; } };
            var failures = new List<Exception>();
            var restoreDialogs = new RecordingSettingsRouteDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            };
            DangerDialogContext context = CreateDangerDialog(owner, new DangerSettingsEditSession(values, []),
                MainWindowViewModelTestFactory.GetComposition(owner), new DangerDialogService(), new DangerSchemaDialogPort(), new DangerApplicationDataStore(),
                workspace: workspace, statePort: statePort, reportFailure: failures.Add);
            using SettingsDialogViewModel settings = context.Settings;
            SettingsWindow? window = null;
            void ClickConfirm()
            {
                var page = (FrameworkElement)((ContentControl)window!.FindName("settingsPageContent")).Content;
                FindDescendants<Button>(page).Single(button => Equals(button.Tag, "confirm:bmt"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AwaitViewOperationTerminal(settings);
            }
            try
            {
                window = new SettingsWindow(restoreDialogs) { DataContext = settings, PlaylistWorkspace = owner.PlaylistWorkspace };
                scope.ShowAndWaitForContentRendered(window);
                Assert.IsFalse(FindDescendants<Button>(window).Single(button => Equals(button.Tag, "cancel:bmt")).IsEnabled);
                settings.KeepBeatorajaBmtFilesWhenOutputDisabled = false;
                ClickConfirm();
                Assert.AreEqual(1, failures.Count);
                Assert.IsNotNull(window.PlaylistWorkspace.PlaylistTreeTables);
                byte[] confirmedBeforeRestore = File.ReadAllBytes(config);
                int savesBeforeRestore = context.SettingsSession.SaveCount;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.HandlePlaylistRestoreAsync(), "restore after BMT failure");
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(1, restoreDialogs.ConfirmationRequests.Count, "過去BMT失敗だけでは復元確認を拒否しません。");
                Assert.AreEqual(0, restoreDialogs.FileRequests.Count, "既存の復元確認の取消ではpickerを開きません。");
                Assert.AreEqual(0, restoreDialogs.MessageRequests.Count, "終了通知へ進みません。");
                Assert.AreEqual(savesBeforeRestore, context.SettingsSession.SaveCount);
                CollectionAssert.AreEqual(confirmedBeforeRestore, File.ReadAllBytes(config));
                Assert.AreEqual(1, operations, "復元確認の取消でBMTを再実行しません。");
                Assert.AreEqual(1, failures.Count, "復元確認の取消で保存・処理の新しい失敗を作りません。");
                Assert.IsTrue(settings.IsBeatorajaSelectionEnabled);
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(config);
                Assert.IsFalse(fresh.KeepBeatorajaBmtFilesWhenOutputDisabled);
                window.Close();
                window = new SettingsWindow(restoreDialogs) { DataContext = settings, PlaylistWorkspace = owner.PlaylistWorkspace };
                scope.ShowAndWaitForContentRendered(window);
                Assert.IsFalse(FindDescendants<Button>(window).Single(button => Equals(button.Tag, "cancel:bmt")).IsEnabled);
                Assert.IsTrue(FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:bmt")).IsEnabled, "差分なしでも現在設定から全表を明示更新できます。");
                fail = false;
                Assert.AreEqual(1, operations, "再表示は自動更新しません。");
                ClickConfirm();
                Assert.AreEqual(2, operations);
                Assert.AreEqual(1, failures.Count, "次の明示更新が成功し、新しい失敗を通知しません。");
                Assert.IsTrue(settings.IsEditCancellationEnabled);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.HandlePlaylistRestoreAsync(), "restore after explicit current update");
                Assert.AreEqual(2, restoreDialogs.ConfirmationRequests.Count, "明示更新後も通常の復元確認へ進めます。");
                Assert.AreEqual(0, restoreDialogs.FileRequests.Count, "確認取消なので実復元へ進みません。");
                Assert.AreEqual(2, operations);
                Assert.IsTrue(context.SettingsSession.SaveCount >= savesBeforeRestore);
                Assert.IsTrue(window.IsVisible);
            }
            finally
            {
                if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>score失敗後も復元確認へ進み、通常閉鎖・再表示で自動実行せず、次の有効な実入力を保存・読込みできます。</summary>
    /// <remarks>実providerとcompiled CheckBoxの接続を確認します。実SQLite失敗とモデル投影はEditCompletion/Startup系列が担います。</remarks>
    [TestMethod]
    public void ScoreProcessingFailure_AllowsNextSelectionAndRestoreAfterReopen()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = Path.Combine(Path.GetTempPath(), "SettingsScoreNext-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string config = Path.Combine(root, "user.config");
            Settings values = PortableSettingsPersistenceTests.OpenSettings(config);
            string beatoraja = Directory.CreateDirectory(Path.Combine(root, "beatoraja")).FullName;
            File.WriteAllText(Path.Combine(beatoraja, "beatoraja.jar"), string.Empty);
            File.WriteAllText(Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName), "{\"playername\":\"player\"}");
            string scorePath = Path.Combine(Directory.CreateDirectory(Path.Combine(beatoraja, "player", "player")).FullName, "score.db");
            File.WriteAllBytes(scorePath, []);
            values.OperationModeLR2DB = false;
            values.BMSRootPath = root;
            values.StandaloneBmsRootPaths = root;
            values.BMSInstallDir = root;
            values.BeatorajaRootPath = beatoraja;
            values.BeatorajaPlayerId = "player";
            values.BeatorajaScoreDbPath = scorePath;
            values.UseBeatorajaScoreDb = true;
            values.Save();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
            owner.SettingDialog.Dispose();
            bool fail = true;
            int operations = 0;
            var statePort = new DangerStatePort(() =>
            {
                operations++;
                return Task.FromResult(new StartupInitializationResult(fail ? StartupInitializationOutcome.OperationFailed : StartupInitializationOutcome.Succeeded,
                    Failure: fail ? new IOException("score processing failed") : null, OperationKind: StartupProgressOperationKind.ScoreOnly));
            });
            var failures = new List<Exception>();
            var restoreDialogs = new RecordingSettingsRouteDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel) };
            DangerDialogContext context = CreateDangerDialog(owner, new DangerSettingsEditSession(values, []),
                MainWindowViewModelTestFactory.GetComposition(owner), new DangerDialogService(), new DangerSchemaDialogPort(), new DangerApplicationDataStore(),
                statePort: statePort, reportFailure: failures.Add);
            using SettingsDialogViewModel settings = context.Settings;
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow(restoreDialogs) { DataContext = settings, PlaylistWorkspace = owner.PlaylistWorkspace };
                scope.ShowAndWaitForContentRendered(window);
                FindDescendants<CheckBox>(window).Single(check => check.Name == "useBeatorajaScoreDbCheckBox")
                    .SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                AwaitViewOperationTerminal(settings);
                Assert.AreEqual(1, failures.Count);
                Assert.AreEqual(1, operations);
                Assert.IsFalse(PortableSettingsPersistenceTests.OpenSettings(config).UseBeatorajaScoreDb);
                Assert.IsTrue(settings.IsScoreSelectionEnabled);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.HandlePlaylistRestoreAsync(), "restore after score failure");
                Assert.AreEqual(1, restoreDialogs.ConfirmationRequests.Count);
                Assert.AreEqual(0, restoreDialogs.FileRequests.Count);
                Assert.AreEqual(1, operations);
                window.Close();
                window = new SettingsWindow(restoreDialogs) { DataContext = settings, PlaylistWorkspace = owner.PlaylistWorkspace };
                scope.ShowAndWaitForContentRendered(window);
                Assert.AreEqual(1, operations, "再表示だけでは処理しません。");
                CheckBox checkbox = FindDescendants<CheckBox>(window).Single(check => check.Name == "useBeatorajaScoreDbCheckBox");
                Assert.IsTrue(checkbox.IsEnabled);
                fail = false;
                checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                AwaitViewOperationTerminal(settings);
                Assert.AreEqual(2, operations);
                Assert.AreEqual(1, failures.Count);
                Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(config).UseBeatorajaScoreDb);
            }
            finally
            {
                if (window?.IsVisible == true) { window.CloseForOwnerShutdown(); }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static Grid CreateSettingsControlHost()
    {
        var host = new Grid();
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/BeMusicSeeker;component/Themes/Light.xaml", UriKind.RelativeOrAbsolute)
        });
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/BeMusicSeeker;component/Views/Settings/SettingsControls.xaml", UriKind.RelativeOrAbsolute)
        });
        return host;
    }

    private static void EnsureCanonicalApplicationResources()
    {
        Application application = Application.Current;
        Assert.IsNotNull(application);
        AddCanonicalResourceIfMissing(
            application,
            "/BeMusicSeeker;component/Themes/CanonicalDialogStyles.xaml");
    }

    private static void AddCanonicalResourceIfMissing(Application application, string source)
    {
        if (application.Resources.MergedDictionaries.Any(dictionary =>
            string.Equals(dictionary.Source?.OriginalString, source, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute)
        });
    }


    private static Rect GetVisualBounds(Visual ancestor, FrameworkElement descendant)
        => descendant.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(0d, 0d, descendant.ActualWidth, descendant.ActualHeight));

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is T match)
            {
                yield return match;
            }
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }
    }

    private static Color GetApplicationBrushColor(string resourceKey)
    {
        return ((SolidColorBrush)Application.Current.FindResource(resourceKey)).Color;
    }

    private static void AssertBrushColor(FrameworkElement resourceOwner, string resourceKey, Brush actual)
    {
        var expected = (SolidColorBrush)resourceOwner.TryFindResource(resourceKey);
        Assert.IsInstanceOfType<SolidColorBrush>(actual, resourceKey);
        Assert.AreEqual(expected.Color, ((SolidColorBrush)actual).Color, resourceKey);
    }

    /// <summary>表示だけを検証する6ケースで同じ専用draftを接続し、Audioページの自動照会には局所結果を与えます。</summary>
    /// <remarks>自動照会のnative呼出しは表示保証の対象外で、受付・取消・解放は音声workflowの既存ケースが分担します。元childは表示前に破棄し、置換childの寿命は各呼出元のfinally/usingが所有します。</remarks>
    private static MainWindowViewModel CreateOwnerForPresentationWithoutNativeCapabilityQuery()
    {
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
        owner.SettingDialog.Dispose();
        ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
        var workflow = new AudioDeviceTestWorkflowOwner(
            new NoOpAudioDeviceTestPlaybackPort(),
            new SuccessfulAudioDeviceTestRuntime(),
            new DelegateAudioDeviceCapabilityRuntime(request => new AudioDeviceCapabilityResult(
                request.Backend,
                request.DeviceIdentity,
                request.DeviceName,
                AudioDeviceCapabilityStatus.Available,
                [SampleRate.SAMPLE_RATE_44100Hz])));
        var dialog = new SettingsDialogViewModel(
            owner,
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            owner.LibraryFolderTree,
            composition,
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            new NoOpSettingsEditSession(values),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: composition.ApplicationPathSnapshot,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new SettingsAudioGateway(() => values),
            audioDeviceTestWorkflow: workflow,
            operationAdmission: composition.OperationAdmission);
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SettingDialog))!
            .SetValue(owner, dialog);
        return owner;
    }

    private static SettingsDialogViewModel CreateManualResyncSettingsDialog(
        MainWindowViewModel owner,
        RecordingManualResyncRuntime runtime, Settings? values = null)
    {
        var settingsSession = new ManualResyncSettingsEditSession(
            values ?? MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
            }));
        var composition = new ApplicationComposition(
            settingsEditSession: settingsSession,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var workflow = new Lr2SongDbSyncWorkflowOwner(runtime);
        return new SettingsDialogViewModel(
            new ManualResyncStatePort(),
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            owner.LibraryFolderTree,
            composition,
            owner.PlaybackPanel,
            workflow,
            settingsSession,
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
    }

    private sealed class ManualResyncStatePort : ISettingsDialogStatePort
    {
        public bool HasActiveLibraryProfile => true;

        public bool IsLibraryOperationInProgress => false;


        public Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
            => Task.FromResult(result with { CompletionPublished = result.Outcome == StartupInitializationOutcome.Succeeded });


        public Task<LeapYearFolderRepairApproval?> PrepareLibraryInitializationAsync() => Task.FromResult<LeapYearFolderRepairApproval?>(null);

        public Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification? notification) => Task.CompletedTask;

        public Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null,
            LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public Task PresentLibraryDirectoryWarningAsync(BeMusicSeeker.Models.BmsLibraryInternal.LibraryDirectoryPreflightException failure) => Task.CompletedTask;

        public Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public event EventHandler LibraryOperationAvailabilityChanged
        {
            add { }
            remove { }
        }

        public event Action<Lr2PlayHistorySchemaStatusSnapshot> Lr2PlayHistorySchemaStatusChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class ManualResyncSettingsEditSession : ISettingsEditSession
    {
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
        {
            environment.Commit(Values);
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        internal ManualResyncSettingsEditSession(Settings values)
        {
            Values = values;
        }

        public Settings Values { get; }

        public void Reload()
        {
        }

        public void Save()
        {
        }
    }

    private sealed class RecordingManualResyncRuntime : ILr2SongDbSyncWorkflowRuntime
    {
        internal bool Admitted { get; set; } = true;
        internal Task AdmissionWait { get; set; } = Task.CompletedTask;
        internal Task TerminalWait { get; set; } = Task.CompletedTask;
        internal TaskCompletionSource AdmissionReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object gate = new();

        private readonly List<QueueCall> queueCalls = [];

        internal TaskCompletionSource<QueueCall> QueueAccepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool ILr2SongDbSyncWorkflowRuntime.IsLr2ModeEnabled => true;

        bool ILr2SongDbSyncWorkflowRuntime.IsLibraryAvailable => true;

        internal int QueueCount
        {
            get
            {
                lock (gate)
                {
                    return queueCalls.Count;
                }
            }
        }

        public async Task<bool> QueueAsync(
            string reason,
            bool force,
            bool prepareGeneratedData = false,
            bool allowIncompleteToQueue = true,
            LibraryFileInitializationResult? initializationResult = null, LibraryFileMutationCapability? capability = null, bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null, Lr2SongDbSyncPreparedDataSurface? preparedSurface = null, OperationProgressRequest? originatingRequest = null, BmsLibraryOptionsSnapshot? optionsSnapshot = null, Action<bool>? admissionResult = null)
        {
            QueueCall call = new(reason, force, allowIncompleteToQueue);
            lock (gate)
            {
                queueCalls.Add(call);
            }

            QueueAccepted.TrySetResult(call);
            await AdmissionWait;
            admissionResult?.Invoke(Admitted);
            AdmissionReported.TrySetResult();
            if (Admitted) { await TerminalWait; }
            return Admitted;
        }



        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason, LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
        {
            return Task.CompletedTask;
        }

        public bool Cancel(string reason) => true;

        internal sealed class QueueCall
        {
            internal QueueCall(string reason, bool force, bool allowIncompleteToQueue)
            {
                Reason = reason;
                Force = force;
                AllowIncompleteToQueue = allowIncompleteToQueue;
            }

            internal string Reason { get; }

            internal bool Force { get; }

            internal bool AllowIncompleteToQueue { get; }
        }
    }

    private sealed class DangerDialogContext
    {
        internal DangerDialogContext(
            SettingsDialogViewModel settings,
            DangerSettingsEditSession settingsSession,
            DangerPlayHistoryPort playHistory,
            Func<int> reloadCount)
        {
            Settings = settings;
            SettingsSession = settingsSession;
            PlayHistory = playHistory;
            ReloadCount = reloadCount;
        }

        internal SettingsDialogViewModel Settings { get; }

        internal DangerSettingsEditSession SettingsSession { get; }

        internal DangerPlayHistoryPort PlayHistory { get; }

        internal Func<int> ReloadCount { get; }
    }

    private sealed class StatusBannerBindingSource : INotifyPropertyChanged
    {
        private string? message;

        public string? Message
        {
            get => message;
            set
            {
                if (string.Equals(message, value, StringComparison.Ordinal))
                {
                    return;
                }

                message = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class SchemaPresentationStatePort : ISettingsDialogStatePort
    {
        internal SettingsDialogViewModel LastSettingsDialog { get; set; } = null!;

        public bool HasActiveLibraryProfile => false;

        public bool IsLibraryOperationInProgress => false;


        public Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
            => Task.FromResult(result with { CompletionPublished = result.Outcome == StartupInitializationOutcome.Succeeded });


        public Task<LeapYearFolderRepairApproval?> PrepareLibraryInitializationAsync() => Task.FromResult<LeapYearFolderRepairApproval?>(null);

        public Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification? notification) => Task.CompletedTask;

        public Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null,
            LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public Task PresentLibraryDirectoryWarningAsync(BeMusicSeeker.Models.BmsLibraryInternal.LibraryDirectoryPreflightException failure) => Task.CompletedTask;

        public Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public event EventHandler LibraryOperationAvailabilityChanged
        {
            add { }
            remove { }
        }

        public event Action<Lr2PlayHistorySchemaStatusSnapshot>? Lr2PlayHistorySchemaStatusChanged;

        internal void NotifyLr2PlayHistorySchemaStatusChanged(Lr2PlayHistorySchemaStatusSnapshot snapshot)
            => Lr2PlayHistorySchemaStatusChanged?.Invoke(snapshot);
    }

    private sealed class DangerStatePort : ISettingsDialogStatePort
    {
        private readonly Func<Task> reloadScoresOnly;
        private bool isLibraryOperationInProgress;
        internal Func<Task<StartupInitializationResult>>? ReloadFileDiff { get; init; }

        internal DangerStatePort(Func<Task> reloadScoresOnly)
        {
            this.reloadScoresOnly = reloadScoresOnly ?? throw new ArgumentNullException(nameof(reloadScoresOnly));
        }

        public bool HasActiveLibraryProfile { get; init; } = true;

        public bool IsLibraryOperationInProgress => isLibraryOperationInProgress;

        internal void SetLibraryOperationInProgress(bool value)
        {
            if (isLibraryOperationInProgress == value) { return; }
            isLibraryOperationInProgress = value;
            LibraryOperationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }


        public Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
            => Task.FromResult(result with { CompletionPublished = result.Outcome == StartupInitializationOutcome.Succeeded });


        public Task<LeapYearFolderRepairApproval?> PrepareLibraryInitializationAsync() => Task.FromResult<LeapYearFolderRepairApproval?>(null);

        public Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification? notification) => Task.CompletedTask;

        public Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null,
            LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public async Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability)
        {
            Task operation = reloadScoresOnly();
            await operation;
            return operation is Task<StartupInitializationResult> result ? await result : new(StartupInitializationOutcome.Succeeded);
        }

        public Task PresentLibraryDirectoryWarningAsync(BeMusicSeeker.Models.BmsLibraryInternal.LibraryDirectoryPreflightException failure) => Task.CompletedTask;

        public Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null)
            => ReloadFileDiff?.Invoke() ?? Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public event EventHandler? LibraryOperationAvailabilityChanged;

        public event Action<Lr2PlayHistorySchemaStatusSnapshot> Lr2PlayHistorySchemaStatusChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class DangerApplicationLifetime : IApplicationLifetimePort
    {
        private readonly List<string> events;

        private bool firstStartup;

        internal DangerApplicationLifetime(List<string> events, bool firstStartup = false)
        {
            this.events = events;
            this.firstStartup = firstStartup;
        }

        public bool IsFirstStartup => firstStartup;

        internal int CoordinatedShutdownCount { get; private set; }

        internal int ShutdownRequestCount { get; private set; }

        /// <summary>既存 lifetime request の実完了を Window cleanup へ通知する。</summary>
        internal TaskCompletionSource ShutdownRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteFirstStartup()
        {
            firstStartup = false;
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
            CoordinatedShutdownCount++;
            events.Add("shutdown-mark:" + reason);
        }

        public void ExitForUnrestoredDestination(System.Exception failure)
        => throw new System.InvalidOperationException("Unexpected unrestored destination exit.", failure);

        public void RequestShutdown()
        {
            ShutdownRequestCount++;
            events.Add("shutdown-request");
            ShutdownRequested.TrySetResult();
        }

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }

    private sealed class DangerSettingsEditSession : ISettingsEditSession
    {
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
        {
            environment.Commit(Values);
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        private readonly List<string> events;

        internal DangerSettingsEditSession(Settings values, List<string> events)
        {
            Values = values;
            this.events = events;
        }

        public Settings Values { get; }

        internal int SaveCount { get; private set; }

        public void Reload()
        {
        }

        public void Save()
        {
            SaveCount++;
            events.Add("settings-save");
        }
    }

    private sealed class DangerWorkspacePort : ISettingsDialogWorkspacePort
    {
        internal Func<Task>? ExportOperation { get; init; }
        public bool HasPlaylistTables => true;

        public long PlaylistCatalogVersion => 0;

        public IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots() => [];

        public bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath) => false;

        public void SchedulePlaylistUrlCompletionRefresh(string reason)
        {
        }

        public Task<BeatorajaBmtPreparation> PrepareBeatorajaBmtSettingsAsync(BeatorajaBmtOptionsSnapshot previous, BeatorajaBmtOptionsSnapshot target, LibraryFileMutationCapability capability)
            => Task.FromResult(new BeatorajaBmtPreparation(previous, target, [], []));

        public Task CompleteBeatorajaBmtSettingsAsync(BeatorajaBmtPreparation preparation, LibraryFileMutationCapability capability)
            => ExportOperation?.Invoke() ?? Task.CompletedTask;

        public Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string operationName) => operation();

        public event EventHandler<PlaylistCatalogChangedEventArgs> PlaylistCatalogChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class DangerPlayHistoryPort : ISettingsDialogPlayHistoryPort
    {
        internal int InvalidationCount { get; private set; }

        internal string? LastInvalidationReason { get; private set; }

        public void InvalidateReadCache(string reason)
        {
            InvalidationCount++;
            LastInvalidationReason = reason;
        }

        public void RefreshDisplayTargetCatalog(bool queueRefreshWhenSelectionChanges = true)
        {
        }

        public void RefreshDisplayTargetSetsFromSettings(
            string serializedDisplayTargetSets,
            bool queueRefreshWhenSelectionChanges)
        {
        }
    }

    private sealed class DangerSchemaDialogPort : ILr2PlayHistorySchemaUninstallDialogPort
    {
        private readonly TaskCompletionSource<UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool HoldResult { get; set; }

        internal Lr2PlayHistorySchemaUninstallMode ImmediateResult { get; set; }

        internal int CallCount { get; private set; }

        public Task<UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>> ShowAsync(
            string scoreDbPath,
            CancellationToken cancellationToken = default, string? candidateDiscardNotice = null)
        {
            CallCount++;
            Started.TrySetResult();
            return HoldResult
                ? completion.Task
                : Task.FromResult(new UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>(
                    UiInteractionStatus.Accepted,
                    ImmediateResult,
                    error: null));
        }

        internal void Complete(UiInteractionStatus status)
        {
            completion.TrySetResult(new UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>(status));
        }
    }

    private sealed class DangerApplicationDataStore : IApplicationDataUninstallStore
    {
        private readonly List<string>? events;

        internal DangerApplicationDataStore(List<string>? events = null)
        {
            this.events = events;
        }

        internal int CallCount { get; private set; }

        internal Exception? Failure { get; set; }

        public void Uninstall(string songDbPath)
        {
            CallCount++;
            events?.Add("store");
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    private sealed class DangerDialogService : IUiDialogService
    {
        private readonly List<string>? events;
        private readonly TaskCompletionSource<UiDialogResult> confirmationCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal DangerDialogService(List<string>? events = null)
        {
            this.events = events;
        }

        internal bool HoldConfirmation { get; set; }

        internal MessageBoxResult ImmediateConfirmationResult { get; set; } = MessageBoxResult.OK;

        internal TaskCompletionSource ConfirmationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConfirmationCount { get; private set; }

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; } = null!;

        internal List<UiMessageRequest> Messages { get; } = [];

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal Exception? MessageTaskFailure { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            if (string.Equals(request.MessageBoxText, Resources.Msg_success_uninstall, StringComparison.Ordinal))
            {
                events?.Add("message-success");
            }
            else if (string.Equals(request.MessageBoxText, Resources.Msg_ApplicationWillExit, StringComparison.Ordinal))
            {
                events?.Add("message-exit");
            }
            return MessageTaskFailure == null ? Task.FromResult(MessageResult) : Task.FromException<UiDialogResult>(MessageTaskFailure);
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            events?.Add("confirm");
            ConfirmationCount++;
            LastConfirmationRequest = request;
            ConfirmationStarted.TrySetResult();
            return HoldConfirmation
                ? confirmationCompletion.Task
                : Task.FromResult(UiDialogResult.FromMessageBoxResult(ImmediateConfirmationResult));
        }

        internal void CompleteConfirmation(MessageBoxResult result)
        {
            confirmationCompletion.TrySetResult(UiDialogResult.FromMessageBoxResult(result));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingPresentationPort : ISettingDialogPresentationPort
    {
        internal int CloseRequestCount { get; private set; }

        internal Action? CloseAction { get; set; }

        public void OpenSettingsDialog(bool deferPresentation = false)
        {
        }

        public void OpenInitialSetupLanguageDialog()
        {
        }

        public void CloseSettingsDialog()
        {
            CloseRequestCount++;
            CloseAction?.Invoke();
        }

        public Task CloseSettingsDialogAsync()
        {
            CloseSettingsDialog();
            return Task.CompletedTask;
        }

        public void RefreshAppearanceSelection()
        {
        }
    }

    private enum PlaylistUriCompletionRoute
    {
        CancelButton,
        NativeClose
    }

    private sealed class RecordingSettingsRouteDialogService : IUiDialogService
    {
        private readonly Queue<string> folderPaths;

        internal RecordingSettingsRouteDialogService(params string[] folderPaths)
        {
            this.folderPaths = new Queue<string>(folderPaths);
        }

        internal Window ExpectedOwner { get; set; } = null!;

        internal List<UiFolderPickerRequest> FolderRequests { get; } = [];

        internal Queue<UiFolderPickerResult> FolderResults { get; } = [];

        internal Queue<Task<UiFolderPickerResult>> FolderTasks { get; } = [];

        internal List<UiSaveFilePickerRequest> SaveFileRequests { get; } = [];

        internal List<UiFilePickerRequest> FileRequests { get; } = [];

        internal Exception? FileSynchronousFailure { get; set; }

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal Queue<Task<UiDialogResult>> ConfirmationTasks { get; } = [];

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal UiSaveFilePickerResult SaveFileResult { get; set; } = new(UiDialogStatus.CancelledByUser);

        internal UiFilePickerResult FileResult { get; set; } = new(UiDialogStatus.CancelledByUser);

        internal Type LastWindowType { get; private set; } = null!;

        internal Window LastCreatedWindow { get; private set; } = null!;

        internal Window LastWindowOwner { get; private set; } = null!;

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            MessageRequests.Add(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            ConfirmationRequests.Add(request);
            if (ConfirmationTasks.Count > 0)
            {
                return ConfirmationTasks.Dequeue();
            }
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            Assert.AreSame(ExpectedOwner, request.Owner);
            LastWindowOwner = request.Owner;
            LastWindowType = typeof(TWindow);
            TWindow created = request.CreateWindow();
            LastCreatedWindow = created;
            return Task.FromResult(new UiWindowDialogResult<TResult>(
                UiDialogStatus.ClosedByUser,
                request.CreateResult(created)));
        }

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default)
        {
            FileRequests.Add(request);
            if (FileSynchronousFailure != null) { throw FileSynchronousFailure; }
            return Task.FromResult(FileResult);
        }

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.AreSame(ExpectedOwner, request.Owner);
            FolderRequests.Add(request);
            if (FolderTasks.Count > 0)
            {
                return FolderTasks.Dequeue();
            }
            if (FolderResults.Count > 0)
            {
                return Task.FromResult(FolderResults.Dequeue());
            }
            string path = folderPaths.Dequeue();
            return Task.FromResult(new UiFolderPickerResult(UiDialogStatus.Accepted, [path]));
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default)
        {
            SaveFileRequests.Add(request);
            return Task.FromResult(SaveFileResult);
        }

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingNativeWindowTitleBarGateway : INativeWindowTitleBarGateway
    {
        internal int ApplyCount { get; private set; }

        internal NativeWindowTitleBarAppearance LastAppearance { get; private set; }

        public void Apply(IntPtr windowHandle, NativeWindowTitleBarAppearance appearance)
        {
            Assert.AreNotEqual(IntPtr.Zero, windowHandle);
            ApplyCount++;
            LastAppearance = appearance;
        }
    }

    private sealed class RecordingReleaseNotesDialogService : IUiDialogService
    {
        internal Window ExpectedOwner { get; set; } = null!;
        internal ReleaseNotesWindow? CreatedWindow { get; private set; }
        internal int WindowCount { get; private set; }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default) where TWindow : Window
        {
            Assert.AreSame(ExpectedOwner, request.Owner);
            TWindow created = request.CreateWindow();
            CreatedWindow = created as ReleaseNotesWindow;
            Assert.IsNotNull(CreatedWindow);
            WindowCount++;
            return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.ClosedByUser, request.CreateResult(created)));
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
