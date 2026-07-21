using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingDialogEditCompletionTests
{
    [TestMethod]
    public async Task ApplySettingsAsync_ChangedNormalSetting_SavesBeforeClosing()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            dialog.PresentationRequested += (_, request) => sequence.Add(request.Kind.ToString());
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "CloseOverlay" }, sequence);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_ActiveProfileWithoutChanges_PublishesSingleCloseRequest()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            var requests = new List<MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind>();
            dialog.PresentationRequested += (_, request) => requests.Add(request.Kind);

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[] { MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay },
                requests);
            Assert.IsFalse(dialog.IsEditCompletionInProgress);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SettingDialogOkClick_AwaitsOwnerCompletionBeforeClosing()
    {
        RunOnStaDispatcherThread(() =>
        {
            Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
            SynchronizationContext previousSynchronizationContext = SynchronizationContext.Current;
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            DispatcherHelper.UIDispatcher = dispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            string root = CreateTemporaryRoot();
            try
            {
                var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
                using var reloadStarted = new ManualResetEventSlim();
                var reloadRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                MainWindowViewModel viewModel = new ApplicationComposition(
                    firstStartupProvider: () => false,
                    completeFirstStartup: () => { },
                    reloadSettings: settingsSession.Reload,
                    saveSettings: settingsSession.Save,
                    settingsEditSession: settingsSession,
                    reloadScoresOnly: _ =>
                    {
                        reloadStarted.Set();
                        return reloadRelease.Task;
                    })
                    .CreateMainWindowViewModel();
                SetActiveLibraryProfile(viewModel, true);
                MainWindowViewModel.SettingDialogViewModel settingDialogViewModel = viewModel.settingDialog;
                settingDialogViewModel.BeatorajaPlayerId = "player2";
                var settingDialog = new SettingDialog
                {
                    DataContext = viewModel
                };
                Button button = (Button)typeof(SettingDialog)
                    .GetField("buttonOK", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(settingDialog)!;
                using var closeRequestObserved = new ManualResetEventSlim();
                DispatcherFrame? frame = null;
                settingDialogViewModel.PresentationRequested += (_, request) =>
                {
                    if (request.Kind == MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay)
                    {
                        closeRequestObserved.Set();
                        if (frame != null)
                        {
                            frame.Continue = false;
                        }
                    }
                };

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

                Assert.IsTrue(reloadStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(settingDialogViewModel.IsEditCompletionInProgress);
                Assert.IsFalse(closeRequestObserved.IsSet);

                reloadRelease.SetResult(true);
                frame = new DispatcherFrame();
                var timeoutTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.ApplicationIdle, (_, _) => frame.Continue = false, dispatcher);
                timeoutTimer.Start();
                Dispatcher.PushFrame(frame);
                timeoutTimer.Stop();
                Assert.IsTrue(closeRequestObserved.IsSet);
                Assert.IsFalse(settingDialogViewModel.IsEditCompletionInProgress);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
                DispatcherHelper.UIDispatcher = previousDispatcher;
                SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
            }
        });
    }

    [TestMethod]
    public async Task ApplySettingsAsync_ScoreSourceChange_AwaitsReloadBeforeClosing()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            using var reloadStarted = new ManualResetEventSlim();
            var reloadRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadScoresOnly: _ =>
                {
                    sequence.Add("reload-start");
                    reloadStarted.Set();
                    return reloadRelease.Task.ContinueWith(
                        _ => sequence.Add("reload-completed"),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                });
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            dialog.PresentationRequested += (_, request) => sequence.Add(request.Kind.ToString());
            dialog.BeatorajaPlayerId = "player2";

            Task applyTask = dialog.ApplySettingsAsync();
            reloadStarted.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Assert.IsFalse(applyTask.IsCompleted);
            CollectionAssert.DoesNotContain(sequence, "CloseOverlay");

            reloadRelease.SetResult(true);
            await applyTask;

            Assert.IsTrue(sequence.Count >= 4, string.Join("|", sequence));
            Assert.AreEqual("save", sequence[0]);
            Assert.AreEqual("reload-start", sequence[1]);
            Assert.AreEqual("reload-completed", sequence[2]);
            Assert.AreEqual("CloseOverlay", sequence[3]);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_ScoreReloadFailureKeepsChangesForRetry()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            int reloadCount = 0;
            settingsSession.SaveObserved = () => sequence.Add("save");
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadScoresOnly: _ =>
                {
                    reloadCount++;
                    sequence.Add("reload-" + reloadCount);
                    return reloadCount == 1
                        ? Task.FromException(new InvalidOperationException("score reload failed"))
                        : Task.CompletedTask;
                },
                reportSettingsApplyFailure: _ => sequence.Add("failure"));
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            var requests = new List<MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind>();
            dialog.PresentationRequested += (_, request) => requests.Add(request.Kind);
            dialog.BeatorajaPlayerId = "player2";

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsTrue(dialog.HasPendingSettingChanges(), string.Join("|", sequence));
            CollectionAssert.DoesNotContain(
                requests,
                MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
            Assert.IsFalse(dialog.IsEditCancellationEnabled);

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "reload-1", "failure", "reload-2" }, sequence);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.AreEqual(2, reloadCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            CollectionAssert.Contains(
                requests,
                MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_FolderChange_AwaitsFileDiffBeforeClosing()
    {
        string root = CreateTemporaryRoot();
        string addedRoot = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            using var reloadStarted = new ManualResetEventSlim();
            var reloadRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadFileDiff: _ =>
                {
                    sequence.Add("reload-start");
                    reloadStarted.Set();
                    return reloadRelease.Task.ContinueWith(
                        _ => sequence.Add("reload-completed"),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                });
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            dialog.PresentationRequested += (_, request) => sequence.Add(request.Kind.ToString());
            dialog.StandaloneBmsRootPathList.Add(addedRoot);

            Task applyTask = dialog.ApplySettingsAsync();
            reloadStarted.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Assert.IsFalse(applyTask.IsCompleted);
            CollectionAssert.DoesNotContain(sequence, "CloseOverlay");

            reloadRelease.SetResult(true);
            await applyTask;

            CollectionAssert.AreEqual(
                new[] { "save", "reload-start", "reload-completed", "CloseOverlay" },
                sequence,
                string.Join("|", sequence));
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(addedRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_FolderDiffFailureKeepsOverlayOpen()
    {
        string root = CreateTemporaryRoot();
        string addedRoot = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            settingsSession.SaveObserved = () => sequence.Add("save");
            int reloadCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                reloadFileDiff: _ =>
                {
                    reloadCount++;
                    sequence.Add("reload-" + reloadCount);
                    return reloadCount == 1
                        ? Task.FromException(new InvalidOperationException("file diff failed"))
                        : Task.CompletedTask;
                },
                reportSettingsApplyFailure: _ => sequence.Add("failure"));
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            dialog.StandaloneBmsRootPathList.Add(addedRoot);
            var requests = new List<MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind>();
            dialog.PresentationRequested += (_, request) => requests.Add(request.Kind);

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(new[] { "save", "reload-1", "failure" }, sequence);
            Assert.IsTrue(dialog.HasPendingSettingChanges(), string.Join("|", sequence));
            Assert.IsFalse(dialog.IsEditCancellationEnabled);
            CollectionAssert.DoesNotContain(
                requests,
                MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay);

            await dialog.ApplySettingsAsync();

            CollectionAssert.AreEqual(
                new[] { "save", "reload-1", "failure", "reload-2" },
                sequence,
                string.Join("|", sequence));
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
            CollectionAssert.Contains(
                requests,
                MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(addedRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_InitialRetryAfterFullReloadClearsScoreReloadPending()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            int reloadCount = 0;
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    return Task.FromResult(initializeCount > 1);
                },
                reloadScoresOnly: _ =>
                {
                    reloadCount++;
                    return reloadCount == 1
                        ? Task.FromException(new InvalidOperationException("score reload failed"))
                        : Task.CompletedTask;
                },
                reportSettingsApplyFailure: _ => { });
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            dialog.BeatorajaPlayerId = "player2";

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, reloadCount);
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            SetPrivateField(dialog, "tempOperationModeLR2DB", !dialog.OperationModeLR2DB);
            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, initializeCount);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(2, initializeCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCancellationEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_FirstStartupValidationFailureCompletesFalseAndRequestsInitialSetup()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings invalidSettings = CreateValidStandaloneSettings(root);
            invalidSettings.BMSRootPath = string.Empty;
            invalidSettings.StandaloneBmsRootPaths = string.Empty;
            var settingsSession = new CountingSettingsEditSession(invalidSettings);
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: true);
            int initialSetupRequestCount = 0;
            viewModel.InitialSetupLanguageDialogRequested += (_, _) => initialSetupRequestCount++;

            bool initialized = await viewModel.InitializeAsync();

            Assert.IsFalse(initialized);
            Assert.AreEqual(1, initialSetupRequestCount);
            Assert.IsFalse(viewModel.IsStartupUiInteractionBlocked);
            Assert.IsFalse(viewModel.IsInitializationCompleted);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_InitialSettings_SavesClosesAndInitializes()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            var sequence = new List<string>();
            using var initializationStarted = new ManualResetEventSlim();
            var initializationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    sequence.Add("initialize-start");
                    initializationStarted.Set();
                    return initializationRelease.Task.ContinueWith(
                        _ =>
                        {
                            sequence.Add("initialize-completed");
                            return true;
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                });
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            settingsSession.SaveObserved = () => sequence.Add("save");
            dialog.PresentationRequested += (_, request) => sequence.Add(request.Kind.ToString());
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
            Assert.IsTrue(dialog.CheckValidation(out string validationError), validationError);

            Task applyTask = dialog.ApplySettingsAsync();
            initializationStarted.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Assert.IsFalse(applyTask.IsCompleted);
            initializationRelease.SetResult(true);
            await applyTask;

            CollectionAssert.AreEqual(
                new[] { "save", "initialize-start", "initialize-completed", "CloseOverlay" },
                sequence);
            Assert.AreEqual(1, initializeCount);
            Assert.AreEqual(1, settingsSession.SaveCount);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_CompletionGuardRejectsConcurrentCall()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root))
            {
                BlockSave = true
            };
            MainWindowViewModel viewModel = CreateViewModel(settingsSession, firstStartup: false);
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            var requests = new List<MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind>();
            dialog.PresentationRequested += (_, request) => requests.Add(request.Kind);
            dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;

            Task first = Task.Run(() => dialog.ApplySettingsAsync());
            settingsSession.SaveEntered.Wait();
            Assert.IsTrue(dialog.IsEditCompletionInProgress);
            Task second = dialog.ApplySettingsAsync();
            dialog.CancelCommand.Execute();
            settingsSession.ReleaseSave.Set();
            await Task.WhenAll(first, second);

            Assert.AreEqual(1, settingsSession.SaveCount);
            CollectionAssert.AreEqual(
                new[] { MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay },
                requests);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_FullRestartFailureKeepsOverlayOpen()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    return Task.FromResult(initializeCount != 1);
                });
            SetActiveLibraryProfile(viewModel, true);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            var requests = new List<MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind>();
            dialog.PresentationRequested += (_, request) => requests.Add(request.Kind);
            SetPrivateField(dialog, "tempOperationModeLR2DB", !dialog.OperationModeLR2DB);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, initializeCount);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
            CollectionAssert.DoesNotContain(
                requests,
                MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(2, initializeCount);
            CollectionAssert.Contains(
                requests,
                MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplySettingsAsync_InitialInitializationFailureKeepsOverlayOpen()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var settingsSession = new CountingSettingsEditSession(CreateValidStandaloneSettings(root));
            int initializeCount = 0;
            MainWindowViewModel viewModel = CreateViewModel(
                settingsSession,
                firstStartup: false,
                initializeOwner: _ =>
                {
                    initializeCount++;
                    return Task.FromResult(false);
                });
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            var requests = new List<MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind>();
            dialog.PresentationRequested += (_, request) => requests.Add(request.Kind);

            await dialog.ApplySettingsAsync();

            Assert.AreEqual(1, initializeCount);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
            CollectionAssert.DoesNotContain(
                requests,
                MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay);
            Assert.IsTrue(dialog.IsEditCompletionEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MainWindowViewModel CreateViewModel(
        CountingSettingsEditSession settingsSession,
        bool firstStartup,
        Func<MainWindowViewModel, Task<bool>>? initializeOwner = null,
        Func<MainWindowViewModel, Task>? reloadScoresOnly = null,
        Action<Exception>? reportSettingsApplyFailure = null,
        Func<MainWindowViewModel, Task>? reloadFileDiff = null)
    {
        return new ApplicationComposition(
            firstStartupProvider: () => firstStartup,
            completeFirstStartup: () => { },
            reloadSettings: settingsSession.Reload,
            saveSettings: settingsSession.Save,
            settingsEditSession: settingsSession,
            initializeOwner: initializeOwner,
            reloadScoresOnly: reloadScoresOnly,
            reportSettingsApplyFailure: reportSettingsApplyFailure,
            reloadFileDiff: reloadFileDiff)
            .CreateMainWindowViewModel();
    }

    private static Settings CreateValidStandaloneSettings(string root)
    {
        var settings = new Settings
        {
            OperationModeLR2DB = false,
            BMSRootPath = root,
            StandaloneBmsRootPaths = root,
            BMSInstallDir = root,
            TableListURL = new Uri("http://127.0.0.1:1/table-list.json"),
            EnablePlaylistUrlCompletion = false,
            ScanBmsFilesOnStartup = false,
            SkipInitPlaylistLoad = true,
            UseBeatorajaScoreDb = false,
            EnableBeatorajaBmtOutput = false,
            UseExternalPanelImage = false,
            UsePlayeruBMplay = false,
            UsePlayerLR2body = false,
            UsePlayerBMIIDXView = false,
            IsLR2BackupEnabled = false
        };
        return settings;
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialogEditCompletion_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void SetActiveLibraryProfile(MainWindowViewModel viewModel, bool value)
    {
        typeof(MainWindowViewModel)
            .GetField("hasActiveLibraryProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static void RunOnStaDispatcherThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                Dispatcher dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private sealed class CountingSettingsEditSession : ISettingsEditSession
    {
        internal CountingSettingsEditSession(Settings values)
        {
            Values = values;
        }

        internal Action? SaveObserved { get; set; }

        internal bool BlockSave { get; set; }

        internal ManualResetEventSlim SaveEntered { get; } = new(false);

        internal ManualResetEventSlim ReleaseSave { get; } = new(false);

        internal int SaveCount { get; private set; }

        public Settings Values { get; }

        public void Reload()
        {
        }

        public void Save()
        {
            SaveCount++;
            SaveObserved?.Invoke();
            if (BlockSave)
            {
                SaveEntered.Set();
                ReleaseSave.Wait();
            }
        }
    }
}
