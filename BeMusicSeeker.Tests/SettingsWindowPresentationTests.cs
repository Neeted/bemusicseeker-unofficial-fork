using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
    [TestInitialize]
    public void MaterializeCanonicalApplicationResources()
    {
        TestUiDispatcherHost.Invoke(EnsureCanonicalApplicationResources);
    }

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
            Assert.IsTrue(double.IsFinite(window.Width) && window.Width > 0d);
            Assert.IsTrue(double.IsFinite(window.Height) && window.Height > 0d);
            Assert.IsTrue(double.IsFinite(window.MinWidth) && window.MinWidth > 0d);
            Assert.IsTrue(double.IsFinite(window.MinHeight) && window.MinHeight > 0d);
            Assert.IsTrue(window.MinWidth <= window.Width);
            Assert.IsTrue(window.MinHeight <= window.Height);
        });
    }

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

                Border nativeContent = FindDescendants<Border>(window).Single(border =>
                    border.Style != null
                    && FindResourceInStyleScope(border, "App.Canonical.NativeWindowContentStyle")
                    && border.BorderThickness == new Thickness(0)
                    && border.CornerRadius == new CornerRadius(0));
                ListBox navigation = (ListBox)window.FindName("settingsNavigation");
                ContentControl pageHeader = (ContentControl)window.FindName("settingsPageHeader");

                Rect nativeBounds = new(0d, 0d, nativeContent.ActualWidth, nativeContent.ActualHeight);
                Rect navigationBounds = GetVisualBounds(nativeContent, navigation);
                Rect headerBounds = GetVisualBounds(nativeContent, pageHeader);
                Assert.IsTrue(
                    navigationBounds.Left <= nativeBounds.Left + 1d,
                    $"Settings navigation must reach the native client edge (native={nativeBounds}, navigation={navigationBounds}).");
                Assert.IsTrue(
                    headerBounds.Left > navigationBounds.Right,
                    "The selected page must retain inner spacing after the outer native inset is removed.");
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

    [TestMethod]
    public void SettingsWindow_RightClickCategoryIsTenthAndExecutablePickerUpdatesSelectedDraft()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(new Settings
            {
                RightClickActionsJson = string.Empty,
                OperationModeLR2DB = false,
                BMSRootPath = Path.GetTempPath(),
                StandaloneBmsRootPaths = Path.GetTempPath(),
                BMSInstallDir = Path.GetTempPath(),
                ScanBmsFilesOnStartup = false,
                SkipInitPlaylistLoad = true
            });
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
                window.Measure(new Size(820, 760));
                window.Arrange(new Rect(0, 0, 820, 760));
                window.UpdateLayout();
                ListBox navigation = (ListBox)window.FindName("settingsNavigation");
                Assert.AreEqual(11, navigation.Items.Count);
                var rightClickNavigation = (ListBoxItem)window.FindName("navigationRightClick");
                Assert.AreEqual("SettingsCategoryRightClick", AutomationProperties.GetAutomationId(rightClickNavigation));
                Assert.AreEqual(9, navigation.Items.IndexOf(rightClickNavigation));
                var aboutNavigation = (ListBoxItem)window.FindName("navigationAbout");
                Assert.AreEqual(10, navigation.Items.IndexOf(aboutNavigation));

                owner.SettingDialog.RightClickActionSettingsEditor.AddProgramAction();
                window.HandleBrowseRightClickProgramExecutable();

                Assert.AreEqual(1, dialogs.FileRequests.Count);
                UiFilePickerRequest request = dialogs.FileRequests[0];
                Assert.AreSame(window, request.Owner);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.RightClick_executable_filter, request.Filter);
                Assert.AreEqual(string.Empty, request.FileName);
                RightClickProgramActionEditorRow selected = owner.SettingDialog
                    .RightClickActionSettingsEditor.SelectedProgramAction;
                Assert.AreEqual(@"C:\Tools\Chart Viewer.exe", selected.ExecutablePath);
                Assert.AreEqual("Chart Viewer", selected.Name);
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_RightClickRestoreDefaultsIsEnabledForValidDraftAndDoesNotPrompt()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            const string originalJson = "{\"webActions\":[{\"id\":\"custom\",\"name\":\"Custom\",\"urlTemplate\":\"https://example.test/{md5}\",\"enabled\":true,\"chartKind\":\"All\"}],\"programActions\":[{\"id\":\"viewer\",\"name\":\"Viewer\",\"executablePath\":\"C:\\\\Tools\\\\viewer.exe\",\"argumentTemplate\":\"{filePath}\",\"enabled\":true}]}";
            var settings = new Settings
            {
                RightClickActionsJson = originalJson,
                OperationModeLR2DB = false,
                BMSRootPath = Path.GetTempPath(),
                StandaloneBmsRootPaths = Path.GetTempPath(),
                BMSInstallDir = Path.GetTempPath(),
                ScanBmsFilesOnStartup = false,
                SkipInitPlaylistLoad = true
            };
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
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
                PumpDispatcher(window.Dispatcher);
                var page = (RightClickSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                Button restoreButton = FindDescendants<Button>(page).Single(button =>
                    AutomationProperties.GetAutomationId(button) == "RightClickRestoreDefaults");

                Assert.IsTrue(restoreButton.IsEnabled);
                restoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, restoreButton));
                PumpDispatcher(window.Dispatcher);

                RightClickActionSettingsEditor editor = owner.SettingDialog.RightClickActionSettingsEditor;
                Assert.AreEqual(5, editor.WebActions.Count);
                Assert.AreEqual(0, editor.ProgramActions.Count);
                Assert.IsTrue(editor.IsDirty);
                Assert.IsFalse(editor.IsInvalidPersistedSettings);
                Assert.IsTrue(editor.TryPrepareSave(out string restoredJson, out string error), error);
                Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, restoredJson);
                Assert.AreEqual(originalJson, settings.RightClickActionsJson);
                Assert.AreEqual(0, dialogs.ConfirmationRequests.Count);
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_RightClickRestoreDefaultsIsDiscardedByNativeClose()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            const string originalJson = "{\"webActions\":[{\"id\":\"custom\",\"name\":\"Custom\",\"urlTemplate\":\"https://example.test/{md5}\",\"enabled\":true,\"chartKind\":\"All\"}],\"programActions\":[{\"id\":\"viewer\",\"name\":\"Viewer\",\"executablePath\":\"C:\\\\Tools\\\\viewer.exe\",\"argumentTemplate\":\"{filePath}\",\"enabled\":true}]}";
            var settings = new Settings
            {
                RightClickActionsJson = originalJson,
                OperationModeLR2DB = false,
                BMSRootPath = Path.GetTempPath(),
                StandaloneBmsRootPaths = Path.GetTempPath(),
                BMSInstallDir = Path.GetTempPath(),
                ScanBmsFilesOnStartup = false,
                SkipInitPlaylistLoad = true
            };
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
            var presentation = new RecordingPresentationPort();
            owner.SettingDialog.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
            presentation.CloseAction = window.CloseFromPresentation;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                owner.SettingDialog.RightClickActionSettingsEditor.RestoreDefaults();
                Assert.IsTrue(owner.SettingDialog.RightClickActionSettingsEditor.IsDirty);

                window.Close();
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(1, presentation.CloseRequestCount);
                Assert.AreEqual(originalJson, settings.RightClickActionsJson);
                Assert.IsFalse(owner.SettingDialog.RightClickActionSettingsEditor.IsDirty);
                Assert.IsFalse(owner.SettingDialog.HasPendingSettingChanges());
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
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
            Assert.AreEqual(SizeToContent.Height, window.SizeToContent);
            Assert.IsTrue(double.IsFinite(window.Width) && window.Width > 0d);
            Assert.IsTrue(double.IsFinite(window.MinWidth) && window.MinWidth > 0d);
            Assert.IsTrue(double.IsFinite(window.MinHeight) && window.MinHeight > 0d);
            Assert.IsTrue(double.IsFinite(window.MaxHeight) && window.MaxHeight >= window.MinHeight);
        });
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

            SettingsWindow window = null;
            try
            {
                Settings settings = new Settings { OperationModeLR2DB = false };
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
                var dialogs = new RecordingSettingsRouteDialogService(
                    installDirectory,
                    searchRoot,
                    additionalOutputBase);
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
                window.HandleAddCustomFolderAdditionalOutputBase();
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

                Assert.AreEqual(3, dialogs.FolderRequests.Count);
                Assert.IsFalse(dialogs.FolderRequests[0].Multiselect);
                Assert.IsTrue(dialogs.FolderRequests[1].Multiselect);
                Assert.IsTrue(dialogs.FolderRequests[2].Multiselect);
                Assert.IsTrue(dialogs.FolderRequests.All(request => ReferenceEquals(window, request.Owner)));
                Assert.AreEqual(1, dialogs.SaveFileRequests.Count);
                Assert.AreEqual("BeMusicSeeker_backup.sql", dialogs.SaveFileRequests[0].FileName);
                Assert.AreEqual(".sql", dialogs.SaveFileRequests[0].DefaultExtension);
                Assert.AreEqual("sqlファイル(*.sql)|*.sql", dialogs.SaveFileRequests[0].Filter);
                Assert.IsTrue(dialogs.SaveFileRequests[0].AddExtension);
                Assert.AreSame(window, dialogs.SaveFileRequests[0].Owner);
                Assert.AreEqual(2, dialogs.ConfirmationRequests.Count);
                Assert.IsTrue(dialogs.ConfirmationRequests.All(request => ReferenceEquals(window, request.Owner)));
                Assert.AreEqual(1, dialogs.FileRequests.Count);
                Assert.AreEqual(UiDialogStatus.Failed, dialogs.FileResult.Status);
                Assert.AreSame(window, dialogs.FileRequests[0].Owner);
                Assert.AreEqual(typeof(PlayHistoryFolderDisplayPresetEditDialog), dialogs.LastWindowType);
                Assert.AreSame(window, dialogs.LastWindowOwner);
                Assert.IsInstanceOfType<PlayHistoryFolderDisplayPresetEditDialog>(dialogs.LastCreatedWindow);
                CollectionAssert.Contains(owner.SettingDialog.AvailableBMSDirectories, installDirectory);
                CollectionAssert.Contains(owner.SettingDialog.AvailableBMSDirectories, searchRoot);
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
            File.WriteAllText(backupPath, "restore fixture");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }

            SettingsWindow window = null;
            MainWindowViewModel owner = null;
            try
            {
                owner = MainWindowViewModelTestFactory.Create(new Settings
                {
                    OperationModeLR2DB = false,
                    BMSRootPath = Path.GetTempPath(),
                    StandaloneBmsRootPaths = Path.GetTempPath(),
                    BMSInstallDir = Path.GetTempPath(),
                    ScanBmsFilesOnStartup = false,
                    SkipInitPlaylistLoad = true
                });
                PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateBackupWorkspace(
                    songDbPath,
                    [new BMSTable { playlist_id = 1, name = "Existing", symbol = "E" }],
                    out _,
                    out _,
                    restoreUiApplyScheduler: _ => throw new InvalidOperationException("restore durable failure"));
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
                InvalidOperationException? failure = null;
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(restoreTask, "settings restore failure");
                }
                catch (InvalidOperationException exception)
                {
                    failure = exception;
                }
                Assert.IsNotNull(failure);

                StringAssert.Contains(failure!.Message, "restore durable failure");
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(SettingsWindowCloseReason.None, window.CloseReason);
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

            SettingsWindow window = null;
            try
            {
                Settings settings = new Settings
                {
                    OperationModeLR2DB = false,
                    BMSRootPath = string.Empty,
                    StandaloneBmsRootPaths = string.Empty
                };
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
                var dialogs = new RecordingSettingsRouteDialogService();
                window = new SettingsWindow(dialogs)
                {
                    DataContext = owner.SettingDialog,
                    PlaylistWorkspace = owner.PlaylistWorkspace
                };
                windowTest.ShowAndWaitForContentRendered(window);
                dialogs.ExpectedOwner = window;

                string[] acceptedPaths = [firstPath, secondPath];
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(
                    UiDialogStatus.Accepted,
                    acceptedPaths));
                window.HandleAddBmsSearchRootPathsAsync().GetAwaiter().GetResult();

                Assert.AreEqual(1, dialogs.FolderRequests.Count);
                Assert.IsTrue(dialogs.FolderRequests[0].Multiselect);
                Assert.AreSame(window, dialogs.FolderRequests[0].Owner);
                CollectionAssert.AreEqual(acceptedPaths, owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(firstPath, owner.SettingDialog.SelectedBmsSearchRootPath);

                string[] rootsAfterAccepted = [.. owner.SettingDialog.AvailableBMSDirectories];
                string selectedAfterAccepted = owner.SettingDialog.SelectedBmsSearchRootPath;
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.CancelledByUser));
                window.HandleAddBmsSearchRootPathsAsync().GetAwaiter().GetResult();
                CollectionAssert.AreEqual(rootsAfterAccepted, owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(selectedAfterAccepted, owner.SettingDialog.SelectedBmsSearchRootPath);

                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.ClosedByUser));
                window.HandleAddBmsSearchRootPathsAsync().GetAwaiter().GetResult();
                CollectionAssert.AreEqual(rootsAfterAccepted, owner.SettingDialog.AvailableBMSDirectories);
                Assert.AreEqual(selectedAfterAccepted, owner.SettingDialog.SelectedBmsSearchRootPath);

                var pickerFailure = new IOException("search root picker sentinel");
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Failed, error: pickerFailure));
                InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                    () => window.HandleAddBmsSearchRootPathsAsync().GetAwaiter().GetResult());
                Assert.AreSame(pickerFailure, failure.InnerException);
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

            SettingsWindow window = null;
            SynchronizationContext previousContext = SynchronizationContext.Current;
            try
            {
                Settings settings = new Settings
                {
                    OperationModeLR2DB = false,
                    BMSRootPath = string.Empty,
                    StandaloneBmsRootPaths = string.Empty
                };
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
                var dialogs = new RecordingSettingsRouteDialogService();
                window = new SettingsWindow(dialogs)
                {
                    DataContext = owner.SettingDialog,
                    PlaylistWorkspace = owner.PlaylistWorkspace
                };
                windowTest.ShowAndWaitForContentRendered(window);
                dialogs.ExpectedOwner = window;

                var completion = new TaskCompletionSource<UiFolderPickerResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                dialogs.FolderTasks.Enqueue(completion.Task);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));

                Task route = window.HandleAddBmsSearchRootPathsAsync();

                Assert.IsFalse(route.IsCompleted);
                bool dispatcherWorkCompleted = false;
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => dispatcherWorkCompleted = true));
                PumpUntil(window, () => dispatcherWorkCompleted, "The settings dispatcher stopped while the BMS root picker route was pending.");
                Assert.IsFalse(route.IsCompleted);

                completion.TrySetResult(new UiFolderPickerResult(
                    UiDialogStatus.Accepted,
                    [firstPath, secondPath]));
                PumpUntil(window, () => route.IsCompleted, "The BMS root picker route did not complete after its picker task completed.");
                route.GetAwaiter().GetResult();

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
                    if (window?.IsVisible == true)
                    {
                        window.CloseForOwnerShutdown();
                    }
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                    Directory.Delete(scope, recursive: true);
                }
            }
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
                var navigation = (ListBox)window.FindName("settingsNavigation");
                navigation.SelectedItem = window.FindName("navigationAbout");
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
    [DataRow(false)]
    [DataRow(true)]
    public void GeneralSchema_InstallOrRepairActualButtonPreservesCancelAndSuccessOwnerContracts(bool accept)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            string scope = CreateDangerSchemaScope(out Settings values, out string scoreDbPath);
            SettingsWindow window = null;
            try
            {
                Lr2PlayHistorySchemaCheckResult resetResult = new Lr2PlayHistorySchemaService().Uninstall(
                    scoreDbPath,
                    isLr2LinkedProfile: true,
                    Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers);
                Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, resetResult.Status);

                var dialogs = new DangerDialogService
                {
                    ImmediateConfirmationResult = accept ? MessageBoxResult.OK : MessageBoxResult.Cancel
                };
                DangerDialogContext context = CreateDangerDialog(
                    values,
                    dialogs,
                    new DangerSchemaDialogPort(),
                    new DangerApplicationDataStore());
                bool draftValue = !context.Settings.ShowRecommUpdatedMsg;
                context.Settings.ShowRecommUpdatedMsg = draftValue;
                window = new SettingsWindow { DataContext = context.Settings };
                windowTest.ShowAndWaitForContentRendered(window);

                var generalPage = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                SettingsField schemaField = FindDescendants<SettingsField>(generalPage)
                    .Single(field => Equals(field.Header, Resources.Lr2_play_history_schema_label));
                SettingsStatusBanner schemaBanner = FindDescendants<SettingsStatusBanner>(schemaField)
                    .Single(candidate => candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                        == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaStatusText));
                Button installOrRepairButton = FindDescendants<Button>(schemaField)
                    .Single(candidate => candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path
                        == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaInstallOrRepairButtonText));

                Assert.IsTrue(installOrRepairButton.IsEnabled,
                    "The compiled General page install/repair button must be enabled for a NotInstalled schema.");
                installOrRepairButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, installOrRepairButton));

                PumpUntil(window, () => dialogs.ConfirmationCount == 1,
                    "The General page install/repair click did not reach its confirmation boundary.");
                Assert.IsNotNull(dialogs.LastConfirmationRequest);
                StringAssert.Contains(dialogs.LastConfirmationRequest.MessageBoxText, scoreDbPath);
                PumpUntil(window, () => ((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled,
                    "The General page install/repair operation gate did not reopen.");
                PumpDispatcher(window.Dispatcher);

                Assert.AreEqual(1, dialogs.ConfirmationCount);
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(draftValue, context.Settings.ShowRecommUpdatedMsg);
                Assert.IsTrue(context.Settings.HasPendingSettingChanges());
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
                    window.CloseForOwnerShutdown();
                }
                Directory.Delete(scope, recursive: true);
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
                ClickAdvancedDangerButton(window, Resources.Lr2_play_history_schema_uninstall);
                PumpUntil(window, () => schemaDialog.Started.Task.IsCompletedSuccessfully,
                    "schema uninstall dialog was not reached");
                Assert.IsFalse(((Grid)window.FindName("settingDialogOperationGrid")).IsEnabled);
                window.Close();
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
                    window.Close();
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
                store,
                applicationLifetime: lifetime);
            SettingsWindow window = null;
            MainWindow owner = null;
            bool hadPreviousViewModelResource = Application.Current.Resources.Contains("vm");
            object previousViewModelResource = hadPreviousViewModelResource
                ? Application.Current.Resources["vm"]
                : null;
            try
            {
                // MainWindow.xaml resolves this compiled resource while the unshown shell is
                // constructed; keep the production shell identity and restore the app scope.
                Application.Current.Resources["vm"] = shellViewModel;
                owner = new MainWindow(
                    shellViewModel,
                        createdWindow =>
                        {
                            window = createdWindow;
                            createdWindow.DataContext = context.Settings;
                        });
                window = owner.CreateSettingsWindowForPresentation();
                Assert.IsNotNull(window);
                Assert.IsFalse(owner.IsVisible);
                Assert.IsFalse(window.IsVisible);
                Assert.IsNull(window.Owner);
                Assert.AreSame(context.Settings, window.DataContext);
                window.Closing += (_, _) => events.Add("settings-closing");
                window.Closed += (_, _) => events.Add("settings-closed");
                ClickAdvancedDangerButton(window, Resources.Settings_uninstall_application_data);
                PumpUntil(
                    owner.Dispatcher,
                    () => lifetime.ShutdownRequestCount == 1,
                    "application-data uninstall did not complete the production shell shutdown route");

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
                        "confirm", "store", "message-success", "message-exit",
                        "settings-closing", "settings-closed", "shutdown-mark:window_close",
                        "settings-save", "shutdown-request"
                    },
                    events);
            }
            finally
            {
                try
                {
                    if (window?.IsVisible == true)
                    {
                        window.CloseForOwnerShutdown();
                    }
                    if (owner?.IsVisible == true)
                    {
                        owner.Close();
                    }
                    context.Settings.Dispose();
                    shellViewModel.SettingDialog.Dispose();
                }
                finally
                {
                    if (hadPreviousViewModelResource)
                    {
                        Application.Current.Resources["vm"] = previousViewModelResource;
                    }
                    else
                    {
                        Application.Current.Resources.Remove("vm");
                    }
                }
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_NavigationHasElevenLocalizedCategoriesInStableOrder()
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
            "Resources.RightClick_actions",
            "Resources.About_this_app"
        ];

        Assert.AreEqual(11, items.Count);
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
    public void GeneralPathStatusBanners_OwnOneVisualGlyphAndExposeMessageAutomation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
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
                string[] statusProperties =
                [
                    nameof(SettingsDialogViewModel.Lr2SongDbPathStatusText),
                    nameof(SettingsDialogViewModel.Lr2ConfigPathStatusText)
                ];

                foreach (string statusProperty in statusProperties)
                {
                    SettingsStatusBanner banner = FindDescendants<SettingsStatusBanner>(page).Single(candidate =>
                        candidate.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path?.Path == statusProperty);
                    string message = banner.Content as string;
                    Assert.IsFalse(string.IsNullOrWhiteSpace(message), statusProperty);
                    Assert.IsFalse(message.StartsWith("✓", StringComparison.Ordinal),
                        statusProperty + " must leave the visual success glyph to the banner icon.");
                    Assert.IsFalse(message.StartsWith("!", StringComparison.Ordinal),
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

    [TestMethod]
    public void Lr2SchemaBanners_ActualPagesRevertSeverityAndAutomationStatusAfterRepairBecomesUnavailable()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            SchemaPresentationContext context = null;
            Settings values = null;
            string scope = null;
            string scoreDbPath = null;
            SettingsWindow window = null;
            ExceptionDispatchInfo bodyFailure = null;
            Exception cleanupFailure = null;
            try
            {
                // Keep the temporary database scope owned by this outer operation. The
                // owner/composition/settings construction below can fail before a
                // SchemaPresentationContext is returned.
                scope = CreateDangerSchemaScope(out values, out scoreDbPath);
                context = CreateSchemaPresentationContext(scope, values, scoreDbPath);
                window = new SettingsWindow { DataContext = context.Settings };
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

                    SetSchemaPresentationStatus(context.StatePort, context.ScoreDbPath, Lr2PlayHistorySchemaStatus.Installed);
                    PumpDispatcher(window.Dispatcher);
                    AssertSchemaBannerState(banner, "i", "Information");

                    SetSchemaPresentationStatus(context.StatePort, context.ScoreDbPath, Lr2PlayHistorySchemaStatus.Repairable);
                    PumpDispatcher(window.Dispatcher);
                    AssertSchemaBannerState(banner, "!", "Warning");

                    SetSchemaPresentationStatus(context.StatePort, context.ScoreDbPath, Lr2PlayHistorySchemaStatus.Installed);
                    PumpDispatcher(window.Dispatcher);
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
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow();
            var sharedDataContext = owner.SettingDialog;
            window.DataContext = sharedDataContext;
            var navigation = (ListBox)window.FindName("settingsNavigation");
            var header = (ContentControl)window.FindName("settingsPageHeader");
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
                typeof(RightClickSettingsPage),
                typeof(AboutSettingsPage)
            ];

            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.AreEqual(0, navigation.SelectedIndex);
                for (int index = 0; index < pageTypes.Length; index++)
                {
                    navigation.SelectedIndex = index;
                    PumpDispatcher(window.Dispatcher);
                    Assert.AreEqual(pageTypes[index], content.Content.GetType());
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
            MainWindow mainWindow = null;
            MainWindowViewModel owner = null;
            CustomTableView mainTable = null;
            CustomTableView playlistSummaryTable = null;
            bool hadPreviousVmResource = Application.Current.Resources.Contains("vm");
            object previousVmResource = hadPreviousVmResource ? Application.Current.Resources["vm"] : null;
            ExceptionDispatchInfo bodyFailure = null;
            Exception cleanupFailure = null;
            try
            {
                var startupEvents = new List<string>();
                var startupLifetime = new DangerApplicationLifetime(startupEvents, firstStartup: true);
                var startupSettingsSession = new DangerSettingsEditSession(new Settings(), startupEvents);
                var composition = new ApplicationComposition(
                    settingsEditSession: startupSettingsSession,
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: startupLifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                owner = composition.CreateMainWindowViewModelForTest();
                Application.Current.Resources["vm"] = owner;
                mainWindow = new MainWindow(owner);
                windowTest.PrepareForOwnedPresentation(mainWindow);
                mainWindow.Show();
                mainWindow.UpdateLayout();
                PumpDispatcher(mainWindow.Dispatcher);
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
                Assert.AreEqual(14d, mainTable.TextFontSize);
                Assert.AreEqual(31d, mainTable.RowHeight);
                Assert.AreEqual(37d, mainTable.HeaderHeight);
                Assert.AreEqual(14d, playlistSummaryTable.TextFontSize);
                Assert.AreEqual(31d, playlistSummaryTable.RowHeight);
                Assert.AreEqual(37d, playlistSummaryTable.HeaderHeight);

                Button reset = FindDescendants<Button>(page)
                    .Single(button => Equals(button.Content, Resources.Appearance_table_reset_defaults));
                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, owner.ViewSettings.CustomTableFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, owner.ViewSettings.CustomTableRowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, owner.ViewSettings.CustomTableHeaderHeight);
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, mainTable.TextFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, mainTable.RowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, mainTable.HeaderHeight);
                Assert.AreEqual(Settings.DefaultCustomTableFontSize, playlistSummaryTable.TextFontSize);
                Assert.AreEqual(Settings.DefaultCustomTableRowHeight, playlistSummaryTable.RowHeight);
                Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, playlistSummaryTable.HeaderHeight);

                ((Button)window.FindName("buttonCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(savedFontSize, owner.ViewSettings.CustomTableFontSize);
                Assert.AreEqual(savedRowHeight, owner.ViewSettings.CustomTableRowHeight);
                Assert.AreEqual(savedHeaderHeight, owner.ViewSettings.CustomTableHeaderHeight);
                Assert.AreEqual(savedFontSize, mainTable.TextFontSize);
                Assert.AreEqual(savedRowHeight, mainTable.RowHeight);
                Assert.AreEqual(savedHeaderHeight, mainTable.HeaderHeight);
                Assert.AreEqual(savedFontSize, playlistSummaryTable.TextFontSize);
                Assert.AreEqual(savedRowHeight, playlistSummaryTable.RowHeight);
                Assert.AreEqual(savedHeaderHeight, playlistSummaryTable.HeaderHeight);

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
                if (mainWindow?.IsVisible == true && owner != null)
                {
                    try
                    {
                        CloseMainWindowThroughShutdownWorkflow(mainWindow, owner);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
                try
                {
                    if (hadPreviousVmResource)
                    {
                        Application.Current.Resources["vm"] = previousVmResource;
                    }
                    else
                    {
                        Application.Current.Resources.Remove("vm");
                    }
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
                PumpDispatcher(window.Dispatcher);

                Assert.IsTrue(contentRendered,
                    "The dispatcher barrier must observe the delayed ContentRendered callback after close.");
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
        IApplicationLifetimePort applicationLifetime = null)
    {
        int reloadCount = 0;
        var statePort = new DangerStatePort(
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
        ((ListBox)window.FindName("settingsNavigation")).SelectedIndex = 8;
        PumpDispatcher(window.Dispatcher);
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
        Lr2PlayHistorySchemaStatusSnapshot snapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(new Lr2PlayHistorySchemaCheckResult
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
                ((Button)reopenedPresentation.FindName("buttonCancel"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(
                    reopenedPresentation,
                    () => !reopenedPresentation.IsVisible,
                    "Reopened SettingsWindow did not close through the actual Cancel route.");
                Assert.AreEqual(2, presentationPort.CloseRequestCount);
                Assert.AreEqual(SettingsWindowCloseReason.Cancel, reopenedPresentation.CloseReason);
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
        // The public invalid-URI setters deliberately display a modal error through the
        // non-injectable legacy message boundary. Seed only this transient presentation
        // state so the lifecycle test can observe reopen cleanup without introducing a
        // second UI dialog or a production-only test seam.
        typeof(SettingsDialogViewModel)
            .GetField("tableListUriValidationMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, "invalid table URI");
        typeof(SettingsDialogViewModel)
            .GetField("playlistMd5UrlMappingTsvUriValidationMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, "invalid mapping URI");
    }

    private static void CloseMainWindowThroughShutdownWorkflow(MainWindow window, MainWindowViewModel viewModel)
    {
        Task closeRequest = viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
        PumpUntil(
            window.Dispatcher,
            () => closeRequest.IsCompleted,
            "The shell shutdown workflow did not complete within the bounded UI pump.");
        closeRequest.GetAwaiter().GetResult();
        window.Close();
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
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
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
                var materializedControlTypes = new HashSet<Type>();

                for (int pageIndex = 0; pageIndex < navigation.Items.Count; pageIndex++)
                {
                    navigation.SelectedIndex = pageIndex;
                    PumpDispatcher(window.Dispatcher);
                    var page = (FrameworkElement)pageHost.Content;
                    Assert.IsFalse(FindDescendants<GroupBox>(page).Any(), page.GetType().Name);
                    Assert.IsFalse(FindDescendants<Expander>(page).Any(), page.GetType().Name);

                    foreach (DependencyObject control in FindDescendants<DependencyObject>(page))
                    {
                        if (control is SettingsSection
                            or SettingsField
                            or SettingsOptionRow
                            or SettingsPathPicker
                            or SettingsListEditor
                            or SettingsStatusBanner)
                        {
                            materializedControlTypes.Add(control.GetType());
                        }
                    }
                }

                foreach (Type requiredType in new[]
                {
                    typeof(SettingsSection),
                    typeof(SettingsField),
                    typeof(SettingsOptionRow),
                    typeof(SettingsPathPicker),
                    typeof(SettingsListEditor),
                    typeof(SettingsStatusBanner)
                })
                {
                    Assert.IsTrue(materializedControlTypes.Contains(requiredType),
                        $"The rendered settings pages did not materialize {requiredType.Name}.");
                }

                FrameworkPropertyMetadata pathMetadata = (FrameworkPropertyMetadata)SettingsPathPicker.PathProperty.GetMetadata(typeof(SettingsPathPicker));
                Assert.IsTrue(pathMetadata.BindsTwoWayByDefault);
                Assert.AreEqual(true, SettingsPathPicker.IsPathReadOnlyProperty.DefaultMetadata.DefaultValue);

                navigation.SelectedIndex = 0;
                PumpDispatcher(window.Dispatcher);
                var generalPage = (FrameworkElement)pageHost.Content;
                SettingsPathPicker pathPicker = FindDescendants<SettingsPathPicker>(generalPage)
                    .First(picker => !string.IsNullOrWhiteSpace(picker.Label));
                TextBox pathEditor = FindDescendants<TextBox>(pathPicker).Single();
                Button browseButton = FindDescendants<Button>(pathPicker)
                    .Single(button => Equals(button.Content, pathPicker.BrowseText));
                Assert.AreEqual(pathPicker.Label, AutomationProperties.GetName(pathEditor));
                Assert.AreEqual(pathPicker.BrowseText, AutomationProperties.GetName(browseButton));

                navigation.SelectedIndex = 3;
                PumpDispatcher(window.Dispatcher);
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
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var startupEvents = new List<string>();
            var startupLifetime = new DangerApplicationLifetime(startupEvents, firstStartup: true);
            var startupSettings = new Settings();
            var startupSettingsSession = new DangerSettingsEditSession(startupSettings, startupEvents);
            var composition = new ApplicationComposition(
                settingsEditSession: startupSettingsSession,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: startupLifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
            bool hadPreviousVmResource = Application.Current.Resources.Contains("vm");
            object previousVmResource = hadPreviousVmResource
                ? Application.Current.Resources["vm"]
                : null;
            Application.Current.Resources["vm"] = viewModel;
            MainWindow owner = null;
            var settingsWindows = new List<SettingsWindow>();
            Exception interactionFailure = null;
            bool settingsPresented = false;
            ExceptionDispatchInfo bodyFailure = null;
            Exception cleanupFailure = null;

            void QueueSettingsPresentationAndClose()
            {
                owner.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    (Action)(() =>
                    {
                        SettingsWindow settingsWindow = settingsWindows.LastOrDefault();
                        try
                        {
                            Assert.IsNotNull(settingsWindow, "The settings window factory did not create a window before the modal dispatcher turn.");
                            Assert.IsTrue(settingsWindow.IsVisible);
                            Assert.AreSame(owner, settingsWindow.Owner);
                            Assert.AreSame(viewModel.SettingDialog, settingsWindow.DataContext);
                            Assert.AreSame(viewModel.PlaybackPanel, settingsWindow.PlaybackPanel);
                            Assert.AreSame(viewModel.PlaylistWorkspace, settingsWindow.PlaylistWorkspace);
                            Assert.AreEqual(Visibility.Visible, owner.PlaybackOverlayVisibility);
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
                owner = new MainWindow(
                    viewModel,
                    settingsWindow =>
                    {
                        settingsWindows.Add(settingsWindow);
                        windowTest.PrepareForOwnedPresentation(settingsWindow);
                    });
                // MainWindow restores its persisted placement during SourceInitialized,
                // so the shell itself is only a coordinator owner here; the settings
                // modal is the presentation whose non-activating policy is asserted.
                windowTest.PrepareForOwnedPresentation(owner);
                owner.Show();
                owner.UpdateLayout();
                Visibility previousOverlayVisibility = owner.PlaybackOverlayVisibility;

                QueueSettingsPresentationAndClose();
                viewModel.SettingDialog.OpenCommand.Execute();
                if (interactionFailure != null)
                {
                    throw new AssertFailedException("The first composed settings modal interaction failed.", interactionFailure);
                }
                Assert.IsTrue(settingsPresented);
                Assert.AreEqual(previousOverlayVisibility, owner.PlaybackOverlayVisibility);

                settingsPresented = false;
                QueueSettingsPresentationAndClose();
                viewModel.SettingDialog.OpenCommand.Execute();
                if (interactionFailure != null)
                {
                    throw new AssertFailedException("The second composed settings modal interaction failed.", interactionFailure);
                }
                Assert.IsTrue(settingsPresented);
                Assert.AreEqual(previousOverlayVisibility, owner.PlaybackOverlayVisibility);
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

                    if (owner?.IsVisible == true)
                    {
                        CloseMainWindowThroughShutdownWorkflow(owner, viewModel);
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
                        if (hadPreviousVmResource)
                        {
                            Application.Current.Resources["vm"] = previousVmResource;
                        }
                        else
                        {
                            Application.Current.Resources.Remove("vm");
                        }
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

    [TestMethod]
    public void MainWindow_SettingsPresentationFailureRestoresPlaybackOverlayVisibility()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var startupEvents = new List<string>();
            var startupLifetime = new DangerApplicationLifetime(startupEvents, firstStartup: true);
            var startupSettingsSession = new DangerSettingsEditSession(new Settings(), startupEvents);
            var composition = new ApplicationComposition(
                settingsEditSession: startupSettingsSession,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: startupLifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
            bool hadPreviousVmResource = Application.Current.Resources.Contains("vm");
            object previousVmResource = hadPreviousVmResource
                ? Application.Current.Resources["vm"]
                : null;
            Application.Current.Resources["vm"] = viewModel;
            MainWindow owner = null;
            SettingsWindow createdSettingsWindow = null;
            SettingsWindow presentedSettingsWindow = null;
            Exception interactionFailure = null;
            bool secondPresentationObserved = false;
            int settingsWindowCreationCount = 0;
            var sentinel = new InvalidOperationException("settings presentation sentinel");
            ExceptionDispatchInfo bodyFailure = null;
            Exception cleanupFailure = null;

            try
            {
                owner = new MainWindow(
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
                        owner.Dispatcher.BeginInvoke(
                            DispatcherPriority.ApplicationIdle,
                            (Action)(() =>
                            {
                                try
                                {
                                    Assert.IsTrue(settingsWindow.IsVisible);
                                    Assert.AreSame(owner, settingsWindow.Owner);
                                    Assert.AreSame(viewModel.SettingDialog, settingsWindow.DataContext);
                                    Assert.AreEqual(Visibility.Visible, owner.PlaybackOverlayVisibility);
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
                    });
                windowTest.PrepareForOwnedPresentation(owner);
                owner.Show();
                owner.UpdateLayout();
                Visibility previousOverlayVisibility = owner.PlaybackOverlayVisibility;

                InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                    () => viewModel.SettingDialog.OpenCommand.Execute());

                StringAssert.Contains(failure.Message, "Settings window failed: Failed");
                Assert.AreSame(sentinel, failure.InnerException);
                Assert.AreEqual(previousOverlayVisibility, owner.PlaybackOverlayVisibility);
                Assert.IsNotNull(createdSettingsWindow);
                Assert.IsFalse(createdSettingsWindow.IsVisible);

                viewModel.SettingDialog.OpenCommand.Execute();
                if (interactionFailure != null)
                {
                    throw new AssertFailedException("The settings modal did not recover after its first factory failure.", interactionFailure);
                }

                Assert.IsTrue(secondPresentationObserved);
                Assert.IsNotNull(presentedSettingsWindow);
                Assert.AreNotSame(createdSettingsWindow, presentedSettingsWindow);
                Assert.AreEqual(SettingsWindowCloseReason.Presentation, presentedSettingsWindow.CloseReason);
                Assert.IsNull(presentedSettingsWindow.DataContext);
                Assert.AreEqual(previousOverlayVisibility, owner.PlaybackOverlayVisibility);
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

                    if (owner?.IsVisible == true)
                    {
                        CloseMainWindowThroughShutdownWorkflow(owner, viewModel);
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
                        if (hadPreviousVmResource)
                        {
                            Application.Current.Resources["vm"] = previousVmResource;
                        }
                        else
                        {
                            Application.Current.Resources.Remove("vm");
                        }
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

    [TestMethod]
    public void NativeClose_WhenCancellationIsEnabled_RequestsCancelCompletion()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };

            window.Close();
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

            window.Close();
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

                window.Close();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.AreEqual(0, presentation.CloseRequestCount);

                operationCompletion.SetResult();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.IsTrue(operation.IsCompletedSuccessfully);

                window.Close();
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
                window.Close();
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

    private static void EnsureCanonicalApplicationResources()
    {
        Application application = Application.Current;
        Assert.IsNotNull(application);
        AddCanonicalResourceIfMissing(
            application,
            "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalDialogStyles.xaml");
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

    private static bool FindResourceInStyleScope(Border element, string key)
    {
        if (element.TryFindResource(key) is not Style expectedStyle)
        {
            return false;
        }

        for (Style candidate = element.Style; candidate != null; candidate = candidate.BasedOn)
        {
            if (ReferenceEquals(candidate, expectedStyle))
            {
                return true;
            }
        }

        return false;
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

    private static void SetEditCompletionInProgress(SettingsDialogViewModel viewModel, bool value)
    {
        // This fixture isolates SettingsWindow's close-reason gate. The ViewModel setter is
        // private by design; driving a full apply would add persistence/reload behavior to a
        // test that only needs the already-observable completion state.
        typeof(SettingsDialogViewModel)
            .GetField("isEditCompletionInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
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

    private sealed class SchemaPresentationStatePort : ISettingsDialogStatePort
    {
        internal SettingsDialogViewModel LastSettingsDialog { get; set; }

        public bool HasActiveLibraryProfile => false;

        public bool IsLibraryOperationInProgress => false;

        public Task<bool> InitializeLibraryAsync() => Task.FromResult(true);

        public Task ReloadScoresOnlyAsync() => Task.CompletedTask;

        public Task ReloadFileDiffAsync() => Task.CompletedTask;

        public event EventHandler LibraryOperationAvailabilityChanged
        {
            add { }
            remove { }
        }

        public event Action<Lr2PlayHistorySchemaStatusSnapshot> Lr2PlayHistorySchemaStatusChanged;

        internal void NotifyLr2PlayHistorySchemaStatusChanged(Lr2PlayHistorySchemaStatusSnapshot snapshot)
            => Lr2PlayHistorySchemaStatusChanged?.Invoke(snapshot);
    }

    private sealed class DangerStatePort : ISettingsDialogStatePort
    {
        private readonly Func<Task> reloadScoresOnly;

        internal DangerStatePort(Func<Task> reloadScoresOnly)
        {
            this.reloadScoresOnly = reloadScoresOnly ?? throw new ArgumentNullException(nameof(reloadScoresOnly));
        }

        public bool HasActiveLibraryProfile => true;

        public bool IsLibraryOperationInProgress => false;

        public Task<bool> InitializeLibraryAsync() => Task.FromResult(true);

        public Task ReloadScoresOnlyAsync() => reloadScoresOnly();

        public Task ReloadFileDiffAsync() => Task.CompletedTask;

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

        public void CompleteFirstStartup()
        {
            firstStartup = false;
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
        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            Values.OperationModeLR2DB = operationMode;
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

        internal string LastInvalidationReason { get; private set; }

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

        internal MessageBoxResult ImmediateConfirmationResult { get; set; } = MessageBoxResult.OK;

        internal TaskCompletionSource ConfirmationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConfirmationCount { get; private set; }

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; }

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

    private sealed class RecordingSettingsRouteDialogService : IUiDialogService
    {
        private readonly Queue<string> folderPaths;

        internal RecordingSettingsRouteDialogService(params string[] folderPaths)
        {
            this.folderPaths = new Queue<string>(folderPaths);
        }

        internal Window ExpectedOwner { get; set; }

        internal List<UiFolderPickerRequest> FolderRequests { get; } = [];

        internal Queue<UiFolderPickerResult> FolderResults { get; } = [];

        internal Queue<Task<UiFolderPickerResult>> FolderTasks { get; } = [];

        internal List<UiSaveFilePickerRequest> SaveFileRequests { get; } = [];

        internal List<UiFilePickerRequest> FileRequests { get; } = [];

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);

        internal UiSaveFilePickerResult SaveFileResult { get; set; } = new(UiDialogStatus.CancelledByUser);

        internal UiFilePickerResult FileResult { get; set; } = new(UiDialogStatus.CancelledByUser);

        internal Type LastWindowType { get; private set; }

        internal Window LastCreatedWindow { get; private set; }

        internal Window LastWindowOwner { get; private set; }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            ConfirmationRequests.Add(request);
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
