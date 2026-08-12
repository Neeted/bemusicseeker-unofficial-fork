using System.Threading.Tasks;
using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.ComponentModel;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ShellShutdownWorkflowOwnerTests
{
    [TestMethod]
    public async Task RepeatedWindowCloseRequestsShareOnePreparationAndCompletion()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);

        Task<ShellShutdownWorkflowCompletionReceipt> first = owner.RequestWindowCloseAsync();
        Task<ShellShutdownWorkflowCompletionReceipt> second = owner.RequestWindowCloseAsync();

        Assert.AreSame(first, second);
        ShellShutdownWorkflowCompletionReceipt receipt = await first;

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(receipt.CloseAllowed);
        Assert.IsTrue(owner.IsShutdownRequested);
        Assert.IsTrue(owner.IsShutdownPrepared);
    }

    [TestMethod]
    public async Task StartupUpdatePreparationAndWindowCloseShareCanonicalPreparation()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);

        Task<ShutdownPreparationResult> updatePreparation = owner.PrepareForStartupUpdateAsync("update");
        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();

        ShutdownPreparationResult updateResult = await updatePreparation;
        ShellShutdownWorkflowCompletionReceipt closeResult = await close;

        Assert.AreEqual("update", updateResult.Reason);
        Assert.IsTrue(closeResult.PreparationSucceeded);
        Assert.IsTrue(owner.IsShutdownPrepared);
    }

    [TestMethod]
    public async Task CoordinatedShutdownIsMarkedBeforePreparationCompletes()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        bool marked = false;
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel, markShutdown: _ => marked = true);

        ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(marked);
    }

    [TestMethod]
    public async Task TerminalResourceCleanupIsIdempotentAfterClosePreparation()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var settingsSession = new RecordingSettingsEditSession();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel, settingsEditSession: settingsSession);

        await owner.RequestWindowCloseAsync();
        viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
        owner.CompleteTerminalShutdown();
        owner.CompleteTerminalShutdown();

        Assert.AreEqual(1, settingsSession.SaveCount);
        Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
    }

    [TestMethod]
    public void TerminalSettingsAreSavedBeforePlayerCleanupAndOnlyOnce()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var events = new List<string>();
        var settingsSession = new RecordingSettingsEditSession(() => events.Add("settings_save"));
        var player = new FakeBmsPlayer(() => events.Add("player_close"));
        FieldInfo playerField = typeof(PlaybackPanelViewModel)
            .GetField("bmsPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(playerField);
        playerField.SetValue(viewModel.PlaybackPanel, player);

        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            settingsEditSession: settingsSession);

        owner.CompleteTerminalShutdown();
        owner.CompleteTerminalShutdown();

        Assert.AreEqual(1, settingsSession.SaveCount);
        Assert.AreEqual(1, player.CloseProcessCount);
        Assert.IsTrue(events.IndexOf("settings_save") < events.IndexOf("player_close"));
    }

    [TestMethod]
    public void TerminalApplicationShutdownIsExplicitAndRequestedOnlyOnceAfterCleanup()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var events = new List<string>();
        var settingsSession = new RecordingSettingsEditSession(() => events.Add("settings_save"));
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            settingsEditSession: settingsSession,
            requestApplicationShutdown: () => events.Add("application_shutdown"));

        owner.CompleteTerminalShutdown();
        CollectionAssert.AreEqual(new[] { "settings_save" }, events);

        owner.RequestTerminalApplicationShutdown();
        owner.RequestTerminalApplicationShutdown();

        CollectionAssert.AreEqual(new[] { "settings_save", "application_shutdown" }, events);
    }

    [TestMethod]
    public void TerminalSettingsSaveFailureIsWarnedAndCleanupContinues()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var warnings = new List<string>();
        var settingsSession = new RecordingSettingsEditSession
        {
            SaveException = new InvalidOperationException("settings unavailable")
        };
        var player = new FakeBmsPlayer();
        FieldInfo playerField = typeof(PlaybackPanelViewModel)
            .GetField("bmsPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(playerField);
        playerField.SetValue(viewModel.PlaybackPanel, player);

        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            settingsEditSession: settingsSession,
            logShutdownWarning: warnings.Add);

        owner.CompleteTerminalShutdown();
        owner.CompleteTerminalShutdown();

        Assert.AreEqual(1, settingsSession.SaveCount);
        Assert.AreEqual(1, player.CloseProcessCount);
        StringAssert.Contains(string.Join("\n", warnings), "settings_save_failed");
    }

    [TestMethod]
    public async Task StartupUpdateTerminalPathUsesTheSameSettingsSaveOwner()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var settingsSession = new RecordingSettingsEditSession();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            settingsEditSession: settingsSession);

        ShutdownPreparationResult preparation = await owner.PrepareForStartupUpdateAsync("update");
        Assert.AreEqual("update", preparation.Reason);

        owner.CompleteTerminalShutdown();

        Assert.AreEqual(1, settingsSession.SaveCount);
    }

    [TestMethod]
    public async Task DispatchFailureCompletesOnlyAfterShutdownDrainFallback()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        TaskCompletionSource<bool> regularChartStopRelease = PreparePendingRegularChartStop(viewModel);
        int performanceStopCount = 0;
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            action => Task.FromException(new InvalidOperationException("dispatcher stopped")),
            stopPerformanceDiagnostics: () =>
            {
                Interlocked.Increment(ref performanceStopCount);
                return Task.CompletedTask;
            });

        Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("update");
        Assert.IsFalse(preparation.Wait(TimeSpan.FromMilliseconds(100)));
        regularChartStopRelease.SetResult(true);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => preparation);

        Assert.IsTrue(owner.IsShutdownRequested);
        Assert.IsTrue(owner.IsShutdownPrepared);
        Assert.AreEqual(1, performanceStopCount);
        Assert.IsTrue(owner.ConsumeUpdatePreparationFailure());
        ShellShutdownWorkflowCompletionReceipt close = await owner.RequestWindowCloseAsync();
        Assert.IsFalse(close.PreparationSucceeded);
        Assert.IsTrue(close.CloseAllowed);
    }

    [TestMethod]
    public async Task PreparationWaitsForRegularChartStopBeforeAllowingClose()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        TaskCompletionSource<bool> regularChartStopRelease = PreparePendingRegularChartStop(viewModel);
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);

        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();

        Assert.IsFalse(close.Wait(TimeSpan.FromMilliseconds(100)));
        Assert.IsFalse(owner.IsShutdownPrepared);
        regularChartStopRelease.SetResult(true);

        ShellShutdownWorkflowCompletionReceipt receipt = await close;

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(receipt.CloseAllowed);
    }

    [TestMethod]
    public async Task LateAttachedCatalogsReceiveShutdownCancellation()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);

        ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();
        Assert.IsTrue(receipt.PreparationSucceeded);

        BMSLibrary library = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        BMSPlaylist playlist = (BMSPlaylist)FormatterServices.GetUninitializedObject(typeof(BMSPlaylist));
        SetPrivateField(
            playlist,
            "shutdownCoordinator",
            new PlaylistShutdownCoordinator());

        owner.AttachLibrary(library);
        owner.AttachPlaylist(playlist);

        Assert.IsTrue(library.IsShutdownRequested);
        Assert.IsTrue(playlist.IsShutdownRequested);
    }

    [TestMethod]
    public async Task WindowCloseAwaitsPerformanceDiagnosticsDrainOffTerminalExitHandler()
    {
        var drainRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var drainEntered = new ManualResetEventSlim();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            stopPerformanceDiagnostics: () =>
            {
                drainEntered.Set();
                return drainRelease.Task;
            });

        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();

        Assert.IsTrue(drainEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(close.IsCompleted);

        drainRelease.SetResult(true);
        ShellShutdownWorkflowCompletionReceipt receipt = await close;

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(receipt.CloseAllowed);
    }

    [TestMethod]
    public void TerminalCleanupStartsCancellationWhenClosePreparationWasBypassed()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);
        BMSLibrary library = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        BMSPlaylist playlist = (BMSPlaylist)FormatterServices.GetUninitializedObject(typeof(BMSPlaylist));
        SetPrivateField(
            playlist,
            "shutdownCoordinator",
            new PlaylistShutdownCoordinator());

        owner.AttachLibrary(library);
        owner.AttachPlaylist(playlist);
        owner.CompleteTerminalShutdown();

        Assert.IsTrue(library.IsShutdownRequested);
        Assert.IsTrue(playlist.IsShutdownRequested);
    }

    [TestMethod]
    public async Task WindowCloseWaitsForActiveStartupUpdateTerminalBeforePreparation()
    {
        var checkRelease = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var checkEntered = new ManualResetEventSlim();
        var startupUpdate = new StartupUpdateWorkflowOwner(
            () =>
            {
                checkEntered.Set();
                return checkRelease.Task;
            },
            asset => Task.FromResult("package.zip"),
            packagePath => new NoOpPreparedUpdaterLaunch(),
            () => { },
            packagePath => { },
            action => Task.Run(action),
            action => action());
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel, startupUpdate: startupUpdate);

        Assert.IsTrue(startupUpdate.Start());
        Assert.IsTrue(checkEntered.Wait(TimeSpan.FromSeconds(5)));

        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
        Assert.IsFalse(close.Wait(TimeSpan.FromMilliseconds(100)));
        Assert.IsFalse(owner.IsShutdownPreparationStarted);

        checkRelease.SetResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
        ShellShutdownWorkflowCompletionReceipt receipt = await close;

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(owner.IsShutdownPrepared);
        Assert.IsTrue(owner.IsCloseAllowed);
    }

    [TestMethod]
    public async Task StartupUpdatePublishesReadyBeforePreparationAndTerminalBeforeClose()
    {
        var events = new List<string>();
        var startEntered = new ManualResetEventSlim();
        var startRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateCheckResult available = UpdateCheckResult.Available(
            new Version(2, 0, 0, 0),
            "1.0.0.0",
            "2.0.0.0",
            [new UpdateAssetInfo
            {
                Kind = "app",
                Label = "App",
                FileName = "app.zip",
                Url = "https://example.test/app.zip",
                Sha256 = new string('a', 64),
                SizeBytes = 1,
                IncludesChartInfoMetadata = false
            }],
            "https://example.test/releases/v2.0.0.0");
        var startupUpdate = new StartupUpdateWorkflowOwner(
            () => Task.FromResult(available),
            asset =>
            {
                events.Add("download");
                return Task.FromResult("package.zip");
            },
            packagePath => new PreparedUpdaterLaunch(() =>
            {
                events.Add("start");
                startEntered.Set();
                startRelease.Task.GetAwaiter().GetResult();
                return new UpdaterLaunchReceipt();
            }),
            () => { },
            packagePath => { },
            action => Task.Run(action),
            action => action());
        startupUpdate.PresentationRequested += request => request.Complete(available.Assets[0]);
        startupUpdate.TerminalPublished += _ => events.Add("terminal");

        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel, startupUpdate: startupUpdate);
        Assert.IsTrue(startupUpdate.Start());
        Assert.IsTrue(startEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(owner.IsShutdownPreparationStarted);

        startRelease.SetResult(true);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => owner.IsShutdownPrepared && startupUpdate.IsIdle,
            TimeSpan.FromSeconds(5)));

        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
        ShellShutdownWorkflowCompletionReceipt receipt = await close;
        events.Add("close");

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(owner.IsCloseAllowed);
        Assert.IsTrue(events.IndexOf("download") >= 0);
        Assert.IsTrue(events.IndexOf("start") >= 0);
        Assert.IsTrue(events.IndexOf("terminal") >= 0);
        Assert.IsTrue(events.IndexOf("start") < events.IndexOf("terminal"));
        Assert.IsTrue(events.IndexOf("terminal") < events.IndexOf("close"));
    }

    [TestMethod]
    public async Task CoordinatedShutdownFailureStillStartsCancellationFallback()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            action => action(),
            _ => throw new InvalidOperationException("coordinated mark failed"));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.PrepareForStartupUpdateAsync("update"));

        Assert.IsTrue(owner.IsShutdownRequested);
        Assert.IsTrue(owner.IsShutdownPrepared);
        ShellShutdownWorkflowCompletionReceipt close = await owner.RequestWindowCloseAsync();
        Assert.IsFalse(close.PreparationSucceeded);
        Assert.IsTrue(close.CloseAllowed);
    }

    [TestMethod]
    public async Task TerminalCleanupClosesPlaybackPlayerExactlyOnce()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var player = new FakeBmsPlayer();
        FieldInfo playerField = typeof(PlaybackPanelViewModel)
            .GetField("bmsPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(playerField);
        playerField.SetValue(viewModel.PlaybackPanel, player);

        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);
        await owner.RequestWindowCloseAsync();
        owner.CompleteTerminalShutdown();
        owner.CompleteTerminalShutdown();

        Assert.AreEqual(1, player.CloseProcessCount);
    }

    private static ShellShutdownWorkflowOwner CreateDirectOwner(
        MainWindowViewModel viewModel,
        Func<Func<Task>, Task>? dispatch = null,
        Action<string>? markShutdown = null,
        StartupUpdateWorkflowOwner? startupUpdate = null,
        ISettingsEditSession? settingsEditSession = null,
        Action? requestApplicationShutdown = null,
        Func<Task>? stopPerformanceDiagnostics = null,
        Action<string>? logShutdown = null,
        Action<string>? logShutdownWarning = null)
    {
        StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
            viewModel,
            "startupBackgroundTaskScheduler");
        startupUpdate ??= new StartupUpdateWorkflowOwner(
                () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
                asset => Task.FromResult("package.zip"),
                packagePath => new NoOpPreparedUpdaterLaunch(),
                () => { },
                packagePath => { },
                action => Task.Run(action),
                action => action());
        return new ShellShutdownWorkflowOwner(
            startupUpdate,
            new ElevatedProcessWarningWorkflowOwner(() => false),
            scheduler,
            viewModel.RegularChartList,
            viewModel.PlaylistWorkspace,
            viewModel.PlayHistory,
            viewModel.PackageInstallWorkflow,
            viewModel.MaintenanceRescanWorkflow,
            viewModel.FolderAutoRenameWorkflow,
            viewModel.PlaybackPanel,
            settingsEditSession
                ?? GetPrivateField<ApplicationComposition>(viewModel, "applicationComposition").SettingsEditSession,
            new SemaphoreSlim(1, 1),
            viewModel.ProgressHub.StartupProgress,
            markShutdown ?? (_ => { }),
            requestApplicationShutdown ?? (() => { }),
            stopPerformanceDiagnostics ?? (() => Task.CompletedTask),
            dispatch ?? (action => action()),
            logShutdown ?? (_ => { }),
            logShutdownWarning ?? (_ => { }),
            value => value ?? string.Empty);
    }

    private static T GetPrivateField<T>(MainWindowViewModel viewModel, string name)
    {
        return (T)typeof(MainWindowViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(viewModel);
    }

    private static TaskCompletionSource<bool> PreparePendingRegularChartStop(MainWindowViewModel viewModel)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(viewModel.RegularChartList, "virtualOrderPrewarmCompletion", release.Task);
        SetPrivateField(viewModel.RegularChartList, "virtualOrderPrewarmCancellation", new CancellationTokenSource());
        return release;
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, name);
        field.SetValue(target, value);
    }

    private sealed class NoOpPreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        public UpdaterLaunchReceipt Start()
        {
            return new UpdaterLaunchReceipt();
        }
    }

    private sealed class PreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        private readonly Func<UpdaterLaunchReceipt> start;

        internal PreparedUpdaterLaunch(Func<UpdaterLaunchReceipt> start)
        {
            this.start = start;
        }

        public UpdaterLaunchReceipt Start()
        {
            return start();
        }
    }

    private sealed class FakeBmsPlayer : IBMSPlayer
    {
        private readonly Action? onClose;

        internal FakeBmsPlayer(Action? onClose = null)
        {
            this.onClose = onClose;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ExePath { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public TimeSpan CurrentTime { get; set; }
        public TimeSpan StopTime { get; set; }
        public TimeSpan BmsDuration { get; set; }
        public TimeSpan MusicDuration { get; set; }
        public int CurrentVoices { get; set; }
        public int MaxVoices { get; set; }
        public int NoteDensity { get; set; }
        public int NoteDensityMax { get; set; }
        public int Bpm { get; set; }
        public int MinBpm { get; set; }
        public int MaxBpm { get; set; }
        public double Total { get; set; }
        public int Combo { get; set; }
        public int Notes { get; set; }
        public int Measure { get; set; }
        public int LastMeasure { get; set; }
        public int CloseProcessCount { get; private set; }

        public void CloseProcess()
        {
            CloseProcessCount++;
            onClose?.Invoke();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTime)));
        }
        public Task PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null) => Task.CompletedTask;
        public void RestartPlayingBMSfile() { }
        public void PausePlayingBMSfileToggle() { }
        public void FastForwardPlayingBMSfileStart() { }
        public void FastForwardPlayingBMSfileEnd() { }
        public void FastBackwardPlayingBMSfileStart() { }
        public void FastBackwardPlayingBMSfileEnd() { }
        public void ShowInfo() { }
        public void ShowEffect() { }
        public void ChangePlayside() { }
        public void IncreaseHighSpeed() { }
        public void DecreaseHighSpeed() { }
        public void VolumeChanged() { }
    }

    private sealed class RecordingSettingsEditSession : ISettingsEditSession
    {
        private readonly Action? onSave;

        internal RecordingSettingsEditSession(Action? onSave = null)
        {
            this.onSave = onSave;
        }

        public BeMusicSeeker.Properties.Settings Values { get; } = BeMusicSeeker.Properties.Settings.Default;

        public int SaveCount { get; private set; }

        public Exception? SaveException { get; set; }

        public void Reload()
        {
        }

        public void Save()
        {
            SaveCount++;
            onSave?.Invoke();
            if (SaveException != null)
            {
                throw SaveException;
            }
        }
    }
}
