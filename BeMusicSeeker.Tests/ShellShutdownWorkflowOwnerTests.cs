using System.Threading.Tasks;
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// Shutdown composition reaches the process-wide Settings.Default instance and shared dispatcher-owned state.
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
        await owner.CompleteTerminalShutdownAsync();
        await owner.CompleteTerminalShutdownAsync();

        Assert.AreEqual(1, settingsSession.SaveCount);
        Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
    }

    // P09 amendment: production attachment, then latest runtime capture must precede the single durable save.
    [TestMethod]
    public async Task TerminalSettingsPersistLatestPlayerCaptureOnlyOnce()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsTerminalPlacement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        MainWindowViewModel? viewModel = null;
        try
        {
            var settings = PortableSettingsPersistenceTests.OpenSettings(path);
            var diskPlacement = new BeMusicSeeker.Models.Utils.WindowPlacement(0, 1, 0, 0, 0, 0, 10, 20, 810, 620);
            var memoryPlacement = new BeMusicSeeker.Models.Utils.WindowPlacement(0, 1, 0, 0, 0, 0, 30, 40, 830, 640);
            var finalPlacement = new BeMusicSeeker.Models.Utils.WindowPlacement(0, 1, 0, 0, 0, 0, 50, 60, 850, 660);
            settings.LR2bodyWindowPlacement = BeMusicSeeker.Models.Utils.Win32WindowPlacementAdapter.ToNative(diskPlacement);
            settings.Save();
            settings.LR2bodyWindowPlacement = BeMusicSeeker.Models.Utils.Win32WindowPlacementAdapter.ToNative(memoryPlacement);
            int saveRequests = 0;
            settings.SettingsSaving += (_, _) => saveRequests++;
            viewModel = MainWindowViewModelTestFactory.Create(settings);
            var gateway = new SettingsPlayerSettingsGateway(() => settings);
            var player = new FakeBmsPlayer(() => gateway.UpdateWindowPlacement(finalPlacement));
            await viewModel.PlaybackPanel.ReplacePlayerAsync(player);
            var session = new RecordingSettingsEditSession(settings.Save, settings);
            var owner = CreateDirectOwner(viewModel, settingsEditSession: session);

            await owner.CompleteTerminalShutdownAsync();
            await owner.CompleteTerminalShutdownAsync();

            Assert.AreEqual(1, session.SaveCount);
            Assert.AreEqual(1, saveRequests);
            Assert.AreEqual(1, player.CloseProcessCount);
            Assert.AreEqual(BeMusicSeeker.Models.Utils.Win32WindowPlacementAdapter.ToNative(finalPlacement),
                PortableSettingsPersistenceTests.OpenSettings(path).LR2bodyWindowPlacement);
        }
        finally
        {
            viewModel?.SettingDialog.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task TerminalApplicationShutdownIsExplicitAndRequestedOnlyOnceAfterCleanup()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var events = new List<string>();
        var settingsSession = new RecordingSettingsEditSession(() => events.Add("settings_save"));
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            settingsEditSession: settingsSession,
            requestApplicationShutdown: () => events.Add("application_shutdown"));

        await owner.CompleteTerminalShutdownAsync();
        CollectionAssert.AreEqual(new[] { "settings_save" }, events);

        owner.RequestTerminalApplicationShutdown();
        owner.RequestTerminalApplicationShutdown();

        CollectionAssert.AreEqual(new[] { "settings_save", "application_shutdown" }, events);
    }

    [TestMethod]
    public void TerminalSettingsSaveFailureIsWarnedAndCleanupContinues()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            TestUiDispatcherHost.AwaitTaskOnDispatcher(RunAsync(), "settings-save-failure");

            async Task RunAsync()
            {
                MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
                try
                {
                    var warnings = new List<string>();
                    var settingsSession = new RecordingSettingsEditSession
                    {
                        SaveException = new InvalidOperationException("settings unavailable")
                    };
                    var player = new FakeBmsPlayer();
                    await viewModel.PlaybackPanel.ReplacePlayerAsync(player);
                    var notices = new List<Exception>();
                    int exitCount = 0;
                    bool noticeOnUi = false;
                    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

                    ShellShutdownWorkflowOwner owner = CreateDirectOwner(
                        viewModel,
                        settingsEditSession: settingsSession,
                        logShutdownWarning: warnings.Add,
                        reportSettingsSaveFailure: exception =>
                        {
                            noticeOnUi = dispatcher.CheckAccess();
                            notices.Add(exception);
                        },
                        requestApplicationShutdown: () => exitCount++);

                    await owner.CompleteTerminalShutdownAsync();
                    await owner.CompleteTerminalShutdownAsync();

                    Assert.AreEqual(1, settingsSession.SaveCount);
                    Assert.AreEqual(1, player.CloseProcessCount);
                    StringAssert.Contains(string.Join("\n", warnings), "settings_save_failed");
                    CollectionAssert.AreEqual(new[] { settingsSession.SaveException }, notices);
                    owner.RequestTerminalApplicationShutdown();
                    owner.RequestTerminalApplicationShutdown();
                    Assert.AreEqual(1, exitCount);
                    Assert.IsTrue(noticeOnUi);
                }
                finally
                {
                    viewModel.SettingDialog.Dispose();
                }
            }
        });
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

        await owner.CompleteTerminalShutdownAsync();

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
        Assert.IsTrue(owner.IsShutdownPreparationStarted);
        Assert.IsTrue(owner.IsShutdownPreparationRunning);
        Assert.IsFalse(preparation.IsCompleted);
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
        var shutdownEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.ProgressHub.StartupProgress.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked)
                && viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked)
            {
                interactionBlocked.TrySetResult(true);
            }
        };
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            markShutdown: _ => shutdownEntered.TrySetResult(true));

        Task<ShellShutdownWorkflowCompletionReceipt>? close = null;
        Task<ShellShutdownWorkflowCompletionReceipt>? closeCompletion = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            try
            {
                close = owner.RequestWindowCloseAsync();

                await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await interactionBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsTrue(owner.IsShutdownPreparationStarted);
                Assert.IsTrue(owner.IsShutdownPreparationRunning);
                Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                Assert.IsFalse(close.IsCompleted);
                Assert.IsFalse(owner.IsShutdownPrepared);
                regularChartStopRelease.TrySetResult(true);

                closeCompletion = close.WaitAsync(TimeSpan.FromSeconds(5));
                ShellShutdownWorkflowCompletionReceipt receipt = await closeCompletion;

                Assert.IsTrue(receipt.PreparationSucceeded);
                Assert.IsTrue(receipt.CloseAllowed);
                await owner.CompleteTerminalShutdownAsync();
                Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
        }
        finally
        {
            // Preserve the body failure if an assertion above aborts before the gate is released,
            // then await the started shutdown so it cannot continue into another test.
            regularChartStopRelease.TrySetResult(true);
            if (close != null)
            {
                try
                {
                    // Wait on the underlying operation again rather than reusing a
                    // timed-out wrapper; WaitAsync does not cancel the shutdown.
                    await close.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
        }

        if (bodyFailure != null)
        {
            if (cleanupFailure != null)
            {
                throw new AggregateException(
                    "The shutdown assertion failed and cleanup also failed.",
                    bodyFailure.SourceException,
                    cleanupFailure);
            }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    [TestMethod]
    public async Task LateAttachedCatalogsReceiveShutdownCancellation()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);

        ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();
        Assert.IsTrue(receipt.PreparationSucceeded);

        BMSLibrary library = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        BMSPlaylist playlist = (BMSPlaylist)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSPlaylist));
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
    public async Task TerminalCleanupStartsCancellationWhenClosePreparationWasBypassed()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);
        BMSLibrary library = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        BMSPlaylist playlist = (BMSPlaylist)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSPlaylist));
        SetPrivateField(
            playlist,
            "shutdownCoordinator",
            new PlaylistShutdownCoordinator());

        owner.AttachLibrary(library);
        owner.AttachPlaylist(playlist);
        await owner.CompleteTerminalShutdownAsync();

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
        Assert.IsFalse(close.IsCompleted);
        Assert.IsFalse(owner.IsShutdownPreparationStarted);
        Assert.IsFalse(owner.IsShutdownPreparationRunning);

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
        await startupUpdate.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await startupUpdate.WaitForTerminalAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(owner.IsShutdownPrepared);

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
    public async Task WindowCloseWinsAgainstDeferredOperationModeRestartRequest()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var settingsSession = new RecordingSettingsEditSession();
        int restartCount = 0;
        int applicationShutdownCount = 0;
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            settingsEditSession: settingsSession,
            requestApplicationShutdown: () => applicationShutdownCount++,
            startApplicationRestart: () =>
            {
                restartCount++;
                return Task.CompletedTask;
            });
        owner.OperationModeRestartRequested += () => Assert.Fail("A close that won the race must reject mode restart.");

        var modeRequestRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> modeRequest = Task.Run(async () =>
        {
            await modeRequestRelease.Task.ConfigureAwait(false);
            return await owner.RequestOperationModeRestartAsync(
                new OperationModeRestartRequest(true, "history-close-wins")).ConfigureAwait(false);
        });

        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
        modeRequestRelease.SetResult(true);

        Assert.IsFalse(await modeRequest.WaitAsync(TimeSpan.FromSeconds(5)));
        ShellShutdownWorkflowCompletionReceipt receipt = await close.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.AreEqual(0, settingsSession.SaveCount);
        Assert.AreEqual(0, restartCount);
        Assert.AreEqual(0, applicationShutdownCount);
    }

    [TestMethod]
    public async Task AcceptedStartupUpdateRejectsLaterOperationModeRestartWithoutSaving()
    {
        int proceeded = 0;
        int aborted = 0;
        var startupUpdate = new StartupUpdateWorkflowOwner(
            () => Task.FromResult(CreateAvailableUpdateResult()),
            _ => Task.FromResult("package.zip"),
            _ => new PreparedUpdaterLaunch(() => new UpdaterLaunchReceipt(
                () => aborted++, () => proceeded++)),
            () => { },
            _ => { },
            action => Task.Run(action),
            action => action());
        startupUpdate.PresentationRequested += request => request.Complete(CreateAvailableUpdateResult().Assets[0]);
        var settingsSession = new RecordingSettingsEditSession();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            MainWindowViewModelTestFactory.Create(),
            startupUpdate: startupUpdate,
            settingsEditSession: settingsSession);
        owner.OperationModeRestartRequested += () => Assert.Fail("先に受理した更新終了を置き換えてはいけません。");

        Assert.IsTrue(startupUpdate.Start());
        await startupUpdate.WaitForTerminalAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(startupUpdate.IsShutdownPreparationStarted);
        Assert.IsFalse(await owner.RequestOperationModeRestartAsync(new OperationModeRestartRequest(true, "later-mode")));
        Assert.AreEqual(0, settingsSession.SaveCount);
        Assert.AreEqual(1, proceeded);
        Assert.AreEqual(0, aborted);
    }

    [TestMethod]
    public async Task AcceptedOperationModeRestartInvalidatesLaterStartupUpdateContinuation()
    {
        var launchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int downloadCount = 0;
        int launchStartCount = 0;
        int launchProceedCount = 0;
        int launchAbortCount = 0;
        var startupUpdate = new StartupUpdateWorkflowOwner(
            () => Task.FromResult(CreateAvailableUpdateResult()),
            asset =>
            {
                Interlocked.Increment(ref downloadCount);
                return Task.FromResult("package.zip");
            },
            packagePath => new PreparedUpdaterLaunch(() =>
            {
                Interlocked.Increment(ref launchStartCount);
                launchEntered.TrySetResult(true);
                launchRelease.Task.GetAwaiter().GetResult();
                return new UpdaterLaunchReceipt(
                    () => Interlocked.Increment(ref launchAbortCount),
                    () => Interlocked.Increment(ref launchProceedCount));
            }),
            () => { },
            packagePath => { },
            action => Task.Run(action),
            action => action());
        startupUpdate.PresentationRequested += request => request.Complete(CreateAvailableUpdateResult().Assets[0]);
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var settingsSession = new RecordingSettingsEditSession();
        int restartCount = 0;
        int applicationShutdownCount = 0;
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            startupUpdate: startupUpdate,
            settingsEditSession: settingsSession,
            requestApplicationShutdown: () => applicationShutdownCount++,
            startApplicationRestart: () =>
            {
                Interlocked.Increment(ref restartCount);
                return Task.CompletedTask;
            });
        Task<ShellShutdownWorkflowCompletionReceipt>? close = null;
        owner.OperationModeRestartRequested += () => close = owner.RequestWindowCloseAsync();

        try
        {
            Assert.IsTrue(startupUpdate.Start());
            await launchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await owner.RequestOperationModeRestartAsync(
                new OperationModeRestartRequest(true, "history-mode-wins")));
            Assert.IsNotNull(close);
        }
        finally
        {
            launchRelease.TrySetResult(true);
            await startupUpdate.WaitForTerminalAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        await startupUpdate.WaitForTerminalAsync().WaitAsync(TimeSpan.FromSeconds(5));
        ShellShutdownWorkflowCompletionReceipt receipt = await close!.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.CompleteTerminalShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        owner.RequestTerminalApplicationShutdown();

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.AreEqual(1, downloadCount);
        Assert.AreEqual(1, launchStartCount);
        Assert.AreEqual(0, launchProceedCount);
        Assert.AreEqual(1, launchAbortCount);
        Assert.AreEqual(2, settingsSession.SaveCount);
        Assert.AreEqual(1, restartCount);
        Assert.AreEqual(1, applicationShutdownCount);
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
            .GetField("bmsPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsNotNull(playerField);
        playerField.SetValue(viewModel.PlaybackPanel, player);

        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);
        await owner.RequestWindowCloseAsync();
        await owner.CompleteTerminalShutdownAsync();
        await owner.CompleteTerminalShutdownAsync();

        Assert.AreEqual(1, player.CloseProcessCount);
    }

    [TestMethod]
    public async Task PreparationWaitsForDetailWorkerIdleAfterRequestCancellationBecomesTerminal()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(action => action());
        var warnings = new List<string>();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            playlistWorkspace: workspace,
            logShutdownWarning: warnings.Add);
        var request = new PlaylistBuildRequest
        {
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(), PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null,
                ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true)
        };
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            workspace.DetailBuildState,
            request,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator.TryTakeNextRequestOrStopWorker(
            workspace.DetailBuildState,
            out PlaylistBuildRequest activeRequest));
        using var buildCancellation = new CancellationTokenSource();
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator.TryBeginIteration(
            workspace.DetailBuildState,
            activeRequest,
            buildCancellation,
            isShutdownRequested: false));
        Task workerIdle = workspace.WaitForDetailBuildIdleAsync();
        Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("detail_worker");

        try
        {
            await workspace.WaitForDetailRequestCompletionAsync(request.RequestVersion)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(workerIdle.IsCompleted);
            Assert.IsFalse(preparation.IsCompleted);
        }
        finally
        {
            PlaylistDetailBuildQueueCoordinator.CompleteIteration(
                workspace.DetailBuildState,
                buildCancellation,
                activeRequest);
            PlaylistDetailBuildQueueCoordinator.FinishWorkerAfterFailure(workspace.DetailBuildState);
        }

        await workerIdle.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        ShutdownPreparationResult result = await preparation.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(result.SlowWaitLogged);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public async Task PreparationWaitsForSummaryBuildAfterShutdownCancelsIt()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(action => action());
        workspace.IsPlaylistSummaryMode = true;
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = buildRequest.CancellationToken.Register(
            () => cancellationObserved.TrySetResult(true));
        Task summaryIdle = workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        var warnings = new List<string>();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(
            viewModel,
            playlistWorkspace: workspace,
            logShutdownWarning: warnings.Add);
        Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("summary_build");

        try
        {
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(summaryIdle.IsCompleted);
            Assert.IsFalse(preparation.IsCompleted);
        }
        finally
        {
            workspace.CompletePlaylistSummaryDataBuild(buildRequest);
        }

        await summaryIdle.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        ShutdownPreparationResult result = await preparation.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(result.SlowWaitLogged);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public async Task PreparationWaitsForRunningReloadCleanupAfterPendingCancellation()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            nameof(ShellShutdownWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "user.config");
        MainWindowViewModel? viewModel = null;
        try
        {
            var settings = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
            var dispatcherEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcherRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shutdownMarked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool shutdownRequested = false;
            int garbageCollectionCount = 0;
            viewModel = MainWindowViewModelTestFactory.Create(settings);
            PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(
                action => action(),
                reloadCleanupDispatcherIdleWaiter: async () =>
                {
                    dispatcherEntered.TrySetResult(true);
                    await dispatcherRelease.Task.ConfigureAwait(false);
                },
                reloadCleanupShutdownRequestedProvider: () => Volatile.Read(ref shutdownRequested),
                reloadCleanupGarbageCollector: () => Interlocked.Increment(ref garbageCollectionCount));
            var warnings = new List<string>();
            ShellShutdownWorkflowOwner owner = CreateDirectOwner(
                viewModel,
                playlistWorkspace: workspace,
                markShutdown: _ =>
                {
                    Volatile.Write(ref shutdownRequested, true);
                    shutdownMarked.TrySetResult(true);
                },
                logShutdownWarning: warnings.Add);
            Assert.IsTrue(workspace.QueuePlaylistReloadCleanup(isFullReload: true, tableCount: 1));
            await dispatcherEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Task cleanupIdle = workspace.WaitForPlaylistReloadCleanupIdleAsync();
            Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("reload_cleanup");

            try
            {
                await shutdownMarked.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.IsFalse(cleanupIdle.IsCompleted);
                Assert.IsFalse(preparation.IsCompleted);
            }
            finally
            {
                dispatcherRelease.TrySetResult(true);
            }

            await cleanupIdle.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            ShutdownPreparationResult result = await preparation.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(result.SlowWaitLogged);
            Assert.AreEqual(0, garbageCollectionCount);
            Assert.AreEqual(0, warnings.Count);
        }
        finally
        {
            viewModel?.SettingDialog.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task PreparationWaitsForPackageInstallQueueReceiptAfterCancellation()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(ShellShutdownWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        using var installEntered = new ManualResetEventSlim(false);
        using var releaseInstall = new ManualResetEventSlim(false);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var packageInstall = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((_, _, _, _, _) =>
                {
                    installEntered.Set();
                    Assert.IsTrue(releaseInstall.Wait(5000), "The package install was not released.");
                    return [];
                }),
                action =>
                {
                    action();
                    return true;
                });
            packageInstall.AttachLibrary(library);
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var warnings = new List<string>();
            ShellShutdownWorkflowOwner owner = CreateDirectOwner(
                viewModel,
                packageInstallWorkflow: packageInstall,
                markShutdown: _ => { },
                logShutdownWarning: warnings.Add);
            owner.AttachLibrary(library);
            packageInstall.Enqueue([Path.Combine(root, "pending.zip")]);
            Assert.IsTrue(installEntered.Wait(5000), "The package install did not start.");

            Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("package_install");
            Assert.IsFalse(preparation.IsCompleted, "Preparation must wait for the package receipt.");
            releaseInstall.Set();

            ShutdownPreparationResult result = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(result.SlowWaitLogged);
            Assert.AreEqual(0, warnings.Count);
        }
        finally
        {
            releaseInstall.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PreparationWaitsForMaintenanceReceiptAfterCancellation()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(ShellShutdownWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        var maintenanceRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Directory.CreateDirectory(root);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            using var maintenanceEntered = new ManualResetEventSlim(false);
            var maintenance = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    maintenanceEntered.Set();
                    maintenanceRelease.Task.GetAwaiter().GetResult();
                    return new MaintenanceWorkflowResult();
                },
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: CreateAcceptedDialogService());
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            ShellShutdownWorkflowOwner owner = CreateDirectOwner(
                viewModel,
                maintenanceRescanWorkflow: maintenance);

            maintenance.AttachLibrary(library);
            Assert.IsTrue((await maintenance.RequestStartAsync()).Started);
            Assert.IsTrue(maintenanceEntered.Wait(TimeSpan.FromSeconds(5)));

            Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("maintenance_receipt");
            Assert.IsFalse(preparation.IsCompleted, "Preparation must wait for the maintenance receipt.");

            maintenanceRelease.TrySetResult(true);
            await maintenance.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            ShutdownPreparationResult result = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("maintenance_receipt", result.Reason);
            Assert.IsFalse(result.SlowWaitLogged);
        }
        finally
        {
            maintenanceRelease.TrySetResult(true);
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task PreparationWaitsForFolderReceiptAfterCancellation()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(ShellShutdownWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        var folderRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Directory.CreateDirectory(root);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            using var folderEntered = new ManualResetEventSlim(false);
            var folder = new FolderAutoRenameWorkflowOwner(
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new BlockingFolderMutationPort(folderEntered, folderRelease),
                new NoopFolderAutoRenamePlaybackPort(),
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                CreateAcceptedDialogService());
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            ShellShutdownWorkflowOwner owner = CreateDirectOwner(
                viewModel,
                folderAutoRenameWorkflow: folder);

            folder.AttachLibrary(library);
            await folder.RequestStartAllAsync(root);
            Assert.IsTrue(folderEntered.Wait(TimeSpan.FromSeconds(5)));

            Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("folder_receipt");
            Assert.IsFalse(preparation.IsCompleted, "Preparation must wait for the folder receipt.");

            folderRelease.TrySetResult(true);
            await folder.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            ShutdownPreparationResult result = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("folder_receipt", result.Reason);
            Assert.IsFalse(result.SlowWaitLogged);
        }
        finally
        {
            folderRelease.TrySetResult(true);
            DeleteDirectory(root);
        }
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
        Action<string>? logShutdownWarning = null,
        PlaylistWorkspaceViewModel? playlistWorkspace = null,
        PackageInstallWorkflowOwner? packageInstallWorkflow = null,
        MaintenanceRescanWorkflowOwner? maintenanceRescanWorkflow = null,
        FolderAutoRenameWorkflowOwner? folderAutoRenameWorkflow = null,
        Action<Exception>? reportSettingsSaveFailure = null,
        Func<Task>? startApplicationRestart = null,
        IUiDialogService? restartFailureDialogs = null,
        Action<Exception>? reportRestartFailure = null)
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
            playlistWorkspace ?? viewModel.PlaylistWorkspace,
            viewModel.PlayHistory,
            packageInstallWorkflow ?? viewModel.PackageInstallWorkflow,
            maintenanceRescanWorkflow ?? viewModel.MaintenanceRescanWorkflow,
            folderAutoRenameWorkflow ?? viewModel.FolderAutoRenameWorkflow,
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
            value => value ?? string.Empty,
            reportSettingsSaveFailure,
            startApplicationRestart,
            restartFailureDialogs,
            reportRestartFailure);
    }

    private static T GetPrivateField<T>(MainWindowViewModel viewModel, string name)
    {
        return (T)typeof(MainWindowViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
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
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsNotNull(field, name);
        field.SetValue(target, value);
    }

    private static BMSLibrary CreateLibrary(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, []);
        using (var initialize = new LR2SongDBExtended(path))
        {
        }
        return new TestBmsLibrary(path, null, null, string.Empty);
    }

    private static UpdateCheckResult CreateAvailableUpdateResult()
    {
        return UpdateCheckResult.Available(
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
    }

    private static PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService CreateAcceptedDialogService()
    {
        return new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK)
        };
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class BlockingFolderMutationPort : IFolderAutoRenameMutationPort
    {
        private readonly ManualResetEventSlim entered;
        private readonly TaskCompletionSource<bool> release;

        internal BlockingFolderMutationPort(
            ManualResetEventSlim entered,
            TaskCompletionSource<bool> release)
        {
            this.entered = entered;
            this.release = release;
        }

        public bool HasTargets(BMSLibrary library, string parentDirectory) => true;

        public FolderAutoRenameExecutionResult RenameSelected(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            Action<int, int, string> progressReporter) =>
            throw new InvalidOperationException("selected folder mutation was not expected");

        public bool RenameAll(
            BMSLibrary library,
            string parentDirectory,
            Action<int, int, string> progressReporter)
        {
            entered.Set();
            release.Task.GetAwaiter().GetResult();
            return true;
        }
    }

    private sealed class NoopFolderAutoRenamePlaybackPort : IFolderAutoRenamePlaybackPort
    {
        public void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts)
        {
        }

        public void StopPlaybackForFolderMutation()
        {
        }
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
        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            Values.OperationModeLR2DB = operationMode;
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        private readonly Action? onSave;

        internal RecordingSettingsEditSession(Action? onSave = null, BeMusicSeeker.Properties.Settings? values = null)
        {
            this.onSave = onSave;
            Values = values ?? BeMusicSeeker.Properties.Settings.Default;
        }

        public BeMusicSeeker.Properties.Settings Values { get; }

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
