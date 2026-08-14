using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
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
using Ribbit.Logging;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingsWindowPresentationTests
{
    [TestMethod]
    public void SettingsWindow_IsStandardResizableWindow()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new SettingsWindow();

            Assert.IsInstanceOfType<Window>(window);
            Assert.AreEqual(ResizeMode.CanResize, window.ResizeMode);
            Assert.AreEqual(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
            Assert.IsFalse(window.ShowInTaskbar);
            Assert.AreEqual(820d, window.Width);
            Assert.AreEqual(760d, window.Height);
            Assert.AreEqual(820d, window.MinWidth);
            Assert.AreEqual(600d, window.MinHeight);
        });
    }

    [TestMethod]
    public void ReleaseNotesWindow_UsesOwnedModalPresentationContract()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new ReleaseNotesWindow();
            Assert.AreEqual(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
            Assert.IsFalse(window.ShowInTaskbar);
            Assert.AreEqual(ResizeMode.CanResize, window.ResizeMode);
            Assert.AreEqual(760d, window.Width);
            Assert.AreEqual(640d, window.Height);
        });
    }

    [TestMethod]
    public void RemainingPages_PreserveOwnerRoutesAndKeepDestructiveActionsOnlyInAdvanced()
    {
        string root = FindRepositoryRoot();
        string backupXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Settings", "Pages", "BackupSettingsPage.xaml"));
        string backupCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Settings", "Pages", "BackupSettingsPage.xaml.cs"));
        string advancedXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Settings", "Pages", "AdvancedSettingsPage.xaml"));
        string advancedCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Settings", "Pages", "AdvancedSettingsPage.xaml.cs"));
        string generalXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Settings", "Pages", "GeneralSettingsPage.xaml"));
        string aboutXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Settings", "Pages", "AboutSettingsPage.xaml"));

        StringAssert.Contains(backupCode, "HandlePlaylistBackupAsync");
        StringAssert.Contains(backupCode, "HandlePlaylistRestoreAsync");
        Assert.IsFalse(backupXaml.Contains("uninstall", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(backupCode.Contains("Uninstall", StringComparison.Ordinal));
        Assert.IsFalse(generalXaml.Contains("uninstallLr2PlayHistorySchemaButtonClicked", StringComparison.Ordinal));
        Assert.AreEqual(2, Regex.Matches(advancedXaml, "SettingsDangerButtonStyle", RegexOptions.CultureInvariant).Count);
        StringAssert.Contains(advancedCode, "HandleUninstallLr2PlayHistorySchemaAsync");
        StringAssert.Contains(advancedCode, "HandleApplicationDataUninstallAsync");
        StringAssert.Contains(aboutXaml, "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases");
    }

    [TestMethod]
    public void PlaylistUriValidation_CancelWithoutDraftChangesStartsNextPresentationClean()
    {
        AssertPlaylistUriValidationClearedOnReopen(PlaylistUriCompletionRoute.CancelButton);
    }

    [TestMethod]
    public void PlaylistUriValidation_SaveWithoutDraftChangesStartsNextPresentationClean()
    {
        AssertPlaylistUriValidationClearedOnReopen(PlaylistUriCompletionRoute.SaveButton);
    }

    [TestMethod]
    public void PlaylistUriValidation_NativeCloseWithoutDraftChangesStartsNextPresentationClean()
    {
        AssertPlaylistUriValidationClearedOnReopen(PlaylistUriCompletionRoute.NativeClose);
    }

    [TestMethod]
    public void SettingsWindow_ReleaseNotesUsesInjectedOwnedWindowRouteWithoutChangingDraft()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
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

    [TestMethod]
    public void SettingsWindow_Lr2AdvancedRouteAwaitsPendingDialogWithoutBlockingDispatcher()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel settings = owner.SettingDialog;
            var dialogs = new PendingLr2AdvancedPathsDialogService();
            var window = new SettingsWindow(dialogs) { DataContext = settings };
            dialogs.ExpectedOwner = window;
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                Task route = window.HandleEditCustomLr2PathsAsync();

                Assert.IsFalse(route.IsCompleted);
                Assert.AreEqual(1, dialogs.WindowCount);
                Assert.AreSame(window, dialogs.LastOwner);
                Assert.AreSame(settings, dialogs.CreatedWindow.DataContext);

                bool dispatcherWorkCompleted = false;
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => dispatcherWorkCompleted = true));
                PumpUntil(window, () => dispatcherWorkCompleted, "The settings dispatcher stopped while the LR2 path dialog route was pending.");
                Assert.IsFalse(route.IsCompleted);

                dialogs.Complete(UiDialogStatus.ClosedByUser);
                PumpUntil(window, () => route.IsCompleted, "The LR2 path dialog route did not complete after its dialog task completed.");
                route.GetAwaiter().GetResult();
                Assert.IsTrue(route.IsCompletedSuccessfully);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_AboutIdentityPresentationUpdatesVersionAndBuildWithoutRedundantStatus()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousCulture = BeMusicSeeker.Properties.Resources.Culture?.Name ?? "ja-JP";
            SettingsWindow window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
                window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    Width = 820,
                    Height = 600
                };

                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedIndex = 9;
                PumpDispatcher(window.Dispatcher);
                var page = (AboutSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                var version = (TextBlock)page.FindName("textBlockVerNum");
                var build = (TextBlock)page.FindName("textBlockBuildNum");
                string englishVersion = version.Text;
                string englishBuild = build.Text;

                ResourceService.Current.ChangeCulture("ja-JP");
                PumpDispatcher(window.Dispatcher);

                Assert.AreNotEqual(englishVersion, version.Text);
                Assert.AreNotEqual(englishBuild, build.Text);
                StringAssert.StartsWith(version.Text, "バージョン:");
                StringAssert.StartsWith(build.Text, "ビルド:");
                Assert.IsNull(page.FindName("textBlockUpdateStatus"));
                Assert.IsFalse(FindDescendants<SettingsStatusBanner>(page).Any());
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

    [TestMethod]
    public void SettingsWindow_JapaneseOperationAndPlayerCopyAppearsOnItsOwningPages()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousCulture = BeMusicSeeker.Properties.Resources.Culture?.Name ?? "ja-JP";
            SettingsWindow window = null;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
                window = new SettingsWindow { DataContext = owner.SettingDialog };
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var pageHost = (ContentControl)window.FindName("settingsPageContent");

                var generalPage = (GeneralSettingsPage)pageHost.Content;
                var standalone = (RadioButton)generalPage.FindName("radioButtonNotUseLR2");
                Assert.AreEqual("スタンドアローン", standalone.Content);
                Assert.AreEqual("スタンドアローン", new RadioButtonAutomationPeer(standalone).GetName());

                navigation.SelectedIndex = 2;
                PumpDispatcher(window.Dispatcher);
                var playbackPage = (PlaybackSettingsPage)pageHost.Content;
                SettingsOptionRow internalPlayer = FindDescendants<SettingsOptionRow>(playbackPage)
                    .Single(row => Equals(row.Header, Resources.Player_Name_Internal));
                SettingsOptionRow lr2Player = FindDescendants<SettingsOptionRow>(playbackPage)
                    .Single(row => Equals(row.Header, "LR2"));
                Assert.AreEqual("※オーディオ設定で設定してください。", internalPlayer.Description);
                Assert.AreEqual("LR2 の実行ファイルで譜面を再生します。", lr2Player.Description);
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

    [TestMethod]
    public void AdvancedDangerZone_SchemaUninstallActualButtonBlocksNativeCloseUntilCancel()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = CreateDangerSchemaScope(out Settings values, out string scoreDbPath);
            SettingsWindow window = null;
            try
            {
                var dialogs = new DangerDialogService();
                var schemaDialog = new DangerSchemaDialogPort { HoldResult = true };
                var store = new DangerApplicationDataStore();
                DangerDialogContext context = CreateDangerDialog(values, dialogs, schemaDialog, store);
                bool draftValue = !context.Settings.ShowRecommUpdatedMsg;
                context.Settings.ShowRecommUpdatedMsg = draftValue;
                var presentation = new RecordingPresentationPort();
                context.Settings.AttachPresentationPort(presentation);
                window = new SettingsWindow { DataContext = context.Settings };
                windowTest.ShowAndWaitForContentRendered(window);
                PrepareSchemaDangerOperation(context.Settings, scoreDbPath);

                ClickAdvancedDangerButton(window, Resources.Lr2_play_history_schema_uninstall);
                PumpUntil(window, () => schemaDialog.Started.Task.IsCompletedSuccessfully,
                    "schema uninstall dialog was not reached");
                Assert.IsFalse(((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled);
                Assert.IsTrue(SimulateNativeClose(window));
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(0, presentation.CloseRequestCount);
                Assert.AreEqual(0, store.CallCount);

                schemaDialog.Complete(UiInteractionStatus.CancelledByUser);
                PumpUntil(window, () => ((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled,
                    "schema uninstall cancellation did not leave the operation gate");

                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(draftValue, context.Settings.ShowRecommUpdatedMsg);
                Assert.IsTrue(context.Settings.HasPendingSettingChanges());
                Assert.AreEqual(0, context.PlayHistory.InvalidationCount);
                Assert.AreEqual(0, context.ReloadCount());
                AssertInstalledSchema(scoreDbPath, expectedInstalled: true);
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AdvancedDangerZone_SchemaUninstallActualButtonPreservesFailureAndSuccessOwnerContracts(bool success)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = CreateDangerSchemaScope(out Settings values, out string scoreDbPath);
            SettingsWindow window = null;
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
                DangerDialogContext context = CreateDangerDialog(values, dialogs, schemaDialog, store);
                bool draftValue = !context.Settings.ShowRecommUpdatedMsg;
                context.Settings.ShowRecommUpdatedMsg = draftValue;
                window = new SettingsWindow { DataContext = context.Settings };
                windowTest.ShowAndWaitForContentRendered(window);
                PrepareSchemaDangerOperation(context.Settings, scoreDbPath);

                ClickAdvancedDangerButton(window, Resources.Lr2_play_history_schema_uninstall);
                PumpUntil(window, () => ((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled,
                    "schema uninstall did not complete");

                Assert.AreEqual(1, schemaDialog.CallCount);
                Assert.AreEqual(0, store.CallCount);
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(draftValue, context.Settings.ShowRecommUpdatedMsg);
                Assert.IsTrue(context.Settings.HasPendingSettingChanges());
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
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AdvancedDangerZone_ApplicationDataActualButtonPreservesCancelAndFailureContracts(bool storeFailure)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var values = new Settings { OperationModeLR2DB = true, LR2SongDBPath = "song.db" };
            var dialogs = new DangerDialogService { HoldConfirmation = !storeFailure };
            var schemaDialog = new DangerSchemaDialogPort();
            var store = new DangerApplicationDataStore
            {
                Failure = storeFailure ? new InvalidOperationException("store failure") : null
            };
            DangerDialogContext context = CreateDangerDialog(values, dialogs, schemaDialog, store);
            bool draftValue = !context.Settings.ShowRecommUpdatedMsg;
            context.Settings.ShowRecommUpdatedMsg = draftValue;
            var presentation = new RecordingPresentationPort();
            context.Settings.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = context.Settings };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                ClickAdvancedDangerButton(window, Resources.Settings_uninstall_application_data);
                Assert.AreEqual(0, schemaDialog.CallCount);

                if (!storeFailure)
                {
                    Assert.IsTrue(dialogs.ConfirmationStarted.Task.IsCompletedSuccessfully);
                    Assert.IsFalse(((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled);
                    Assert.IsTrue(SimulateNativeClose(window));
                    PumpDispatcher(window.Dispatcher);
                    Assert.AreEqual(0, presentation.CloseRequestCount);
                    dialogs.CompleteConfirmation(MessageBoxResult.Cancel);
                }

                PumpUntil(window, () => ((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled,
                    "application-data uninstall did not complete");

                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(draftValue, context.Settings.ShowRecommUpdatedMsg);
                Assert.IsTrue(context.Settings.HasPendingSettingChanges());
                Assert.AreEqual(storeFailure ? 1 : 0, store.CallCount);
                Assert.AreEqual(storeFailure ? 1 : 0, dialogs.Messages.Count);
                if (storeFailure)
                {
                    StringAssert.Contains(dialogs.Messages[0].MessageBoxText, Resources.Msg_failed_uninstall);
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

    [TestMethod]
    public void AdvancedDangerZone_ApplicationDataActualButtonCompletesProductionShellShutdownRoute()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var values = new Settings { OperationModeLR2DB = true, LR2SongDBPath = "song.db" };
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
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel shellViewModel = composition.CreateMainWindowViewModelForTest();
            DangerDialogContext context = CreateDangerDialog(
                shellViewModel,
                settingsSession,
                composition,
                dialogs,
                schemaDialog,
                store);
            typeof(MainWindowViewModel)
                .GetProperty("SettingDialog", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(shellViewModel, context.Settings);
            // Keep the production MainWindow shutdown owner active while isolating unrelated startup initialization.
            typeof(MainWindowViewModel)
                .GetProperty("ShellActivationWorkflow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(shellViewModel, CreateNoOpShellActivationWorkflow());
            object previousVmResource = Application.Current.Resources["vm"];
            Application.Current.Resources["vm"] = shellViewModel;
            SettingsWindow window = null;
            var owner = new MainWindow(
                shellViewModel,
                createdWindow =>
                {
                    Assert.IsFalse(createdWindow.IsVisible);
                    Assert.AreEqual(0, TestWindowPresentationScope.GetNativeHandle(createdWindow));
                    windowTest.PrepareForOwnedPresentation(createdWindow);
                    window = createdWindow;
                });
            var decoy = new Window();
            Exception interactionFailure = null;
            windowTest.PrepareForOwnedPresentation(owner);
            owner.Show();
            try
            {
                windowTest.PrepareForOwnedPresentation(decoy);
                decoy.Show();
                owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() =>
                {
                    try
                    {
                        window = owner.OwnedWindows.OfType<SettingsWindow>().Single();
                        Assert.AreSame(
                            window,
                            typeof(MainWindow)
                                .GetField("settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .GetValue(owner));
                        Assert.AreSame(owner, window.Owner);
                        nint settingsHandle = TestWindowPresentationScope.GetNativeHandle(window);
                        Assert.AreNotEqual(0, settingsHandle);
                        Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(settingsHandle));
                        Assert.AreNotEqual(settingsHandle, TestWindowPresentationScope.ForegroundWindow);
                        window.Closing += (_, _) => events.Add("settings-closing");
                        window.Closed += (_, _) => events.Add("settings-closed");
                        ClickAdvancedDangerButton(window, Resources.Settings_uninstall_application_data);
                    }
                    catch (Exception exception)
                    {
                        interactionFailure = exception;
                        window?.CloseFromPresentation();
                    }
                }));

                context.Settings.OpenCommand.Execute();
                if (interactionFailure != null)
                {
                    throw new AssertFailedException("The production settings modal interaction failed.", interactionFailure);
                }

                Assert.IsNotNull(window);
                PumpUntil(
                    owner.Dispatcher,
                    () => lifetime.ShutdownRequestCount == 1,
                    "application-data uninstall did not complete the production shell shutdown route");

                Assert.AreEqual(0, schemaDialog.CallCount);
                Assert.AreEqual(1, store.CallCount);
                Assert.IsFalse(window.IsVisible);
                Assert.AreEqual(SettingsWindowCloseReason.OwnerShutdown, window.CloseReason);
                Assert.IsNull(window.DataContext);
                Assert.IsFalse(GetPresentationActive(context.Settings));
                Assert.AreSame(shellViewModel, owner.DataContext);
                Assert.IsTrue(owner.IsVisible,
                    "The recording lifetime must replace only the final process-termination boundary.");
                Assert.IsTrue(decoy.IsVisible);
                Assert.IsTrue(shellViewModel.ShellShutdownWorkflow.IsShutdownPrepared);
                Assert.IsTrue(shellViewModel.ShellShutdownWorkflow.IsCloseAllowed);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Assert.AreEqual(1, lifetime.CoordinatedShutdownCount);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "confirm", "store", "message-success", "message-exit",
                        "settings-closing", "settings-closed", "shutdown-mark:window_close",
                        "settings-save", "shutdown-request"
                    },
                    events);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseFromPresentation();
                }
                if (owner.IsVisible)
                {
                    owner.Close();
                    PumpUntil(
                        owner.Dispatcher,
                        () => shellViewModel.ShellShutdownWorkflow.IsCloseAllowed,
                        "shell shutdown cleanup did not reach its close allowance");
                    if (owner.IsVisible)
                    {
                        owner.Close();
                    }
                }
                decoy.Close();
                if (previousVmResource == null)
                {
                    Application.Current.Resources.Remove("vm");
                }
                else
                {
                    Application.Current.Resources["vm"] = previousVmResource;
                }
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_NavigationHasTenLocalizedCategoriesInStableOrder()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement navigation = FindNamedElement(document, "settingsNavigation");
        List<XElement> items = navigation.Elements(PresentationName("ListBoxItem")).ToList();
        string[] expectedResourcePaths =
        [
            "Resources.General",
            "Resources.Appearance",
            "Resources.Playback",
            "Resources.Audio",
            "Resources.Recording",
            "Resources.Playlist",
            "Resources.Install",
            "Resources.Backup",
            "Resources.Advanced_settings",
            "Resources.About_this_app"
        ];

        Assert.AreEqual(10, items.Count);
        CollectionAssert.AreEqual(
            expectedResourcePaths,
            items.Select(item => ExtractResourcePath(item.Attribute("Content")?.Value)).ToArray());
        Assert.AreEqual("0", navigation.Attribute("SelectedIndex")?.Value);
    }

    [TestMethod]
    public void SettingsWindow_JapaneseTitleAndAdvancedCategoryUseDistinctLocalizedAutomationText()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousCulture = BeMusicSeeker.Properties.Resources.Culture?.Name ?? "ja-JP";
            SettingsWindow window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
                window = new SettingsWindow { DataContext = owner.SettingDialog };
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var advanced = (ListBoxItem)navigation.Items[8];

                Assert.AreEqual("Settings", window.Title);
                Assert.AreEqual("Advanced settings", advanced.Content);
                Assert.AreEqual("Advanced settings", AutomationProperties.GetName(advanced));

                ResourceService.Current.ChangeCulture("ja-JP");
                PumpDispatcher(window.Dispatcher);

                Assert.AreEqual("設定", window.Title);
                Assert.AreEqual("詳細設定", advanced.Content);
                Assert.AreEqual("詳細設定", AutomationProperties.GetName(advanced));
                Assert.AreNotEqual(window.Title, advanced.Content,
                    "The settings window identity must not reuse the Advanced category label.");
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

    [TestMethod]
    public void SettingsStatusBanner_CoercesBlankContentAndRecoversBindingsWithoutOverridingVisibility()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
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
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);
                source.Message = " \t ";
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);

                source.Message = "Device ready";
                PumpDispatcher(window.Dispatcher);
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
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(Visibility.Collapsed, banner.Visibility);
                Assert.AreEqual(string.Empty, peer.GetName());
                Assert.AreEqual(string.Empty, peer.GetItemStatus());
                TextBlock icon = FindDescendants<TextBlock>(banner).Single(text => text.Text == "!");
                Assert.IsFalse(icon.IsVisible);

                source.Message = "Recovered";
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(Visibility.Visible, banner.Visibility);
                Assert.AreEqual("Recovered", peer.GetName());

                banner.Visibility = Visibility.Collapsed;
                source.Message = "Still explicitly collapsed";
                PumpDispatcher(window.Dispatcher);
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

    [TestMethod]
    public void Lr2SchemaBanners_ActualPagesRevertSeverityAndAutomationStatusAfterRepairBecomesUnavailable()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                foreach (int categoryIndex in new[] { 0, 8 })
                {
                    navigation.SelectedIndex = categoryIndex;
                    PumpDispatcher(window.Dispatcher);
                    var page = (UserControl)((ContentControl)window.FindName("settingsPageContent")).Content;
                    SettingsStatusBanner banner = FindDescendants<SettingsStatusBanner>(page).Single(candidate =>
                        candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                        == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaStatusText));

                    SetSchemaPresentationStatus(owner.SettingDialog, Lr2PlayHistorySchemaStatus.Installed);
                    PumpDispatcher(window.Dispatcher);
                    AssertSchemaBannerState(banner, "i", "Information");

                    SetSchemaPresentationStatus(owner.SettingDialog, Lr2PlayHistorySchemaStatus.Repairable);
                    PumpDispatcher(window.Dispatcher);
                    AssertSchemaBannerState(banner, "!", "Warning");

                    SetSchemaPresentationStatus(owner.SettingDialog, Lr2PlayHistorySchemaStatus.Installed);
                    PumpDispatcher(window.Dispatcher);
                    AssertSchemaBannerState(banner, "i", "Information");
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

    [TestMethod]
    public void AudioSettingsPage_NullDeviceTestStatusDoesNotRenderAnEmptyBanner()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedIndex = 3;
                PumpDispatcher(window.Dispatcher);
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

    [TestMethod]
    public void AudioSettingsPage_LongDeviceTestStatusWrapsAtMinimumWindowWidthAndKeepsFullAutomationName()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            const string longMessage = "The selected audio device could not be initialized because its current output format is unavailable. Choose another device or format, then run the audio test again.";
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            window.Width = 820;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedIndex = 3;
                typeof(SettingsDialogViewModel)
                    .GetProperty(nameof(SettingsDialogViewModel.AudioDeviceTestStatusMessage))!
                    .SetValue(owner.SettingDialog, longMessage);
                PumpDispatcher(window.Dispatcher);

                var page = (AudioSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                SettingsStatusBanner banner = FindDescendants<SettingsStatusBanner>(page).Single(candidate =>
                    candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                    == nameof(SettingsDialogViewModel.AudioDeviceTestStatusMessage));
                TextBlock message = FindDescendants<TextBlock>(banner).Single(text => text.Text == longMessage);
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(banner);

                Assert.AreEqual(820d, window.ActualWidth, 0.5d);
                Assert.AreEqual(TextWrapping.Wrap, message.TextWrapping);
                Assert.IsTrue(message.ActualHeight > message.FontSize * 1.5d,
                    $"The long audio status remained one line at the 820 DIP window width ({message.ActualWidth} x {message.ActualHeight}).");
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

    [TestMethod]
    public void SettingsWindow_SelectionControlsPageVisibilityAndHeader()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement header = document.Descendants(PresentationName("ContentControl"))
            .Single(element => element.Attribute("Content")?.Value.Contains("SelectedItem.Content", StringComparison.Ordinal) == true);
        StringAssert.Contains(header.Attribute("Content")!.Value, "ElementName=settingsNavigation");
        Assert.IsFalse(SourceTextTestHelper.ReadSettingsWindowXamlSourceText()
            .Contains("Visibility=\"{Binding IsSelected, ElementName=navigation", StringComparison.Ordinal));

        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new SettingsWindow();
            var sharedDataContext = new object();
            window.DataContext = sharedDataContext;
            var navigation = (ListBox)window.FindName("settingsNavigation");
            var content = (ContentControl)window.FindName("settingsPageContent");
            Type[] pageTypes =
            [
                typeof(GeneralSettingsPage),
                typeof(AppearanceSettingsPage),
                typeof(PlaybackSettingsPage),
                typeof(AudioSettingsPage),
                typeof(RecordingSettingsPage),
                typeof(PlaylistSettingsPage),
                typeof(InstallSettingsPage),
                typeof(BackupSettingsPage),
                typeof(AdvancedSettingsPage),
                typeof(AboutSettingsPage)
            ];

            Assert.AreEqual(0, navigation.SelectedIndex);
            for (int index = 0; index < pageTypes.Length; index++)
            {
                navigation.SelectedIndex = index;
                Assert.AreEqual(pageTypes[index], content.Content.GetType());
                Assert.AreSame(sharedDataContext, ((FrameworkElement)content.Content).DataContext);
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_NavigationSupportsKeyboardAutomationAndResetsPageScroll()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow
            {
                DataContext = viewModel.SettingDialog,
                Width = 820,
                Height = 600
            };
            try
            {
                windowTest.ShowAndWaitForContentRendered(
                    window,
                    TestWindowActivation.ForegroundInteraction);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var scroller = (ScrollViewer)window.FindName("settingsPageScrollViewer");
                var header = (ContentControl)window.FindName("settingsPageHeader");
                var pageHost = (ContentControl)window.FindName("settingsPageContent");

                var firstItem = (ListBoxItem)navigation.Items[0];
                Assert.AreEqual(SelectionMode.Single, navigation.SelectionMode);
                Assert.AreEqual(KeyboardNavigationMode.Continue, KeyboardNavigation.GetDirectionalNavigation(navigation));
                Assert.IsTrue(firstItem.Focusable, "Navigation items must participate in keyboard focus traversal.");
                window.Activate();
                Assert.IsTrue(firstItem.Focus(), "The displayed navigation item must accept keyboard focus.");
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(firstItem.IsKeyboardFocusWithin);
                RaiseKey(firstItem, Key.Down);
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(1, navigation.SelectedIndex);
                Assert.IsInstanceOfType<AppearanceSettingsPage>(pageHost.Content);
                Assert.AreEqual(((ListBoxItem)navigation.SelectedItem).Content, header.Content);
                ((FrameworkElement)pageHost.Content).Height = 1200;

                var secondItem = (ListBoxItem)navigation.Items[1];
                Assert.IsTrue(secondItem.IsKeyboardFocusWithin);
                RaiseKey(secondItem, Key.Up);
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(0, navigation.SelectedIndex);
                Assert.IsInstanceOfType<GeneralSettingsPage>(pageHost.Content);
                Assert.AreEqual(((ListBoxItem)navigation.SelectedItem).Content, header.Content);

                foreach (ListBoxItem item in navigation.Items)
                {
                    Assert.IsFalse(
                        string.IsNullOrWhiteSpace(AutomationProperties.GetName(item)),
                        "Every settings category must expose a localized automation name.");
                    StringAssert.StartsWith(AutomationProperties.GetAutomationId(item), "SettingsCategory");
                }

                var navigationPeer = new ListBoxAutomationPeer(navigation);
                var selectionProvider = (ISelectionProvider)navigationPeer.GetPattern(PatternInterface.Selection);
                Assert.IsNotNull(selectionProvider);
                Assert.IsFalse(selectionProvider.CanSelectMultiple);
                AutomationPeer playbackPeer = navigationPeer.GetChildren()
                    .Single(peer => peer.GetAutomationId() == "SettingsCategoryPlayback");
                var playbackSelection = (ISelectionItemProvider)playbackPeer.GetPattern(PatternInterface.SelectionItem);
                Assert.IsNotNull(playbackSelection);
                playbackSelection.Select();
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(2, navigation.SelectedIndex);
                Assert.IsInstanceOfType<PlaybackSettingsPage>(pageHost.Content);
                Assert.AreEqual(((ListBoxItem)navigation.SelectedItem).Content, header.Content);
                Assert.IsTrue(playbackSelection.IsSelected);

                navigation.SelectedIndex = 0;
                var selectedPage = (FrameworkElement)pageHost.Content;
                selectedPage.Height = 1200;
                window.UpdateLayout();
                Assert.IsTrue(scroller.ScrollableHeight > 0, "The shell body must expose overflow through its shared scroller.");
                scroller.ScrollToVerticalOffset(Math.Min(100d, scroller.ScrollableHeight));
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(scroller.VerticalOffset > 0, "The body scroller must accept a non-zero page offset.");
                navigation.SelectedIndex = 1;
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(0d, scroller.VerticalOffset, "Changing categories must reset the shared body scroller.");
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

    [TestMethod]
    public void SettingsWindow_AppearanceThemeBindingSurvivesDeferredPageConnectionAndCancelRollback()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousTheme = Settings.Default.AppearanceTheme;
            SettingsWindow window = null;
            try
            {
                Settings.Default.AppearanceTheme = AppThemeService.Light;
                AppThemeService.ApplyTheme(AppThemeService.Light);
                MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create(Settings.Default);
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
                Assert.AreEqual(0, navigation.SelectedIndex);
                Assert.IsInstanceOfType<GeneralSettingsPage>(pageHost.Content);

                navigation.SelectedIndex = 1;
                PumpDispatcher(window.Dispatcher);
                var appearancePage = (AppearanceSettingsPage)pageHost.Content;
                Assert.AreSame(settings, appearancePage.DataContext);
                var selector = (RadioButton)appearancePage.FindName("radioButtonDarkTheme");
                BindingExpression themeBinding = selector.GetBindingExpression(ToggleButton.IsCheckedProperty);
                Assert.IsNotNull(themeBinding);
                Assert.AreEqual(BindingStatus.Active, themeBinding.Status);
                Assert.AreEqual(BindingMode.TwoWay, themeBinding.ParentBinding.Mode);
                Assert.AreEqual(false, selector.IsChecked);

                selector.Focus();
                selector.IsChecked = true;
                PumpDispatcher(window.Dispatcher);

                Assert.AreEqual(true, selector.IsChecked);
                Assert.AreEqual(AppThemeService.Dark, settings.AppearanceTheme);
                Assert.AreEqual(AppThemeService.Dark, Settings.Default.AppearanceTheme);
                Assert.AreEqual(AppThemeService.Dark, GetAppliedApplicationTheme());
                Assert.IsTrue(BindingOperations.IsDataBound(selector, ToggleButton.IsCheckedProperty));

                ((Button)window.FindName("buttonCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window.Dispatcher);

                Assert.AreEqual(AppThemeService.Light, settings.AppearanceTheme);
                Assert.AreEqual(AppThemeService.Light, Settings.Default.AppearanceTheme);
                Assert.AreEqual(AppThemeService.Light, GetAppliedApplicationTheme());
                Assert.AreEqual(false, selector.IsChecked);
                Assert.IsTrue(BindingOperations.IsDataBound(selector, ToggleButton.IsCheckedProperty));
                Assert.AreEqual(BindingStatus.Active, selector.GetBindingExpression(ToggleButton.IsCheckedProperty)?.Status);
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

    [TestMethod]
    public void ThemedWindows_ApplyInitialAndLiveThemeAndDetachNativeTitleBarOnClose()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string previousTheme = Settings.Default.AppearanceTheme;
            ReleaseNotesWindow releaseNotes = null;
            ReleaseNotesWindow darkReleaseNotes = null;
            Lr2AdvancedPathsDialog advancedPaths = null;
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
                PumpDispatcher(releaseNotes.Dispatcher);

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

                SettingsDialogViewModel settings = MainWindowViewModelTestFactory.Create().SettingDialog;
                advancedPaths = new Lr2AdvancedPathsDialog(settings);
                windowTest.ShowAndWaitForContentRendered(advancedPaths);
                TextBox pathTextBox = FindDescendants<TextBox>(advancedPaths).First();

                Assert.AreEqual(GetApplicationBrushColor("App.DialogBackgroundBrush"), ((SolidColorBrush)advancedPaths.Background).Color);
                Assert.AreEqual(GetApplicationBrushColor("App.ControlBackgroundBrush"), ((SolidColorBrush)pathTextBox.Background).Color);

                Settings.Default.AppearanceTheme = AppThemeService.Dark;
                AppThemeService.ApplyTheme(AppThemeService.Dark);
                PumpDispatcher(advancedPaths.Dispatcher);

                Assert.AreEqual(GetApplicationBrushColor("App.DialogBackgroundBrush"), ((SolidColorBrush)advancedPaths.Background).Color);
                Assert.AreEqual(GetApplicationBrushColor("App.ControlBackgroundBrush"), ((SolidColorBrush)pathTextBox.Background).Color);
            }
            finally
            {
                releaseNotes?.Close();
                darkReleaseNotes?.Close();
                advancedPaths?.Close();
                Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
            }
        });
    }

    [TestMethod]
    public void NativeTitleBarThemeSource_MissingOrNonSolidSemanticResource_Propagates()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string resourceKey = $"{nameof(NativeTitleBarThemeSource_MissingOrNonSolidSemanticResource_Propagates)}.{Guid.NewGuid():N}";
            ResourceDictionary resources = Application.Current.Resources;
            MethodInfo resolverMethod = typeof(AppNativeWindowTitleBarThemeSource).GetMethod(
                "GetRequiredSolidColorBrush",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                [typeof(string)],
                modifiers: null);
            Assert.IsNotNull(resolverMethod);
            Func<string, SolidColorBrush> resolveRequiredBrush =
                resolverMethod.CreateDelegate<Func<string, SolidColorBrush>>();
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

    [TestMethod]
    public void SettingsWindow_AppearanceControlsPreviewThroughMainViewSettingsAndCancelRollback()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            double previousFontSize = Settings.Default.CustomTableFontSize;
            double previousRowHeight = Settings.Default.CustomTableRowHeight;
            double previousHeaderHeight = Settings.Default.CustomTableHeaderHeight;
            SettingsWindow window = null;
            try
            {
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
                SettingsDialogViewModel settings = owner.SettingDialog;
                double savedFontSize = settings.CustomTableFontSize;
                double savedRowHeight = settings.CustomTableRowHeight;
                double savedHeaderHeight = settings.CustomTableHeaderHeight;
                window = new SettingsWindow { DataContext = settings };
                windowTest.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedIndex = 1;
                PumpDispatcher(window.Dispatcher);
                var page = (AppearanceSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;

                Dictionary<string, Slider> sliders = FindDescendants<Slider>(page)
                    .ToDictionary(
                        slider => slider.GetBindingExpression(RangeBase.ValueProperty)?.ParentBinding.Path?.Path
                            ?? throw new AssertFailedException("Appearance slider must bind to a settings property."),
                        StringComparer.Ordinal);
                CollectionAssert.AreEquivalent(
                    new[] { "CustomTableFontSize", "CustomTableRowHeight", "CustomTableHeaderHeight" },
                    sliders.Keys.ToArray());
                Assert.IsFalse(FindDescendants<ListBox>(page).Any(), "Appearance must not render a fake list preview.");

                sliders["CustomTableFontSize"].Value = 14d;
                sliders["CustomTableRowHeight"].Value = 31d;
                sliders["CustomTableHeaderHeight"].Value = 37d;
                PumpDispatcher(window.Dispatcher);

                Assert.AreEqual(14d, owner.ViewSettings.CustomTableFontSize);
                Assert.AreEqual(31d, owner.ViewSettings.CustomTableRowHeight);
                Assert.AreEqual(37d, owner.ViewSettings.CustomTableHeaderHeight);

                Button reset = FindDescendants<Button>(page)
                    .Single(button => Equals(button.Content, Resources.Appearance_table_reset_defaults));
                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, owner.ViewSettings.CustomTableFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, owner.ViewSettings.CustomTableRowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, owner.ViewSettings.CustomTableHeaderHeight);

                ((Button)window.FindName("buttonCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(savedFontSize, owner.ViewSettings.CustomTableFontSize);
                Assert.AreEqual(savedRowHeight, owner.ViewSettings.CustomTableRowHeight);
                Assert.AreEqual(savedHeaderHeight, owner.ViewSettings.CustomTableHeaderHeight);

                string mainWindowXaml = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "MainWindow.xaml");
                StringAssert.Contains(mainWindowXaml, "DataContext.ViewSettings.CustomTableFontSize");
                StringAssert.Contains(mainWindowXaml, "DataContext.ViewSettings.CustomTableRowHeight");
                StringAssert.Contains(mainWindowXaml, "DataContext.ViewSettings.CustomTableHeaderHeight");
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                Settings.Default.CustomTableFontSize = previousFontSize;
                Settings.Default.CustomTableRowHeight = previousRowHeight;
                Settings.Default.CustomTableHeaderHeight = previousHeaderHeight;
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_PlaybackPageAttachAndRadioClickPresentOnlySelectedPlayerWithoutChangingHiddenDrafts()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
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
                ((ListBox)window.FindName("settingsNavigation")).SelectedIndex = 2;
                PumpDispatcher(window.Dispatcher);
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
                PumpDispatcher(window.Dispatcher);

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
                settings.ResetSettings();
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_GeneralConfigPathBindingUpdatesWhenSameRootRestoresStandardTuple()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingsRootReselect_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            SettingsDialogViewModel settings = null;
            SettingsWindow window = null;
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
                settings = mainViewModel.SettingDialog;
                settings.LR2RootPath = root;
                settings.LR2ConfigXmlPath = customConfig;
                window = new SettingsWindow
                {
                    DataContext = settings,
                    PlaybackPanel = mainViewModel.PlaybackPanel,
                    Width = 820,
                    Height = 600
                };
                windowTest.ShowAndWaitForContentRendered(window);
                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                TextBox configPathTextBox = FindDescendants<TextBox>(page).Single(textBox =>
                    textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(settings.LR2ConfigXmlPath));
                Assert.AreEqual(customConfig, configPathTextBox.Text);
                bool configPathNotified = false;
                settings.PropertyChanged += (_, args) => configPathNotified |= args.PropertyName == nameof(settings.LR2ConfigXmlPath);

                settings.SetRootFolderPathFromPicker(nameof(settings.LR2RootPath), root);
                PumpDispatcher(window.Dispatcher);

                Assert.IsTrue(configPathNotified);
                Assert.AreEqual(standardConfig, settings.LR2ConfigXmlPath);
                Assert.AreEqual(standardConfig, configPathTextBox.Text);
            }
            finally
            {
                settings?.ResetSettings();
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    [TestMethod]
    public void SettingsField_RepresentativeEditorsExposeLocalizedAutomationNamesOnAllFivePages()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
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
                var assertions = new List<(int Page, Func<FrameworkElement, Control> Find, string Name)>
                {
                    (0, page => FindDescendants<ComboBox>(page).Single(control => control.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path?.Path == "Language"), Resources.Language),
                    (1, page => FindDescendants<Slider>(page).Single(control => control.GetBindingExpression(RangeBase.ValueProperty)?.ParentBinding.Path?.Path == "CustomTableFontSize"), Resources.Appearance_table_font_size),
                    (2, page => FindDescendants<ComboBox>(page).Single(control => control.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path?.Path == "LR2bodyResolution"), Resources.Player_LR2_wsize),
                    (3, page => FindDescendants<ComboBox>(page).Single(control => control.GetBindingExpression(Selector.SelectedIndexProperty)?.ParentBinding.Path?.Path == "PlayerDriverIndex"), Resources.Device_setting_driver),
                    (4, page => (ComboBox)((FrameworkElement)page).FindName("comboBoxEncoder"), Resources.Record_setting_filetype)
                };
                foreach ((int pageIndex, Func<FrameworkElement, Control> find, string expectedName) in assertions)
                {
                    navigation.SelectedIndex = pageIndex;
                    PumpDispatcher(window.Dispatcher);
                    Control editor = find((FrameworkElement)host.Content);
                    AutomationPeer peer = editor switch
                    {
                        ComboBox comboBox => new ComboBoxAutomationPeer(comboBox),
                        Slider slider => new SliderAutomationPeer(slider),
                        _ => throw new AssertFailedException("Unsupported representative editor: " + editor.GetType().Name)
                    };
                    Assert.AreEqual(expectedName, peer.GetName(), "Page index " + pageIndex);
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

    [TestMethod]
    public void SettingsField_UnnamedEditorFallbackTracksHeaderChanges()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { Header = "Initial localized label", Content = editor };
            var peer = new ComboBoxAutomationPeer(editor);

            Assert.AreEqual("Initial localized label", peer.GetName());

            field.Header = "Updated localized label";

            Assert.AreEqual("Updated localized label", peer.GetName());
        });
    }

    [TestMethod]
    public void SettingsField_ExplicitAndBoundEditorNamesAreNotClobbered()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var explicitEditor = new ComboBox();
            AutomationProperties.SetName(explicitEditor, "Explicit localized name");
            var explicitField = new SettingsField { Header = "Field fallback", Content = explicitEditor };
            var explicitPeer = new ComboBoxAutomationPeer(explicitEditor);

            var boundEditor = new ComboBox();
            BindingOperations.SetBinding(
                boundEditor,
                AutomationProperties.NameProperty,
                new Binding { Source = "Bound localized name" });
            var boundField = new SettingsField { Header = "Field fallback", Content = boundEditor };
            var boundPeer = new ComboBoxAutomationPeer(boundEditor);

            explicitField.Header = "Changed fallback";
            boundField.Header = "Changed fallback";

            Assert.AreEqual("Explicit localized name", explicitPeer.GetName());
            Assert.AreEqual("Bound localized name", boundPeer.GetName());
            Assert.IsNotNull(BindingOperations.GetBindingExpression(boundEditor, AutomationProperties.NameProperty));
        });
    }

    [TestMethod]
    public void SettingsField_CallerLocalTakeoverMatchingFallbackSurvivesHeaderChangeAndDetach()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { Header = "A", Content = editor };
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

    [TestMethod]
    public void SettingsField_CallerBindingTakeoverMatchingFallbackSurvivesHeaderChangeAndDetach()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { Header = "A", Content = editor };
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

    [TestMethod]
    public void SettingsField_CallerLocalTakeoverMatchingFallbackSurvivesImmediateDetach()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { Header = "A", Content = editor };
            Assert.IsNotNull(BindingOperations.GetBindingExpressionBase(editor, AutomationProperties.NameProperty));

            AutomationProperties.SetName(editor, "A");
            field.Content = null;

            Assert.AreEqual("A", editor.ReadLocalValue(AutomationProperties.NameProperty));
            Assert.AreEqual("A", AutomationProperties.GetName(editor));
        });
    }

    [TestMethod]
    public void SettingsField_CallerBindingTakeoverMatchingFallbackSurvivesImmediateDetach()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var editor = new ComboBox();
            var field = new SettingsField { Header = "A", Content = editor };
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

    [DataTestMethod]
    [DataRow("Done")]
    [DataRow("Cancel")]
    [DataRow("NativeClose")]
    public void SettingsWindow_Lr2AdvancedRouteUsesOwnedModalRealBrowseAndTerminalButtons(string closeMode)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingsAdvancedRoute_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            SettingsWindow window = null;
            SettingsDialogViewModel settings = null;
            try
            {
                string root = Path.Combine(scope, "root");
                string originalSong = Path.Combine(root, "LR2files", "Database", "song.db");
                string originalConfig = Path.Combine(root, "LR2files", "Config", "config.xml");
                string initialCustomSong = Path.Combine(scope, "initial", "songs.db");
                string typedSong = Path.Combine(scope, "typed", "songs.db");
                string typedConfig = Path.Combine(scope, "typed", "config.xml");
                string pickedSong = Path.Combine(scope, "picked", "songs.db");
                string pickedConfig = Path.Combine(scope, "picked", "config.xml");
                foreach (string directory in new[] { Path.GetDirectoryName(originalSong)!, Path.GetDirectoryName(originalConfig)!, Path.GetDirectoryName(initialCustomSong)!, Path.GetDirectoryName(typedSong)!, Path.GetDirectoryName(pickedSong)! })
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(originalSong, []);
                File.WriteAllText(originalConfig, "<config><system /><jukebox /></config>");
                File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
                File.WriteAllBytes(initialCustomSong, []);
                File.WriteAllBytes(typedSong, []);
                File.WriteAllText(typedConfig, "<config><system /><jukebox /></config>");
                File.WriteAllBytes(pickedSong, []);
                File.WriteAllText(pickedConfig, "<config><system /><jukebox /></config>");

                MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
                settings = mainViewModel.SettingDialog;
                settings.LR2RootPath = root;
                settings.LR2SongDBPath = initialCustomSong;
                settings.LR2ConfigXmlPath = originalConfig;
                string draftSongBeforeDialog = settings.LR2SongDBPath;
                string draftConfigBeforeDialog = settings.LR2ConfigXmlPath;
                var dialogs = new RecordingSettingsWindowDialogService(
                    windowTest,
                    settings,
                    draftSongBeforeDialog,
                    draftConfigBeforeDialog,
                    typedSong,
                    typedConfig,
                    pickedSong,
                    pickedConfig,
                    closeMode);
                window = new SettingsWindow(dialogs)
                {
                    DataContext = settings,
                    Width = 820,
                    Height = 600
                };
                dialogs.ExpectedOwner = window;
                windowTest.ShowAndWaitForContentRendered(window);
                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                ((Button)page.FindName("buttonEditCustomLr2Paths")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window.Dispatcher);

                Assert.AreSame(window, dialogs.LastModalOwner);
                Assert.IsFalse(dialogs.LastModalShowInTaskbar);
                Assert.AreEqual(2, dialogs.FileRequests.Count);
                Assert.IsTrue(dialogs.FileRequests.All(request => ReferenceEquals(window, request.Owner)));
                Assert.AreEqual(Path.GetDirectoryName(typedSong), dialogs.FileRequests[0].InitialDirectory);
                Assert.AreEqual(Path.GetDirectoryName(typedConfig), dialogs.FileRequests[1].InitialDirectory);
                StringAssert.Contains(dialogs.FileRequests[0].Filter, "*.db");
                StringAssert.Contains(dialogs.FileRequests[1].Filter, "config.xm?");
                if (closeMode == "Done")
                {
                    Assert.AreEqual(pickedSong, settings.LR2SongDBPath);
                    Assert.AreEqual(pickedConfig, settings.LR2ConfigXmlPath);
                }
                else
                {
                    Assert.AreEqual(draftSongBeforeDialog, settings.LR2SongDBPath);
                    Assert.AreEqual(draftConfigBeforeDialog, settings.LR2ConfigXmlPath);
                }
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                settings?.ResetSettings();
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    [DataTestMethod]
    [DataRow("Failed", "Accepted", 1)]
    [DataRow("OwnerUnavailable", "Failed", 2)]
    public void SettingsWindow_Lr2AdvancedClickReportsRouteFailureOnceWithoutUnhandledException(
        string windowStatusName,
        string notificationStatusName,
        int expectedLogCount)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            UiDialogStatus windowStatus = Enum.Parse<UiDialogStatus>(windowStatusName);
            UiDialogStatus notificationStatus = Enum.Parse<UiDialogStatus>(notificationStatusName);
            LoggingConfiguration originalConfiguration = LogManager.Configuration;
            var logTarget = new MemoryTarget { Layout = "${message}|${exception:format=message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Error, LogLevel.Fatal, logTarget);
            LogManager.Configuration = configuration;
            SettingsWindow window = null;
            try
            {
                MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
                var dialogs = new FailingLr2AdvancedPathsDialogService(windowStatus, notificationStatus);
                window = new SettingsWindow(dialogs)
                {
                    DataContext = mainViewModel.SettingDialog,
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
                    ((Button)page.FindName("buttonEditCustomLr2Paths")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpDispatcher(window.Dispatcher);
                }
                finally
                {
                    window.Dispatcher.UnhandledException -= handler;
                }

                LogManager.Flush();
                Assert.AreEqual(1, dialogs.WindowRequestCount);
                Assert.AreEqual(1, dialogs.MessageRequestCount, "Failure notification must not recurse.");
                Assert.AreSame(window, dialogs.LastMessageRequest.Owner);
                Assert.AreEqual(Resources.Msg_error_unexpected, dialogs.LastMessageRequest.MessageBoxText);
                Assert.IsFalse(
                    dialogs.LastMessageRequest.MessageBoxText.Contains("LR2 advanced paths dialog", StringComparison.Ordinal),
                    "Internal route names must remain in diagnostics instead of leaking into localized UI text.");
                Assert.IsFalse(
                    dialogs.LastMessageRequest.MessageBoxText.Contains(notificationStatus.ToString(), StringComparison.Ordinal),
                    "Internal dialog status values must remain in diagnostics instead of leaking into localized UI text.");
                Assert.AreEqual(0, unhandled.Count);
                Assert.AreEqual(expectedLogCount, logTarget.Logs.Count);
                StringAssert.Contains(logTarget.Logs[0], "LR2 advanced paths dialog failed");
                if (notificationStatus == UiDialogStatus.Failed)
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

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SettingsWindow_Lr2AdvancedRejectedOrCancelledBrowseKeepsTypedLocalDraftAndParentTuple(bool cancelPicker)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingsAdvancedMalformed_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            SettingsWindow window = null;
            SettingsDialogViewModel settings = null;
            try
            {
                string root = Path.Combine(scope, "root");
                string originalSong = Path.Combine(root, "LR2files", "Database", "song.db");
                string originalConfig = Path.Combine(root, "LR2files", "Config", "config.xml");
                string typedSong = Path.Combine(scope, "typed", "song.db");
                string typedConfig = Path.Combine(scope, "typed", "config.xml");
                string pickedSong = Path.Combine(scope, "picked", "song.db");
                string malformedConfig = Path.Combine(scope, "picked", "config.xml");
                foreach (string directory in new[] { Path.GetDirectoryName(originalSong)!, Path.GetDirectoryName(originalConfig)!, Path.GetDirectoryName(typedSong)!, Path.GetDirectoryName(pickedSong)! })
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(originalSong, []);
                File.WriteAllBytes(typedSong, []);
                File.WriteAllBytes(pickedSong, []);
                File.WriteAllText(originalConfig, "<config><system /><jukebox /></config>");
                File.WriteAllText(typedConfig, "<config><system /><jukebox /></config>");
                File.WriteAllText(malformedConfig, "<config>");

                settings = MainWindowViewModelTestFactory.Create().SettingDialog;
                settings.LR2RootPath = root;
                settings.LR2SongDBPath = originalSong;
                settings.LR2ConfigXmlPath = originalConfig;
                var dialogs = new RecordingSettingsWindowDialogService(
                    windowTest,
                    settings,
                    originalSong,
                    originalConfig,
                    typedSong,
                    typedConfig,
                    pickedSong,
                    malformedConfig,
                    "Cancel",
                    expectConfigAccepted: false,
                    configPickerStatus: cancelPicker ? UiDialogStatus.CancelledByUser : UiDialogStatus.Accepted);
                window = new SettingsWindow(dialogs)
                {
                    DataContext = settings,
                    Width = 820,
                    Height = 600
                };
                dialogs.ExpectedOwner = window;
                windowTest.ShowAndWaitForContentRendered(window);
                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                ((Button)page.FindName("buttonEditCustomLr2Paths")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window.Dispatcher);

                Assert.AreEqual(originalSong, settings.LR2SongDBPath);
                Assert.AreEqual(originalConfig, settings.LR2ConfigXmlPath);
                Assert.AreEqual(
                    cancelPicker ? string.Empty : Resources.Error_InvalidLR2SongDbOrConfigPath,
                    dialogs.ValidationErrorAfterConfigBrowse);
                Assert.AreEqual(typedConfig, dialogs.ConfigPathAfterBrowse);
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                settings?.ResetSettings();
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_OwnerShutdownBeforeContentRenderedCannotReactivateDetachedPresentation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel mainViewModel = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel settings = mainViewModel.SettingDialog;
            var window = new SettingsWindow
            {
                DataContext = settings,
                Width = 820,
                Height = 600
            };
            var dispatcherFailures = new List<Exception>();
            bool contentRendered = false;
            DispatcherUnhandledExceptionEventHandler unhandledHandler = (_, args) =>
            {
                dispatcherFailures.Add(args.Exception);
                args.Handled = true;
            };
            EventHandler contentRenderedHandler = (_, _) => contentRendered = true;
            window.Dispatcher.UnhandledException += unhandledHandler;
            window.ContentRendered += contentRenderedHandler;
            try
            {
                windowTest.PrepareForOwnedPresentation(window);
                window.Show();
                Assert.IsFalse(contentRendered,
                    "Show must return before the queued ContentRendered callback in this lifecycle scenario.");

                window.CloseForOwnerShutdown();
                Assert.AreEqual(SettingsWindowCloseReason.OwnerShutdown, window.CloseReason);
                Assert.IsNull(window.DataContext, "Owner shutdown must detach the shared settings DataContext.");
                Assert.IsFalse(GetPresentationActive(settings));

                PumpDispatcher(window.Dispatcher);

                Assert.IsTrue(contentRendered,
                    "The dispatcher barrier must observe the delayed ContentRendered callback after close.");
                Assert.AreEqual(0, dispatcherFailures.Count,
                    "A delayed ContentRendered callback must not escape through Dispatcher.UnhandledException.");
                Assert.IsNull(window.DataContext);
                Assert.IsFalse(GetPresentationActive(settings),
                    "A closed settings window must not reactivate the shared presentation.");
                Assert.IsFalse(GetWindowPresentationActivated(window));
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

    [TestMethod]
    public void SettingsWindow_KeepsNavigationHeaderAndFooterOutsideSinglePageScrollViewer()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement pageScroller = FindNamedElement(document, "settingsPageScrollViewer");
        XElement saveButton = FindNamedElement(document, "buttonOK");
        XElement cancelButton = FindNamedElement(document, "buttonCancel");

        Assert.IsFalse(document.Descendants(PresentationName("TabControl")).Any());
        Assert.IsFalse(document.Descendants(PresentationName("TabItem")).Any());
        Assert.AreEqual("Auto", pageScroller.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.AreEqual("Disabled", pageScroller.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.IsFalse(saveButton.Ancestors(PresentationName("ScrollViewer")).Any());
        Assert.IsFalse(cancelButton.Ancestors(PresentationName("ScrollViewer")).Any());
        Assert.IsFalse(FindNamedElement(document, "settingsNavigation").Ancestors(PresentationName("ScrollViewer")).Any());

        XDocument aboutDocument = LoadSettingsPageXamls()
            .Single(page => page.Root?.Attribute(XamlName("Class"))?.Value.EndsWith(".AboutSettingsPage", StringComparison.Ordinal) == true);
        Assert.IsFalse(aboutDocument.Descendants(PresentationName("FlowDocumentScrollViewer")).Any());

        XDocument releaseNotesDocument = XDocument.Load(Path.Combine(
            FindRepositoryRoot(), "BeMusicSeeker", "Views", "ReleaseNotesWindow.xaml"));
        XElement versionDocument = releaseNotesDocument.Descendants(PresentationName("FlowDocumentScrollViewer")).Single();
        Assert.IsNull(versionDocument.Attribute("Height"));
        Assert.AreEqual("Auto", versionDocument.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.AreEqual("Disabled", versionDocument.Attribute("HorizontalScrollBarVisibility")?.Value);
    }

    [TestMethod]
    public void SettingsWindow_UsesClosedExplicitControlSystem()
    {
        XDocument windowDocument = LoadSettingsWindowXaml();
        XDocument controlsDocument = LoadSettingsControlsXaml();
        XElement operationGrid = FindNamedElement(windowDocument, "settingDialogOperationGrid");
        XElement navigation = FindNamedElement(windowDocument, "settingsNavigation");
        XElement header = windowDocument.Descendants(PresentationName("ContentControl"))
            .Single(element => element.Attribute("Content")?.Value.Contains("SelectedItem.Content", StringComparison.Ordinal) == true);
        XElement saveButton = FindNamedElement(windowDocument, "buttonOK");
        XElement cancelButton = FindNamedElement(windowDocument, "buttonCancel");

        XElement mergedDictionary = windowDocument
            .Descendants(PresentationName("ResourceDictionary"))
            .Single(dictionary => dictionary.Attribute("Source")?.Value == "Settings/SettingsControls.xaml");
        Assert.IsNotNull(mergedDictionary);
        Assert.IsFalse(operationGrid.Elements(PresentationName("FrameworkElement.Resources")).Any(),
            "Settings-wide control styles must live in the window-local merged dictionary.");
        Assert.AreEqual("0,16", navigation.Attribute("Padding")?.Value);
        Assert.AreEqual("26", header.Attribute("FontSize")?.Value);
        Assert.AreEqual("{StaticResource SettingsPrimaryButtonStyle}", saveButton.Attribute("Style")?.Value);
        Assert.AreEqual("{StaticResource SettingsQuietButtonStyle}", cancelButton.Attribute("Style")?.Value);

        foreach (string styleKey in new[]
        {
            "SettingsButtonStyle",
            "SettingsPrimaryButtonStyle",
            "SettingsQuietButtonStyle",
            "SettingsDangerButtonStyle",
            "SettingsIconButtonStyle",
            "settingsNavigationItemStyle"
        })
        {
            Assert.IsNotNull(FindKeyedStyle(controlsDocument, styleKey), styleKey);
        }

        XElement primaryStyle = FindKeyedStyle(controlsDocument, "SettingsPrimaryButtonStyle");
        AssertStyleSetter(primaryStyle, "Background", "{DynamicResource App.AccentBrush}");
        AssertStyleSetter(primaryStyle, "Foreground", "{DynamicResource App.AccentForegroundBrush}");
        AssertStyleTriggerSetter(primaryStyle, "IsMouseOver", "Background", "{DynamicResource App.AccentHoverBrush}");
        AssertStyleTriggerSetter(primaryStyle, "IsPressed", "Background", "{DynamicResource App.AccentPressedBrush}");
        AssertStyleTriggerSetter(primaryStyle, "IsEnabled", "Foreground", "{DynamicResource App.AccentForegroundBrush}");

        string[] explicitTemplateTargets =
        [
            "{x:Type Button}",
            "{x:Type TextBox}",
            "{x:Type ComboBox}",
            "{x:Type CheckBox}",
            "{x:Type RadioButton}",
            "{x:Type Slider}",
            "{x:Type ScrollBar}",
            "{x:Type ListBoxItem}"
        ];
        foreach (string targetType in explicitTemplateTargets)
        {
            Assert.IsTrue(controlsDocument.Descendants(PresentationName("ControlTemplate"))
                .Any(template => template.Attribute("TargetType")?.Value == targetType), targetType);
        }

        CollectionAssert.IsSubsetOf(
            new[] { "PART_ContentHost", "PART_Popup", "PART_Track" },
            controlsDocument.Descendants()
                .Select(element => element.Attribute(XamlName("Name"))?.Value)
                .Where(name => name != null)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        foreach (string stateProperty in new[] { "IsKeyboardFocused", "IsKeyboardFocusWithin", "IsEnabled" })
        {
            Assert.IsTrue(controlsDocument.Descendants(PresentationName("Trigger"))
                .Any(trigger => trigger.Attribute("Property")?.Value == stateProperty), stateProperty);
        }

        string settingsVisualText = windowDocument.ToString(SaveOptions.DisableFormatting)
            + controlsDocument.ToString(SaveOptions.DisableFormatting);
        foreach (string legacyKey in new[]
        {
            "NormalBrush",
            "MouseOverBrush",
            "PressedBrush",
            "PressedBorderBrush",
            "DefaultedBorderBrush",
            "Table.CurrentCellTextBrush"
        })
        {
            Assert.IsFalse(settingsVisualText.Contains("{DynamicResource " + legacyKey + "}", StringComparison.Ordinal), legacyKey);
        }
        Assert.IsFalse(settingsVisualText.Contains("DarkOrange", StringComparison.Ordinal));
        Assert.IsFalse(settingsVisualText.Contains("Color=\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window window = null;
            try
            {
                Grid host = CreateSettingsControlHost();
                var panel = new StackPanel { Width = 300 };
                var selectionCombo = new ComboBox
                {
                    ItemsSource = new[] { "First", "Second" },
                    SelectedIndex = 0,
                    Width = 300
                };
                var editableCombo = new ComboBox
                {
                    ItemsSource = new[] { "First", "Second" },
                    IsEditable = true,
                    Text = "First",
                    Width = 300,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                panel.Children.Add(selectionCombo);
                panel.Children.Add(editableCombo);
                host.Children.Add(panel);
                window = new Window
                {
                    Content = host,
                    Width = 360,
                    Height = 160
                };
                windowTest.ShowAndWaitForContentRendered(
                    window,
                    TestWindowActivation.ForegroundInteraction);

                selectionCombo.ApplyTemplate();
                editableCombo.ApplyTemplate();
                var selectionToggle = (ToggleButton)selectionCombo.Template.FindName("DropDownToggle", selectionCombo);
                var editableToggle = (ToggleButton)editableCombo.Template.FindName("DropDownToggle", editableCombo);
                var editor = (TextBox)editableCombo.Template.FindName("PART_EditableTextBox", editableCombo);
                var selectionPopup = (Popup)selectionCombo.Template.FindName("PART_Popup", selectionCombo);
                var editablePopup = (Popup)editableCombo.Template.FindName("PART_Popup", editableCombo);
                windowTest.TrackPopup(selectionPopup);
                windowTest.TrackPopup(editablePopup);

                foreach (double x in new[] { 6d, selectionCombo.ActualWidth / 2d, selectionCombo.ActualWidth - 6d })
                {
                    IInputElement hit = selectionCombo.InputHitTest(new Point(x, selectionCombo.ActualHeight / 2d));
                    Assert.IsTrue(IsVisualDescendantOf(hit as DependencyObject, selectionToggle),
                        $"The noneditable ComboBox hit at x={x} did not route to the full-span dropdown toggle.");
                }

                IInputElement editableTextHit = editableCombo.InputHitTest(new Point(12, editableCombo.ActualHeight / 2d));
                IInputElement editableArrowHit = editableCombo.InputHitTest(new Point(editableCombo.ActualWidth - 6d, editableCombo.ActualHeight / 2d));
                Assert.IsTrue(IsVisualDescendantOf(editableTextHit as DependencyObject, editor));
                Assert.IsTrue(IsVisualDescendantOf(editableArrowHit as DependencyObject, editableToggle));
                Assert.AreSame(editor, editableCombo.Template.FindName("PART_EditableTextBox", editableCombo));
                Assert.AreSame(editablePopup, editableCombo.Template.FindName("PART_Popup", editableCombo));

                editor.Focus();
                editor.CaretIndex = editor.Text.Length;
                var composition = new TextComposition(InputManager.Current, editor, "Z");
                editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                {
                    RoutedEvent = TextCompositionManager.TextInputEvent
                });
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(editor.IsKeyboardFocused);
                Assert.AreEqual("FirstZ", editor.Text);
                Assert.AreEqual(editor.Text.Length, editor.CaretIndex);

                var selectionPeer = new ComboBoxAutomationPeer(selectionCombo);
                var selectionProvider = (IExpandCollapseProvider)selectionPeer.GetPattern(PatternInterface.ExpandCollapse);
                Assert.IsNotNull(selectionProvider);
                selectionProvider.Expand();
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(selectionPopup.IsOpen);
                RaiseKey(selectionCombo, Key.Down);
                RaiseKey(selectionCombo, Key.Enter);
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(1, selectionCombo.SelectedIndex);
                Assert.IsFalse(selectionPopup.IsOpen);

                var editablePeer = new ComboBoxAutomationPeer(editableCombo);
                var editableProvider = (IExpandCollapseProvider)editablePeer.GetPattern(PatternInterface.ExpandCollapse);
                Assert.IsNotNull(editableProvider);
                editableProvider.Expand();
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(editablePopup.IsOpen);
                editableProvider.Collapse();
                PumpDispatcher(window.Dispatcher);
                Assert.IsFalse(editablePopup.IsOpen);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [TestMethod]
    public void SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            const string sentinel = "OuterImplicitStyleSentinel";
            Application application = Application.Current;
            Assert.IsNotNull(application);
            Type[] sentinelTypes =
            [
                typeof(ScrollViewer),
                typeof(ScrollBar),
                typeof(ComboBoxItem),
                typeof(ListBox),
                typeof(ListBoxItem),
                typeof(Expander)
            ];
            var previousResources = new Dictionary<Type, object>();
            foreach (Type type in sentinelTypes)
            {
                if (application.Resources.Contains(type))
                {
                    previousResources[type] = application.Resources[type];
                }

                var sentinelStyle = new Style(type);
                sentinelStyle.Setters.Add(new Setter(FrameworkElement.TagProperty, sentinel));
                application.Resources[type] = sentinelStyle;
            }

            Window window = null;
            try
            {
                Grid host = CreateSettingsControlHost();
                Style scrollViewerStyle = (Style)host.Resources["SettingsScrollViewerStyle"];
                Style scrollBarStyle = (Style)host.Resources["SettingsScrollBarStyle"];
                Style comboBoxItemStyle = (Style)host.Resources["SettingsComboBoxItemStyle"];
                Style listBoxStyle = (Style)host.Resources["SettingsListBoxStyle"];
                Style listBoxItemStyle = (Style)host.Resources["SettingsListBoxItemStyle"];
                Style expanderStyle = (Style)host.Resources["SettingsExpanderStyle"];

                var pageScroller = new ScrollViewer
                {
                    Content = new Border { Width = 800, Height = 800 },
                    Width = 180,
                    Height = 100,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Visible
                };
                var textBox = new TextBox { Text = "settings" };
                var comboBox = new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
                var listBox = new ListBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 1, Height = 64 };
                var expander = new Expander { Header = "Details", Content = new TextBlock { Text = "Content" } };
                var slider = new Slider { Minimum = 0, Maximum = 10, TickFrequency = 1, TickPlacement = TickPlacement.TopLeft, Width = 180 };
                var primaryButton = new Button { Content = "Save", Style = (Style)host.Resources["SettingsPrimaryButtonStyle"] };
                var panel = new StackPanel();
                panel.Children.Add(pageScroller);
                panel.Children.Add(textBox);
                panel.Children.Add(comboBox);
                panel.Children.Add(listBox);
                panel.Children.Add(expander);
                panel.Children.Add(slider);
                panel.Children.Add(primaryButton);
                host.Children.Add(panel);

                window = new Window
                {
                    Content = host,
                    Width = 420,
                    Height = 520
                };
                windowTest.ShowAndWaitForContentRendered(
                    window,
                    TestWindowActivation.ForegroundInteraction);

                AssertLocalImplicitStyle(pageScroller.Style, scrollViewerStyle, nameof(ScrollViewer));
                AssertLocalImplicitStyle(listBox.Style, listBoxStyle, nameof(ListBox));
                Assert.AreSame(listBoxItemStyle, listBox.ItemContainerStyle);
                Assert.AreSame(comboBoxItemStyle, comboBox.ItemContainerStyle);
                AssertLocalImplicitStyle(expander.Style, expanderStyle, nameof(Expander));
                foreach (FrameworkElement element in new FrameworkElement[] { pageScroller, comboBox, listBox, expander })
                {
                    Assert.AreNotEqual(sentinel, element.Tag, element.GetType().Name);
                }

                pageScroller.ApplyTemplate();
                var verticalScrollBar = (ScrollBar)pageScroller.Template.FindName("PART_VerticalScrollBar", pageScroller);
                Assert.IsNotNull(pageScroller.Template.FindName("PART_ScrollContentPresenter", pageScroller));
                Assert.AreSame(scrollBarStyle, verticalScrollBar.Style);
                Assert.AreNotEqual(sentinel, verticalScrollBar.Tag);
                verticalScrollBar.ApplyTemplate();
                Assert.IsNotNull(verticalScrollBar.Template.FindName("PART_Track", verticalScrollBar));
                var scrollPeer = new ScrollViewerAutomationPeer(pageScroller);
                var scrollProvider = (IScrollProvider)scrollPeer.GetPattern(PatternInterface.Scroll);
                Assert.IsNotNull(scrollProvider);
                Assert.IsTrue(scrollProvider.VerticallyScrollable);
                double initialOffset = pageScroller.VerticalOffset;
                scrollProvider.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(pageScroller.VerticalOffset > initialOffset);
                Assert.AreEqual(pageScroller.VerticalOffset, verticalScrollBar.Value, 0.01d);

                textBox.ApplyTemplate();
                var textContentHost = (ScrollViewer)textBox.Template.FindName("PART_ContentHost", textBox);
                Assert.AreSame(scrollViewerStyle, textContentHost.Style);
                Assert.AreNotEqual(sentinel, textContentHost.Tag);

                comboBox.ApplyTemplate();
                var popup = (Popup)comboBox.Template.FindName("PART_Popup", comboBox);
                windowTest.TrackPopup(popup);
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(popup.IsOpen);
                Assert.IsTrue(comboBox.IsDropDownOpen);
                ScrollViewer popupScroller = FindDescendant<ScrollViewer>(popup.Child);
                Assert.IsNotNull(popupScroller);
                Assert.AreSame(scrollViewerStyle, popupScroller.Style);
                var firstComboItem = (ComboBoxItem)comboBox.ItemContainerGenerator.ContainerFromIndex(0);
                var secondComboItem = (ComboBoxItem)comboBox.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.IsNotNull(firstComboItem);
                Assert.IsNotNull(secondComboItem);
                Assert.AreSame(comboBoxItemStyle, firstComboItem.Style);
                Assert.AreSame(comboBoxItemStyle, secondComboItem.Style);
                Assert.AreNotEqual(sentinel, firstComboItem.Tag);
                firstComboItem.ApplyTemplate();
                Assert.IsNotNull(firstComboItem.Template.FindName("ItemChrome", firstComboItem));

                var comboPeer = new ComboBoxAutomationPeer(comboBox);
                var comboExpandProvider = (IExpandCollapseProvider)comboPeer.GetPattern(PatternInterface.ExpandCollapse);
                Assert.IsNotNull(comboExpandProvider);
                Assert.AreEqual(ExpandCollapseState.Expanded, comboExpandProvider.ExpandCollapseState);
                RaiseKey(comboBox, Key.Down);
                PumpDispatcher(window.Dispatcher);
                RaiseKey(comboBox, Key.Enter);
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(1, comboBox.SelectedIndex);
                Assert.AreEqual("Second", comboBox.SelectedItem);
                Assert.IsTrue(secondComboItem.IsSelected);
                Assert.IsFalse(comboBox.IsDropDownOpen);
                Assert.IsFalse(popup.IsOpen);
                Assert.AreEqual(ExpandCollapseState.Collapsed, comboExpandProvider.ExpandCollapseState);

                listBox.ApplyTemplate();
                ScrollViewer listScroller = FindDescendant<ScrollViewer>(listBox);
                Assert.IsNotNull(listScroller);
                Assert.AreSame(scrollViewerStyle, listScroller.Style);
                Assert.AreEqual(1, listBox.SelectedIndex);
                var firstListItem = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0);
                var secondListItem = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.IsNotNull(firstListItem);
                Assert.IsNotNull(secondListItem);
                Assert.AreSame(listBoxItemStyle, firstListItem.Style);
                Assert.AreSame(listBoxItemStyle, secondListItem.Style);
                Assert.AreNotEqual(sentinel, secondListItem.Tag);
                Assert.IsTrue(secondListItem.IsSelected);
                secondListItem.ApplyTemplate();
                var selectedListChrome = (Border)secondListItem.Template.FindName("ItemChrome", secondListItem);
                AssertBrushColor(host, "App.ControlSelectedBrush", selectedListChrome.Background);

                var listPeer = new ListBoxAutomationPeer(listBox);
                var selectionProvider = (ISelectionProvider)listPeer.GetPattern(PatternInterface.Selection);
                Assert.IsNotNull(selectionProvider);
                Assert.IsFalse(selectionProvider.CanSelectMultiple);
                Assert.AreEqual(1, selectionProvider.GetSelection().Length);
                AutomationPeer firstListItemPeer = listPeer.GetChildren().Single(peer => peer.GetName() == "First");
                var selectionItemProvider = (ISelectionItemProvider)firstListItemPeer.GetPattern(PatternInterface.SelectionItem);
                Assert.IsNotNull(selectionItemProvider);
                Assert.IsFalse(selectionItemProvider.IsSelected);
                selectionItemProvider.Select();
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(0, listBox.SelectedIndex);
                Assert.IsTrue(firstListItem.IsSelected);
                Assert.IsFalse(secondListItem.IsSelected);
                Assert.IsTrue(selectionItemProvider.IsSelected);

                expander.ApplyTemplate();
                var headerSite = (ToggleButton)expander.Template.FindName("HeaderSite", expander);
                headerSite.IsChecked = true;
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Assert.IsTrue(expander.IsExpanded);
                var expanderPeer = new ExpanderAutomationPeer(expander);
                var expanderProvider = (IExpandCollapseProvider)expanderPeer.GetPattern(PatternInterface.ExpandCollapse);
                Assert.IsNotNull(expanderProvider);
                Assert.AreEqual(ExpandCollapseState.Expanded, expanderProvider.ExpandCollapseState);
                expanderProvider.Collapse();
                PumpDispatcher(window.Dispatcher);
                Assert.IsFalse(expander.IsExpanded);
                Assert.AreEqual(ExpandCollapseState.Collapsed, expanderProvider.ExpandCollapseState);
                expanderProvider.Expand();
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(expander.IsExpanded);

                slider.ApplyTemplate();
                var topTickBar = (TickBar)slider.Template.FindName("TopTickBar", slider);
                var bottomTickBar = (TickBar)slider.Template.FindName("BottomTickBar", slider);
                Assert.AreEqual(Visibility.Visible, topTickBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, bottomTickBar.Visibility);
                Assert.IsNotNull(slider.Template.FindName("PART_Track", slider));
                AssertBrushColor(host, "App.SliderTickBrush", topTickBar.Fill);
                slider.TickPlacement = TickPlacement.Both;
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Assert.AreEqual(Visibility.Visible, topTickBar.Visibility);
                Assert.AreEqual(Visibility.Visible, bottomTickBar.Visibility);

                primaryButton.Focus();
                window.Dispatcher.Invoke(DispatcherPriority.Input, new Action(() => { }));
                var focusBorder = (Border)primaryButton.Template.FindName("FocusBorder", primaryButton);
                Assert.AreEqual(Visibility.Visible, focusBorder.Visibility);
                AssertBrushColor(host, "App.AccentFocusRingBrush", focusBorder.BorderBrush);
                Color focusColor = ((SolidColorBrush)focusBorder.BorderBrush).Color;
                foreach (string accentState in new[] { "App.AccentBrush", "App.AccentHoverBrush", "App.AccentPressedBrush" })
                {
                    Color stateColor = ((SolidColorBrush)host.TryFindResource(accentState)).Color;
                    Assert.IsTrue(ContrastRatio(focusColor, stateColor) >= 3d, accentState);
                }

                primaryButton.IsEnabled = false;
                AssertBrushColor(host, "App.AccentBrush", primaryButton.Background);
                AssertBrushColor(host, "App.AccentForegroundBrush", primaryButton.Foreground);
            }
            finally
            {
                window?.Close();
                foreach (Type type in sentinelTypes)
                {
                    application.Resources.Remove(type);
                    if (previousResources.TryGetValue(type, out object previous))
                    {
                        application.Resources[type] = previous;
                    }
                }

            }
        });
    }

    private static void AssertLocalImplicitStyle(Style actualStyle, Style localBaseStyle, string controlName)
    {
        Assert.IsNotNull(actualStyle, controlName + " must resolve an implicit Settings-local style.");
        Assert.AreSame(localBaseStyle, actualStyle.BasedOn,
            controlName + " implicit style must be based on the closed Settings style, not an application resource.");
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
        values = new Settings
        {
            OperationModeLR2DB = true,
            LR2RootPath = scope,
            LR2ConfigXmlPath = configPath,
            LR2SongDBPath = Path.Combine(scope, "LR2files", "Database", "song.db")
        };
        return scope;
    }

    private static DangerDialogContext CreateDangerDialog(
        Settings values,
        DangerDialogService dialogs,
        DangerSchemaDialogPort schemaDialog,
        DangerApplicationDataStore store)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        var composition = new ApplicationComposition(
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        return CreateDangerDialog(
            owner,
            new SettingsEditSession(values),
            composition,
            dialogs,
            schemaDialog,
            store);
    }

    private static DangerDialogContext CreateDangerDialog(
        MainWindowViewModel owner,
        ISettingsEditSession settingsEditSession,
        ApplicationComposition composition,
        DangerDialogService dialogs,
        DangerSchemaDialogPort schemaDialog,
        DangerApplicationDataStore store)
    {
        typeof(MainWindowViewModel)
            .GetField("hasActiveLibraryProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, true);
        int reloadCount = 0;
        var statePort = new TestSettingsDialogStatePort(
            owner,
            () => Task.FromResult(true),
            reloadScoresOnly: () =>
            {
                reloadCount++;
                return Task.CompletedTask;
            });
        var workspace = new DangerWorkspacePort();
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
            schemaDialogs: dialogs,
            schemaWindowDialogs: schemaDialog,
            applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(dialogs, store),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
        return new DangerDialogContext(settings, playHistory, () => reloadCount);
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
        ((ListBox)window.FindName("settingsNavigation")).SelectedIndex = 8;
        PumpDispatcher(window.Dispatcher);
        var page = (AdvancedSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
        Button button = FindDescendants<Button>(page).Single(candidate => Equals(candidate.Content, content));
        Assert.IsTrue(button.IsEnabled, content + " button must be enabled for the test fixture.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static void PrepareSchemaDangerOperation(SettingsDialogViewModel settings, string scoreDbPath)
    {
        typeof(SettingsDialogViewModel)
            .GetField("operationModeLR2DB", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, true);
        typeof(SettingsDialogViewModel)
            .GetField("lr2PlayHistoryScoreDbPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, scoreDbPath);
        typeof(SettingsDialogViewModel)
            .GetMethod("RaiseLr2PlayHistorySchemaStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(settings, null);
        PumpDispatcher(Dispatcher.CurrentDispatcher);
    }

    private static void SetSchemaPresentationStatus(
        SettingsDialogViewModel settings,
        Lr2PlayHistorySchemaStatus status)
    {
        const string scoreDbPath = @"C:\fixture\score.db";
        typeof(SettingsDialogViewModel)
            .GetField("operationModeLR2DB", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, true);
        typeof(SettingsDialogViewModel)
            .GetField("lr2PlayHistoryScoreDbPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, scoreDbPath);
        typeof(SettingsDialogViewModel)
            .GetField("lr2PlayHistorySchemaStatusSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, Lr2PlayHistorySchemaStatusSnapshot.FromResult(new Lr2PlayHistorySchemaCheckResult
            {
                Status = status,
                ScoreDbPath = scoreDbPath,
                Message = status.ToString()
            }));
        typeof(SettingsDialogViewModel)
            .GetMethod("RaiseLr2PlayHistorySchemaStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(settings, null);
        Assert.AreEqual(
            status == Lr2PlayHistorySchemaStatus.Repairable,
            settings.CanInstallOrRepairLr2PlayHistorySchema);
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

    private static void PumpUntil(SettingsWindow window, Func<bool> predicate, string failureMessage)
        => PumpUntil(window.Dispatcher, predicate, failureMessage);

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> predicate, string failureMessage)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            PumpDispatcher(dispatcher);
        }
        Assert.IsTrue(predicate(), failureMessage);
    }

    private static void AssertInstalledSchema(string scoreDbPath, bool expectedInstalled)
    {
        using var database = new SQLiteConnection(scoreDbPath);
        int objectCount = database.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM sqlite_master WHERE name LIKE 'bms_lr2_%' OR name LIKE 'idx_bms_lr2_%';");
        Assert.AreEqual(expectedInstalled, objectCount > 0);
    }

    private static void AssertPlaylistUriValidationClearedOnReopen(PlaylistUriCompletionRoute completionRoute)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel settings = owner.SettingDialog;
            if (completionRoute == PlaylistUriCompletionRoute.SaveButton)
            {
                typeof(MainWindowViewModel)
                    .GetField("hasActiveLibraryProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(owner, true);
            }

            var presentationPort = new RecordingPresentationPort();
            settings.AttachPresentationPort(presentationPort);
            Uri tableListUriBefore = settings.TableListURL;
            string mappingUriBefore = settings.PlaylistMd5UrlMappingTsvUri;
            SettingsWindow firstPresentation = null;
            SettingsWindow reopenedPresentation = null;
            try
            {
                firstPresentation = new SettingsWindow { DataContext = settings };
                presentationPort.CloseAction = firstPresentation.CloseFromPresentation;
                windowTest.ShowAndWaitForContentRendered(firstPresentation);
                SeedTransientPlaylistUriValidation(settings);

                Assert.IsFalse(settings.HasPendingSettingChanges(),
                    "Invalid URI input must not mutate the settings draft.");
                var navigation = (ListBox)firstPresentation.FindName("settingsNavigation");
                navigation.SelectedIndex = 5;
                PumpDispatcher(firstPresentation.Dispatcher);
                navigation.SelectedIndex = 0;
                PumpDispatcher(firstPresentation.Dispatcher);
                navigation.SelectedIndex = 5;
                PumpDispatcher(firstPresentation.Dispatcher);
                PumpDispatcher(firstPresentation.Dispatcher);
                Assert.IsFalse(string.IsNullOrWhiteSpace(settings.TableListUriValidationMessage));
                Assert.IsFalse(string.IsNullOrWhiteSpace(settings.PlaylistMd5UrlMappingTsvUriValidationMessage));

                switch (completionRoute)
                {
                    case PlaylistUriCompletionRoute.CancelButton:
                        ((Button)firstPresentation.FindName("buttonCancel"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        break;
                    case PlaylistUriCompletionRoute.SaveButton:
                        ((Button)firstPresentation.FindName("buttonOK"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        break;
                    case PlaylistUriCompletionRoute.NativeClose:
                        firstPresentation.Close();
                        break;
                    default:
                        Assert.Fail("Unsupported URI completion route: " + completionRoute);
                        break;
                }
                PumpUntil(
                    firstPresentation,
                    () => !firstPresentation.IsVisible,
                    completionRoute + " did not close the actual SettingsWindow route.");
                Assert.IsFalse(settings.IsEditCompletionInProgress);
                Assert.IsFalse(settings.HasPendingSettingChanges());
                Assert.AreEqual(1, presentationPort.CloseRequestCount);
                Assert.AreEqual(
                    completionRoute == PlaylistUriCompletionRoute.SaveButton
                        ? SettingsWindowCloseReason.Apply
                        : SettingsWindowCloseReason.Cancel,
                    firstPresentation.CloseReason);
                Assert.IsFalse(GetPresentationActive(settings));
                Assert.IsNull(firstPresentation.DataContext);
                Assert.IsFalse(string.IsNullOrWhiteSpace(settings.TableListUriValidationMessage),
                    "Closing a presentation must not hide the transient message before the next activation.");
                firstPresentation = null;

                var changedProperties = new List<string>();
                PropertyChangedEventHandler propertyChanged = (_, e) => changedProperties.Add(e.PropertyName ?? string.Empty);
                settings.PropertyChanged += propertyChanged;
                try
                {
                    reopenedPresentation = new SettingsWindow { DataContext = settings };
                    presentationPort.CloseAction = reopenedPresentation.CloseFromPresentation;
                    windowTest.ShowAndWaitForContentRendered(reopenedPresentation);
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
                Assert.IsFalse(settings.HasPendingSettingChanges());
                Assert.IsTrue(GetPresentationActive(settings));

                ((Button)reopenedPresentation.FindName("buttonCancel"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(
                    reopenedPresentation,
                    () => !reopenedPresentation.IsVisible,
                    "Reopened SettingsWindow did not close through the actual Cancel route.");
                Assert.AreEqual(2, presentationPort.CloseRequestCount);
                Assert.AreEqual(SettingsWindowCloseReason.Cancel, reopenedPresentation.CloseReason);
                Assert.IsFalse(GetPresentationActive(settings));
                Assert.IsNull(reopenedPresentation.DataContext);
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

    private static void SeedTransientPlaylistUriValidation(SettingsDialogViewModel settings)
    {
        typeof(SettingsDialogViewModel)
            .GetField("tableListUriValidationMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, "invalid table URI");
        typeof(SettingsDialogViewModel)
            .GetField("playlistMd5UrlMappingTsvUriValidationMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, "invalid mapping URI");
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        dispatcher.Invoke(DispatcherPriority.Input, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static void RaiseKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target);
        Assert.IsNotNull(source);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }

    private static string GetAppliedApplicationTheme()
    {
        string source = Application.Current.Resources.MergedDictionaries
            .Select(dictionary => dictionary.Source?.OriginalString)
            .Single(value => value != null
                && (value.StartsWith("/Themes/", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("/BeMusicSeeker;component/Themes/", StringComparison.OrdinalIgnoreCase))
                && value.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
        return Path.GetFileNameWithoutExtension(source);
    }

    private static bool GetPresentationActive(SettingsDialogViewModel settings)
    {
        return (bool)typeof(SettingsDialogViewModel)
            .GetField("isPresentationActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(settings)!;
    }

    private static bool GetWindowPresentationActivated(SettingsWindow window)
    {
        return (bool)typeof(SettingsWindow)
            .GetField("presentationActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
    }

    [TestMethod]
    public void SettingsWindow_FieldLayoutsUseFlexibleLabelValueAndActionColumns()
    {
        XDocument[] pages = LoadSettingsPageXamls();
        string presentation = string.Join(Environment.NewLine, pages.Select(page => page.ToString(SaveOptions.DisableFormatting)));

        Assert.IsFalse(presentation.Contains("<GroupBox", StringComparison.Ordinal));
        Assert.IsFalse(presentation.Contains("<Expander", StringComparison.Ordinal));
        Assert.IsFalse(presentation.Contains("Height=\"24\"", StringComparison.Ordinal));
        Assert.IsFalse(presentation.Contains("ActualWidth", StringComparison.Ordinal));
        foreach (string controlName in new[] { "SettingsSection", "SettingsField", "SettingsOptionRow", "SettingsPathPicker", "SettingsListEditor", "SettingsStatusBanner" })
        {
            StringAssert.Contains(presentation, controlName);
        }

        XElement[] lists = pages.SelectMany(page => page.Descendants(PresentationName("ListBox")))
            .Where(list => list.Attribute(XamlName("Name"))?.Value is "additionalOutputList" or "playHistoryPresetList")
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "additionalOutputList", "playHistoryPresetList" },
            lists.Select(list => list.Attribute(XamlName("Name"))!.Value).ToArray());
        Assert.IsTrue(lists.All(list => double.Parse(list.Attribute("MinHeight")!.Value) >= 120));
        Assert.IsTrue(lists.All(list => list.Attribute("ScrollViewer.HorizontalScrollBarVisibility")?.Value == "Disabled"));
    }

    [TestMethod]
    public void SettingsWindow_AllPagesContinuouslyStretchWithoutWidthBreakpoints()
    {
        XDocument window = LoadSettingsWindowXaml();
        XDocument[] pages = LoadSettingsPageXamls();
        XElement content = FindNamedElement(window, "settingsPageContent");
        string source = string.Join(Environment.NewLine,
            pages.Prepend(window).Select(document => document.ToString(SaveOptions.DisableFormatting)));

        Assert.AreEqual("Stretch", content.Attribute("HorizontalAlignment")?.Value);
        Assert.AreEqual("Stretch", content.Attribute("HorizontalContentAlignment")?.Value);
        Assert.IsNull(content.Attribute("MaxWidth"));
        Assert.IsFalse(source.Contains("ActualWidth", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("WidthConverter", StringComparison.OrdinalIgnoreCase));

        foreach (XDocument page in pages)
        {
            XElement root = page.Root!;
            string pageName = root.Attribute(XamlName("Class"))!.Value;
            Assert.IsNull(root.Attribute("MaxWidth"), pageName + " must not cap the available body width.");
            Assert.AreNotEqual("Left", root.Attribute("HorizontalAlignment")?.Value, pageName + " must stretch with the shell.");
            Assert.AreEqual("Stretch", root.Attribute("HorizontalContentAlignment")?.Value, pageName);

            IEnumerable<XElement> layoutOwners = root.Descendants()
                .Where(element => element.Name.LocalName is "SettingsSection" or "SettingsField" or "SettingsListEditor" or "Grid");
            Assert.IsFalse(layoutOwners.Any(element => element.Attribute("MaxWidth") != null), pageName);
            Assert.IsFalse(layoutOwners.Any(element => element.Attribute("HorizontalAlignment")?.Value == "Left"), pageName);
        }
    }

    [TestMethod]
    public void SettingsWindow_AllPagesFitActualMinimumWidthViewport()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var scroller = (ScrollViewer)window.FindName("settingsPageScrollViewer");
                var content = (ContentControl)window.FindName("settingsPageContent");

                for (int categoryIndex = 0; categoryIndex < navigation.Items.Count; categoryIndex++)
                {
                    navigation.SelectedIndex = categoryIndex;
                    PumpDispatcher(window.Dispatcher);
                    window.UpdateLayout();

                    var page = (FrameworkElement)content.Content;
                    string pageName = page.GetType().Name;
                    Assert.IsTrue(scroller.ViewportWidth > 0, pageName);
                    Assert.IsTrue(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5,
                        $"{pageName} produced horizontal extent {scroller.ExtentWidth} in viewport {scroller.ViewportWidth}.");
                    Assert.AreEqual(0d, scroller.ScrollableWidth, 0.5, pageName);
                    Assert.IsTrue(content.ActualWidth <= scroller.ViewportWidth + 0.5, pageName);
                    Assert.IsTrue(page.ActualWidth <= content.ActualWidth + 0.5, pageName);
                    Assert.AreEqual(HorizontalAlignment.Stretch, page.HorizontalAlignment, pageName);
                    Assert.IsTrue(double.IsPositiveInfinity(page.MaxWidth), pageName);

                    IEnumerable<FrameworkElement> widthOwners = FindDescendants<FrameworkElement>(page)
                        .Where(element => element != page
                            && element.IsDescendantOf(page)
                            && (element is Grid
                                or SettingsSection
                                or SettingsField
                                or SettingsOptionRow
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

    [TestMethod]
    public void RedesignedSettingsPages_UseAccessibleReusablePresentationContracts()
    {
        string[] pageNames =
        [
            "GeneralSettingsPage.xaml",
            "AppearanceSettingsPage.xaml",
            "PlaybackSettingsPage.xaml",
            "AudioSettingsPage.xaml",
            "RecordingSettingsPage.xaml"
        ];
        string source = string.Join(Environment.NewLine, pageNames.Select(name =>
            SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "Settings", "Pages", name)));
        foreach (string controlName in new[] { "SettingsSection", "SettingsField", "SettingsOptionRow", "SettingsPathPicker", "SettingsListEditor", "SettingsStatusBanner" })
        {
            StringAssert.Contains(source, "settings:" + controlName);
        }
        Assert.IsFalse(source.Contains("<GroupBox", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("<Expander", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Height=\"24\"", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ActualWidth", StringComparison.Ordinal));

        FrameworkPropertyMetadata pathMetadata = (FrameworkPropertyMetadata)SettingsPathPicker.PathProperty.GetMetadata(typeof(SettingsPathPicker));
        Assert.IsTrue(pathMetadata.BindsTwoWayByDefault);
        Assert.AreEqual(true, SettingsPathPicker.IsPathReadOnlyProperty.DefaultMetadata.DefaultValue);
        string controls = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "Settings", "SettingsControls.xaml");
        StringAssert.Contains(controls, "AutomationProperties.Name=\"{TemplateBinding Label}\"");
        StringAssert.Contains(controls, "AutomationProperties.LiveSetting");
        StringAssert.Contains(controls, "Text=\"{TemplateBinding Icon}\"");
    }

    [TestMethod]
    public void SettingsWindow_ActionsAndNavigationExposeLocalizedAccessibleContracts()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement navigation = FindNamedElement(document, "settingsNavigation");
        XElement saveButton = FindNamedElement(document, "buttonOK");
        XElement cancelButton = FindNamedElement(document, "buttonCancel");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.AreEqual("SettingsCategoryNavigation", navigation.Attribute(presentation + "AutomationProperties.AutomationId")?.Value ?? navigation.Attribute("AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual("SettingsSaveAndClose", saveButton.Attribute("AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual("SettingsCancel", cancelButton.Attribute("AutomationProperties.AutomationId")?.Value);
        StringAssert.Contains(saveButton.Attribute("Content")!.Value, "Resources.Save_and_close");
        StringAssert.Contains(cancelButton.Attribute("Content")!.Value, "Resources.Cancel");
    }

    [TestMethod]
    public void MainWindow_PresentsFreshOwnedModalThroughCoordinator()
    {
        string root = FindRepositoryRoot();
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();

        Assert.IsFalse(mainWindowXaml.Contains("<v:SettingsWindow", StringComparison.Ordinal));
        StringAssert.Contains(mainWindowCode, "new UiWindowDialogRequest<SettingsWindow, SettingsWindowCloseReason>(");
        StringAssert.Contains(mainWindowCode, "settingsWindow = new SettingsWindow");
        StringAssert.Contains(mainWindowCode, "DataContext = viewModel.SettingDialog");
        StringAssert.Contains(mainWindowCode, "PlaybackPanel = viewModel.PlaybackPanel");
        StringAssert.Contains(mainWindowCode, "PlaylistWorkspace = viewModel.PlaylistWorkspace");
        StringAssert.Contains(mainWindowCode, "settingsWindow.Activate();");
        StringAssert.Contains(mainWindowCode, "PlaybackOverlayVisibility = Visibility.Visible;");
        StringAssert.Contains(mainWindowCode, "PlaybackOverlayVisibility = previousPlaybackOverlayVisibility;");
    }

    [TestMethod]
    public void SettingsWindow_CloseLifecycleSeparatesUserAndProgrammaticRoutes()
    {
        string root = FindRepositoryRoot();
        string settingsWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingsWindow.cs"));

        StringAssert.Contains(settingsWindowCode, "if (!settingDialogViewModel.IsEditCancellationEnabled || viewOperationInProgress || userCancellationQueued)");
        StringAssert.Contains(settingsWindowCode, "settingDialogViewModel.CancelCommand.Execute();");
        StringAssert.Contains(settingsWindowCode, "internal void CloseFromPresentation()");
        StringAssert.Contains(settingsWindowCode, "internal void CloseForOwnerShutdown()");
        StringAssert.Contains(settingsWindowCode, "private void CloseForManualResync()");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.Apply");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.Cancel");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.Presentation");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.ManualResync");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.OwnerShutdown");
    }

    [TestMethod]
    public void NativeClose_WhenCancellationIsEnabled_RequestsCancelCompletion()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };

            Assert.IsTrue(SimulateNativeClose(window));
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

            Assert.AreEqual(1, presentation.CloseRequestCount);
        });
    }

    [TestMethod]
    public void NativeClose_WhenCancellationIsDisabled_DoesNotRequestCancelCompletion()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            typeof(SettingsDialogViewModel)
                .GetField("scoreReloadPending", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel.SettingDialog, true);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };

            Assert.IsTrue(SimulateNativeClose(window));
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

            Assert.AreEqual(0, presentation.CloseRequestCount);
        });
    }

    [TestMethod]
    public void RunViewOperation_BlocksNativeCloseUntilOperationCompletes()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task operation = window.RunViewOperationAsync(() => operationCompletion.Task);

                Assert.IsTrue(SimulateNativeClose(window));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.AreEqual(0, presentation.CloseRequestCount);

                operationCompletion.SetResult();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.IsTrue(operation.IsCompletedSuccessfully);

                Assert.IsTrue(SimulateNativeClose(window));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.AreEqual(1, presentation.CloseRequestCount);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    [TestMethod]
    public void ApplyOperation_RejectsNativeCloseUntilPresentationSuccessAuthorizesApplyClose()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel settings = viewModel.SettingDialog;
            var window = new SettingsWindow { DataContext = settings };
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var applyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool closed = false;
                var closeCancellations = new List<bool>();
                window.Closing += (_, args) => closeCancellations.Add(args.Cancel);
                window.Closed += (_, _) => closed = true;

                Task applyOperation = window.RunApplyOperationAsync(async () =>
                {
                    SetEditCompletionInProgress(settings, true);
                    try
                    {
                        await applyRelease.Task;
                        window.CloseFromPresentation();
                    }
                    finally
                    {
                        SetEditCompletionInProgress(settings, false);
                    }
                });

                Assert.IsFalse(applyOperation.IsCompleted);
                Assert.IsTrue(SimulateNativeClose(window));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.AreEqual(SettingsWindowCloseReason.None, window.CloseReason);
                Assert.IsFalse(closed);

                applyRelease.SetResult();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

                Assert.IsTrue(applyOperation.IsCompletedSuccessfully);
                Assert.AreEqual(SettingsWindowCloseReason.Apply, window.CloseReason);
                Assert.IsTrue(closed);
                CollectionAssert.AreEqual(new[] { true, false }, closeCancellations);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_PresentationWorkFollowsWindowLifetime()
    {
        string root = FindRepositoryRoot();
        string settingsWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingsWindow.cs"));

        StringAssert.Contains(settingsWindowCode, "protected override void OnContentRendered(EventArgs e)");
        StringAssert.Contains(settingsWindowCode, "settingDialogViewModel.SetPresentationActive(true);");
        StringAssert.Contains(settingsWindowCode, "settingDialogViewModel.RefreshLr2PlayHistorySchemaStatusPresentation();");
        StringAssert.Contains(settingsWindowCode, "\"settings_dialog_open\"");
        StringAssert.Contains(settingsWindowCode, "protected override void OnClosed(EventArgs e)");
        StringAssert.Contains(settingsWindowCode, "settingDialogViewModel.SetPresentationActive(false);");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "BeMusicSeeker.sln")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static XDocument LoadSettingsWindowXaml()
    {
        return XDocument.Load(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingsWindow.xaml"));
    }

    private static XDocument LoadSettingsControlsXaml()
    {
        return XDocument.Load(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "Settings", "SettingsControls.xaml"));
    }

    private static XDocument[] LoadSettingsPageXamls()
    {
        string directory = Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "Settings", "Pages");
        return Directory.EnumerateFiles(directory, "*SettingsPage.xaml")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(XDocument.Load)
            .ToArray();
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
            Source = new Uri("/BeMusicSeeker;component/BeMusicSeeker/Views/Settings/SettingsControls.xaml", UriKind.RelativeOrAbsolute)
        });
        return host;
    }

    private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null)
        {
            return null;
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
                return match;
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

        return null;
    }

    private static bool IsVisualDescendantOf(DependencyObject candidate, DependencyObject ancestor)
    {
        for (DependencyObject current = candidate; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

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

    private static double ContrastRatio(Color first, Color second)
    {
        static double Channel(byte value)
        {
            double normalized = value / 255d;
            return normalized <= 0.04045d
                ? normalized / 12.92d
                : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
        }

        static double Luminance(Color color)
        {
            return 0.2126d * Channel(color.R)
                + 0.7152d * Channel(color.G)
                + 0.0722d * Channel(color.B);
        }

        double firstLuminance = Luminance(first);
        double secondLuminance = Luminance(second);
        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    private static XElement FindNamedElement(XDocument document, string name)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == name || element.Attribute("Name")?.Value == name);
    }

    private static XElement FindNamedElement(IEnumerable<XDocument> documents, string name)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return documents.SelectMany(document => document.Descendants())
            .Single(element => element.Attribute(xaml + "Name")?.Value == name || element.Attribute("Name")?.Value == name);
    }

    private static XName PresentationName(string localName)
    {
        return XName.Get(localName, "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
    }

    private static XName XamlName(string localName)
    {
        return XName.Get(localName, "http://schemas.microsoft.com/winfx/2006/xaml");
    }

    private static XElement FindKeyedStyle(XDocument document, string key)
    {
        XElement style = document.Descendants(PresentationName("Style"))
            .SingleOrDefault(candidate => candidate.Attribute(XamlName("Key"))?.Value == key);
        return style ?? LoadSettingsControlsXaml().Descendants(PresentationName("Style"))
            .Single(candidate => candidate.Attribute(XamlName("Key"))?.Value == key);
    }

    private static void AssertStyleSetter(XElement style, string property, string value)
    {
        Assert.IsTrue(style.Elements(PresentationName("Setter"))
            .Any(setter => setter.Attribute("Property")?.Value == property && setter.Attribute("Value")?.Value == value),
            property + "=" + value);
    }

    private static void AssertStyleMultiTriggerSetter(
        XElement style,
        string stateProperty,
        string setterProperty,
        string setterValue)
    {
        Assert.IsTrue(style.Descendants(PresentationName("MultiTrigger"))
            .Where(trigger =>
            {
                XElement[] conditions = trigger
                    .Element(PresentationName("MultiTrigger.Conditions"))!
                    .Elements(PresentationName("Condition"))
                    .ToArray();
                return conditions.Any(condition => condition.Attribute("Property")?.Value == "IsEnabled" && condition.Attribute("Value")?.Value == "True")
                    && conditions.Any(condition => condition.Attribute("Property")?.Value == stateProperty && condition.Attribute("Value")?.Value == "True");
            })
            .SelectMany(trigger => trigger.Elements(PresentationName("Setter")))
            .Any(setter => setter.Attribute("Property")?.Value == setterProperty && setter.Attribute("Value")?.Value == setterValue),
            "IsEnabled=True + " + stateProperty + "=True, " + setterProperty + "=" + setterValue);
    }

    private static void AssertStyleTriggerSetter(
        XElement style,
        string stateProperty,
        string setterProperty,
        string setterValue)
    {
        Assert.IsTrue(style.Descendants(PresentationName("Trigger"))
            .Where(trigger => trigger.Attribute("Property")?.Value == stateProperty)
            .SelectMany(trigger => trigger.Elements(PresentationName("Setter")))
            .Any(setter => setter.Attribute("Property")?.Value == setterProperty
                && setter.Attribute("Value")?.Value == setterValue),
            stateProperty + ", " + setterProperty + "=" + setterValue);
    }

    private static string ExtractResourcePath(string binding)
    {
        const string marker = "Path=";
        int start = binding?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (start < 0)
        {
            return string.Empty;
        }
        start += marker.Length;
        int end = binding.IndexOf(',', start);
        return end < 0 ? binding[start..].TrimEnd('}') : binding[start..end];
    }

    private static bool SimulateNativeClose(SettingsWindow window)
    {
        var args = new System.ComponentModel.CancelEventArgs();
        typeof(SettingsWindow)
            .GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [args]);
        return args.Cancel;
    }

    private static void SetEditCompletionInProgress(SettingsDialogViewModel viewModel, bool value)
    {
        typeof(SettingsDialogViewModel)
            .GetField("isEditCompletionInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    private sealed class DangerDialogContext
    {
        internal DangerDialogContext(
            SettingsDialogViewModel settings,
            DangerPlayHistoryPort playHistory,
            Func<int> reloadCount)
        {
            Settings = settings;
            PlayHistory = playHistory;
            ReloadCount = reloadCount;
        }

        internal SettingsDialogViewModel Settings { get; }

        internal DangerPlayHistoryPort PlayHistory { get; }

        internal Func<int> ReloadCount { get; }
    }

    private sealed class StatusBannerBindingSource : INotifyPropertyChanged
    {
        private string message;

        public string Message
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

        public event PropertyChangedEventHandler PropertyChanged;
    }

    private sealed class DangerApplicationLifetime : IApplicationLifetimePort
    {
        private readonly List<string> events;

        internal DangerApplicationLifetime(List<string> events)
        {
            this.events = events;
        }

        public bool IsFirstStartup => false;

        internal int CoordinatedShutdownCount { get; private set; }

        internal int ShutdownRequestCount { get; private set; }

        public void CompleteFirstStartup()
        {
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
            CoordinatedShutdownCount++;
            events.Add("shutdown-mark:" + reason);
        }

        public void RequestShutdown()
        {
            ShutdownRequestCount++;
            events.Add("shutdown-request");
        }

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }

    private sealed class DangerSettingsEditSession : ISettingsEditSession
    {
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
        public bool HasPlaylistTables => true;

        public long PlaylistCatalogVersion => 0;

        public IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots() => [];

        public bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath) => false;

        public void SchedulePlaylistUrlCompletionRefresh(string reason)
        {
        }

        public void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath)
        {
        }

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

        public void InvalidateReadCache(string reason)
        {
            InvalidationCount++;
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
            CancellationToken cancellationToken = default)
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
        private readonly List<string> events;

        internal DangerApplicationDataStore(List<string> events = null)
        {
            this.events = events;
        }

        internal int CallCount { get; private set; }

        internal Exception Failure { get; set; }

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
        private readonly List<string> events;
        private readonly TaskCompletionSource<UiDialogResult> confirmationCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal DangerDialogService(List<string> events = null)
        {
            this.events = events;
        }

        internal bool HoldConfirmation { get; set; }

        internal TaskCompletionSource ConfirmationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<UiMessageRequest> Messages { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            if (string.Equals(request.MessageBoxText, Resources.Msg_success_uninstall, StringComparison.Ordinal))
            {
                events?.Add("message-success");
            }
            else if (string.Equals(request.MessageBoxText, "アプリケーションを終了します。", StringComparison.Ordinal))
            {
                events?.Add("message-exit");
            }
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            events?.Add("confirm");
            ConfirmationStarted.TrySetResult();
            return HoldConfirmation
                ? confirmationCompletion.Task
                : Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
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

        internal Action CloseAction { get; set; }

        public void OpenSettingsDialog()
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

        public void RefreshAppearanceSelection()
        {
        }
    }

    private enum PlaylistUriCompletionRoute
    {
        CancelButton,
        SaveButton,
        NativeClose
    }

    private sealed class RecordingSettingsWindowDialogService : IUiDialogService
    {
        private readonly TestWindowPresentationScope windowTest;
        private readonly SettingsDialogViewModel settings;
        private readonly string originalSong;
        private readonly string originalConfig;
        private readonly string typedSong;
        private readonly string typedConfig;
        private readonly string pickedSong;
        private readonly string pickedConfig;
        private readonly string closeMode;
        private readonly bool expectConfigAccepted;
        private readonly UiDialogStatus configPickerStatus;
        private int filePickIndex;

        internal RecordingSettingsWindowDialogService(
            TestWindowPresentationScope windowTest,
            SettingsDialogViewModel settings,
            string originalSong,
            string originalConfig,
            string typedSong,
            string typedConfig,
            string pickedSong,
            string pickedConfig,
            string closeMode,
            bool expectConfigAccepted = true,
            UiDialogStatus configPickerStatus = UiDialogStatus.Accepted)
        {
            this.windowTest = windowTest;
            this.settings = settings;
            this.originalSong = originalSong;
            this.originalConfig = originalConfig;
            this.typedSong = typedSong;
            this.typedConfig = typedConfig;
            this.pickedSong = pickedSong;
            this.pickedConfig = pickedConfig;
            this.closeMode = closeMode;
            this.expectConfigAccepted = expectConfigAccepted;
            this.configPickerStatus = configPickerStatus;
        }

        internal Window ExpectedOwner { get; set; }
        internal Window LastModalOwner { get; private set; }
        internal bool LastModalShowInTaskbar { get; private set; }
        internal List<UiFilePickerRequest> FileRequests { get; } = [];
        internal string ConfigPathAfterBrowse { get; private set; }
        internal string ValidationErrorAfterConfigBrowse { get; private set; }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            Assert.AreSame(ExpectedOwner, request.Owner);
            TWindow window = request.CreateWindow();
            window.Owner = request.Owner;
            LastModalOwner = window.Owner;
            LastModalShowInTaskbar = window.ShowInTaskbar;
            Assert.IsInstanceOfType<Lr2AdvancedPathsDialog>(window);
            var advancedDialog = (Lr2AdvancedPathsDialog)(Window)window;
            advancedDialog.ContentRendered += (_, _) =>
            {
                SettingsPathPicker songPicker = FindDescendants<SettingsPathPicker>(advancedDialog).Single(picker => picker.Label == Resources.FilePath_songDB);
                SettingsPathPicker configPicker = FindDescendants<SettingsPathPicker>(advancedDialog).Single(picker => picker.Label == Resources.FilePath_configXml);
                TextBox songEditor = FindDescendants<TextBox>(songPicker).Single();
                TextBox configEditor = FindDescendants<TextBox>(configPicker).Single();
                songEditor.Text = typedSong;
                songEditor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                configEditor.Text = typedConfig;
                configEditor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.AreEqual(typedSong, advancedDialog.SongDbPath);
                Assert.AreEqual(typedConfig, advancedDialog.ConfigPath);
                Assert.AreEqual(originalSong, settings.LR2SongDBPath);
                Assert.AreEqual(originalConfig, settings.LR2ConfigXmlPath);
                FindDescendants<Button>(songPicker).Single(button => Equals(button.Content, Resources.Browse)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(pickedSong, advancedDialog.SongDbPath);
                Assert.AreEqual(originalSong, settings.LR2SongDBPath);
                FindDescendants<Button>(configPicker).Single(button => Equals(button.Content, Resources.Browse)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ConfigPathAfterBrowse = advancedDialog.ConfigPath;
                ValidationErrorAfterConfigBrowse = advancedDialog.ValidationError;
                Assert.AreEqual(
                    configPickerStatus == UiDialogStatus.Accepted && expectConfigAccepted ? pickedConfig : typedConfig,
                    advancedDialog.ConfigPath);
                Assert.AreEqual(originalConfig, settings.LR2ConfigXmlPath);
                if (closeMode == "Done")
                {
                    FindDescendants<Button>(advancedDialog).Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (closeMode == "Cancel")
                {
                    FindDescendants<Button>(advancedDialog).Single(button => button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else
                {
                    advancedDialog.Close();
                }
            };
            windowTest.PrepareForOwnedPresentation(window);
            bool? dialogResult = window.ShowDialog();
            UiDialogStatus status = dialogResult == true
                ? UiDialogStatus.Accepted
                : dialogResult == false ? UiDialogStatus.CancelledByUser : UiDialogStatus.ClosedByUser;
            return Task.FromResult(new UiWindowDialogResult<TResult>(status, request.CreateResult(window), dialogResult));
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            FileRequests.Add(request);
            if (filePickIndex++ == 0)
            {
                return Task.FromResult(new UiFilePickerResult(UiDialogStatus.Accepted, [pickedSong]));
            }
            if (configPickerStatus != UiDialogStatus.Accepted)
            {
                return Task.FromResult(new UiFilePickerResult(configPickerStatus));
            }
            string path = pickedConfig;
            return Task.FromResult(new UiFilePickerResult(UiDialogStatus.Accepted, [path]));
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new UiFolderPickerResult(UiDialogStatus.CancelledByUser));
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailingLr2AdvancedPathsDialogService : IUiDialogService
    {
        private readonly UiDialogStatus windowStatus;
        private readonly UiDialogStatus notificationStatus;

        internal FailingLr2AdvancedPathsDialogService(UiDialogStatus windowStatus, UiDialogStatus notificationStatus)
        {
            this.windowStatus = windowStatus;
            this.notificationStatus = notificationStatus;
        }

        internal Window ExpectedOwner { get; set; }
        internal int WindowRequestCount { get; private set; }
        internal int MessageRequestCount { get; private set; }
        internal UiMessageRequest LastMessageRequest { get; private set; }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            Assert.AreSame(ExpectedOwner, request.Owner);
            WindowRequestCount++;
            Exception error = windowStatus == UiDialogStatus.Failed
                ? new InvalidOperationException("advanced modal failed")
                : null;
            return Task.FromResult(new UiWindowDialogResult<TResult>(windowStatus, error: error));
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageRequestCount++;
            LastMessageRequest = request;
            return Task.FromResult(notificationStatus == UiDialogStatus.Failed
                ? UiDialogResult.Failed(new InvalidOperationException("notification failed"))
                : UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PendingLr2AdvancedPathsDialogService : IUiDialogService
    {
        private readonly TaskCompletionSource<UiDialogStatus> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Window ExpectedOwner { get; set; }
        internal Window LastOwner { get; private set; }
        internal Lr2AdvancedPathsDialog CreatedWindow { get; private set; }
        internal int WindowCount { get; private set; }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            Assert.AreSame(ExpectedOwner, request.Owner);
            TWindow created = request.CreateWindow();
            LastOwner = request.Owner;
            CreatedWindow = created as Lr2AdvancedPathsDialog;
            Assert.IsNotNull(CreatedWindow);
            WindowCount++;
            return AwaitCompletionAsync(completion.Task, request, created);
        }

        internal void Complete(UiDialogStatus status) => completion.TrySetResult(status);

        private static async Task<UiWindowDialogResult<TResult>> AwaitCompletionAsync<TWindow, TResult>(
            Task<UiDialogStatus> completion,
            UiWindowDialogRequest<TWindow, TResult> request,
            TWindow window)
            where TWindow : Window
        {
            UiDialogStatus status = await completion.ConfigureAwait(false);
            return new UiWindowDialogResult<TResult>(status, request.CreateResult(window));
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
        internal Window ExpectedOwner { get; set; }
        internal ReleaseNotesWindow CreatedWindow { get; private set; }
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
