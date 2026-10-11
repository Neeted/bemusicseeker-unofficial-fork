using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// 標準compositionの購読と終端のBassAudioRuntime.Shutdownが共通資源へ到達するため、準備から解放まで並列化しません。
[DoNotParallelize]
public sealed class ShellShutdownWorkflowOwnerTests
{
    /// <summary>閉じる要求を連続しても同じ準備Taskと終端結果を共有し、二重停止を開始しません。</summary>
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

    /// <summary>起動時更新の停止準備に閉じる要求が重なっても、同じ準備理由を維持して閉鎖を許可します。</summary>
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

    /// <summary>閉鎖準備後に終端処理を繰り返しても設定を一回だけ保存し、UI操作禁止状態を解除します。</summary>
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

    /// <summary>player終了で捕捉した最新の配置を一回だけ実XMLへ保存し、古い保存値・メモリ値の再保存を防ぎます。</summary>
    [TestMethod]
    public async Task TerminalSettingsPersistLatestPlayerCaptureOnlyOnce()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsTerminalPlacement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        MainWindowViewModel? viewModel = null;
        try
        {
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
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
            ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel, settingsEditSession: session);

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

    /// <summary>資源解放だけではアプリを終了せず、明示終了要求を設定保存後に一回だけ実行します。</summary>
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

    /// <summary>設定保存が失敗してもplayerを一回解放し、元例外をUIで一回通知して終端終了へ進みます。</summary>
    /// <remarks>通知と保存の実Dispatcher所属が対象であり、Windowの寿命は不要です。Closeの実接続はMainWindowViewHostTestsが分担します。</remarks>
    [TestMethod]
    public void TerminalSettingsSaveFailureIsWarnedAndCleanupContinues()
    {
        TestUiDispatcherHost.Invoke(() =>
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
                    Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

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

    /// <summary>更新用停止準備を経由した終端処理でも注入した設定保存主体へ一回だけ保存し、通常Closeと異なる保存経路への逸脱を検出します。</summary>
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

    /// <summary>UI委譲が失敗しても譜面停止と診断終了を待って元例外を返し、失敗の消費後には準備失敗を保ったままCloseを許可します。</summary>
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

    /// <summary>譜面停止のgate中はCloseを完了せずUI操作を禁止し、gate解放後の終端処理で解除する順序を確認します。</summary>
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
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            try
            {
                close = owner.RequestWindowCloseAsync();

                await TestUiDispatcherHost.AwaitNotificationAsync(shutdownEntered.Task, close, "shell-shutdown.cancel-start");
                await TestUiDispatcherHost.AwaitNotificationAsync(interactionBlocked.Task, close, "shell-shutdown.interaction-blocked");
                Assert.IsTrue(owner.IsShutdownPreparationStarted);
                Assert.IsTrue(owner.IsShutdownPreparationRunning);
                Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                Assert.IsFalse(close.IsCompleted);
                Assert.IsFalse(owner.IsShutdownPrepared);
                regularChartStopRelease.TrySetResult(true);

                ShellShutdownWorkflowCompletionReceipt receipt = await close;

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
            // 本体の例外を保ちつつgateを必ず解放し、開始済み停止処理を次のケースへ残しません。
            regularChartStopRelease.TrySetResult(true);
            if (close != null)
            {
                try
                {
                    // 受付済みの shutdown を解放後まで回収し、次のテストへ処理を残さない。
                    await close;
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

    /// <summary>Close準備後に接続したライブラリとプレイリストにも停止を伝え、L/Pの新規受付が再開する漏れを検出します。</summary>
    [TestMethod]
    public async Task LateAttachedCatalogsReceiveShutdownCancellation()
    {
        string root = Path.Combine(Path.GetTempPath(), "BmsLateAttachedShutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);
        try
        {
            ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();
            Assert.IsTrue(receipt.PreparationSucceeded);

            BMSLibrary library = CreateLibrary(root, "song.db", viewModel);
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
            BMSPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(
                Path.Combine(root, "song.db"), composition.SettingsEditSession.Values, library: library);
            owner.AttachLibrary(library);
            owner.AttachPlaylist(playlist);

            Assert.IsTrue(library.IsShutdownRequested);
            Assert.IsTrue(playlist.IsShutdownRequested);
            Assert.IsFalse(composition.OperationAdmission.TryEnter(out _));
            Assert.IsFalse(composition.PlaylistOperationAdmission.TryEnter(out _));
        }
        finally
        {
            try { await owner.CompleteTerminalShutdownAsync(); }
            finally { viewModel.SettingDialog.Dispose(); DeleteDirectory(root); }
        }
    }

    /// <summary>診断停止のgateを保持した間はCloseを完了せず、診断終端より先に閉鎖を許可する誤りを検出します。</summary>
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

        drainEntered.Wait();
        Assert.IsFalse(close.IsCompleted);

        drainRelease.SetResult(true);
        ShellShutdownWorkflowCompletionReceipt receipt = await close;

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(receipt.CloseAllowed);
    }

    /// <summary>Close準備を経由せず終端処理へ進んでも、接続済みL/Pの取消と新規受付禁止を開始します。</summary>
    [TestMethod]
    public async Task TerminalCleanupStartsCancellationWhenClosePreparationWasBypassed()
    {
        string root = Path.Combine(Path.GetTempPath(), "BmsTerminalBypassShutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db", viewModel);
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
            BMSPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(
                Path.Combine(root, "song.db"), composition.SettingsEditSession.Values, library: library);
            owner.AttachLibrary(library);
            owner.AttachPlaylist(playlist);
            await owner.CompleteTerminalShutdownAsync();

            Assert.IsTrue(library.IsShutdownRequested);
            Assert.IsTrue(playlist.IsShutdownRequested);
            Assert.IsFalse(composition.OperationAdmission.TryEnter(out _));
            Assert.IsFalse(composition.PlaylistOperationAdmission.TryEnter(out _));
        }
        finally
        {
            try { await owner.CompleteTerminalShutdownAsync(); }
            finally { viewModel.SettingDialog.Dispose(); DeleteDirectory(root); }
        }
    }

    /// <summary>更新確認が稼働中のCloseは更新Taskの終端まで準備を開始せず、更新なしの終端後に閉鎖を許可します。</summary>
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
        checkEntered.Wait();

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

    /// <summary>更新起動のhandshake待機中は停止準備を始めず、更新終端後には準備済みとなり、Close要求が成功してCloseを許可することを確認します。</summary>
    [TestMethod]
    public async Task StartupUpdatePublishesReadyBeforePreparationAndTerminalBeforeClose()
    {
        var events = new List<string>();
        var startEntered = new ManualResetEventSlim();
        var startRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var available = UpdateCheckResult.Available(
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
        startEntered.Wait();
        Assert.IsFalse(owner.IsShutdownPreparationStarted);

        startRelease.SetResult(true);
        await startupUpdate.WaitForIdleAsync();
        await startupUpdate.WaitForTerminalAsync();
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

    /// <summary>先にCloseを受理した後の遅延モード再起動要求は拒否し、設定保存・再起動・終了callbackを追加実行しません。</summary>
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

        Assert.IsFalse(await modeRequest);
        ShellShutdownWorkflowCompletionReceipt receipt = await close;

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.AreEqual(0, settingsSession.SaveCount);
        Assert.AreEqual(0, restartCount);
        Assert.AreEqual(0, applicationShutdownCount);
    }

    /// <summary>更新起動を受理済みのときは後続のモード再起動を保存せず拒否し、受理した更新だけを一回継続します。</summary>
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
        await startupUpdate.WaitForTerminalAsync();
        Assert.IsTrue(startupUpdate.IsShutdownPreparationStarted);
        Assert.IsFalse(await owner.RequestOperationModeRestartAsync(new OperationModeRestartRequest(true, "later-mode")));
        Assert.AreEqual(0, settingsSession.SaveCount);
        Assert.AreEqual(1, proceeded);
        Assert.AreEqual(0, aborted);
    }

    /// <summary>モード再起動の受理後に更新起動handshakeが戻っても更新継続を中止し、再起動用保存と終端保存・明示終了を各一回実行します。</summary>
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
            await TestUiDispatcherHost.AwaitNotificationAsync(launchEntered.Task, startupUpdate.WaitForIdleAsync(), "shell-shutdown.update-launch");
            Assert.IsTrue(await owner.RequestOperationModeRestartAsync(
                new OperationModeRestartRequest(true, "history-mode-wins")));
            Assert.IsNotNull(close);
        }
        finally
        {
            launchRelease.TrySetResult(true);
            await startupUpdate.WaitForTerminalAsync();
        }
        await startupUpdate.WaitForTerminalAsync();
        ShellShutdownWorkflowCompletionReceipt receipt = await close!;
        await owner.CompleteTerminalShutdownAsync();
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

    /// <summary>停止開始callbackが例外を投げても取消と停止準備を終え、準備失敗を成功へ置換せずCloseを許可します。</summary>
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

    /// <summary>終端処理を繰り返しても接続済みplayerの終了は一回に留め、二重解放を検出します。</summary>
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

    /// <summary>playerの非同期解放をUI外で完了してからUIで一回設定保存し、UIの停止待ちと解放前保存を防ぎます。</summary>
    /// <remarks>実Dispatcherへの復帰とworker所属を観測します。実Windowの応答性はMainWindowViewHostTestsが分担します。</remarks>
    [TestMethod]
    public void TerminalAsyncPlayerCloseRunsOnWorkerBeforeUiSettingsSave()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            TestUiDispatcherHost.AwaitTaskOnDispatcher(RunAsync(), "terminal-async-player-close");

            async Task RunAsync()
            {
                MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
                try
                {
                    Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    bool? closeOnUi = null;
                    bool? saveOnUi = null;
                    int closesAtSave = 0;
                    var player = new AsyncCloseBmsPlayer(() => closeOnUi = dispatcher.CheckAccess());
                    await viewModel.PlaybackPanel.ReplacePlayerAsync(player);
                    var settingsSession = new RecordingSettingsEditSession(() =>
                    {
                        saveOnUi = dispatcher.CheckAccess();
                        closesAtSave = player.CloseProcessCount;
                    });
                    ShellShutdownWorkflowOwner owner = CreateDirectOwner(
                        viewModel,
                        settingsEditSession: settingsSession);

                    await owner.CompleteTerminalShutdownAsync();

                    Assert.IsTrue(closeOnUi is false,
                        "非同期停止の同期部分もUI外で実行し、UIで先行player操作のlockを待ちません。");
                    Assert.AreEqual(1, player.CloseProcessCount);
                    Assert.AreEqual(1, closesAtSave, "playerの解放後に設定を保存します。");
                    Assert.AreEqual(1, settingsSession.SaveCount);
                    Assert.IsTrue(saveOnUi is true, "設定保存はUIへ戻して実行します。");
                }
                finally
                {
                    viewModel.SettingDialog.Dispose();
                }
            }
        });
    }

    /// <summary>詳細要求の取消Task終端だけでは停止準備を完了せず、稼働workerのidleを待ってから警告なしで完了します。</summary>
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
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        ShutdownPreparationResult? preparationResult = null;

        try
        {
            await workspace.WaitForDetailRequestCompletionAsync(request.RequestVersion)
                .ConfigureAwait(false);
            Assert.IsFalse(workerIdle.IsCompleted);
            Assert.IsFalse(preparation.IsCompleted);
        }
        catch (Exception exception)
        {
            bodyFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            try
            {
                PlaylistDetailBuildQueueCoordinator.CompleteIteration(
                    workspace.DetailBuildState,
                    buildCancellation,
                    activeRequest);
                PlaylistDetailBuildQueueCoordinator.FinishWorkerAfterFailure(workspace.DetailBuildState);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            try
            {
                // 取消後の要求終端と worker idle を、ゲート解放後に必ず回収する。
                await Task.WhenAll(workerIdle, preparation).ConfigureAwait(false);
                preparationResult = preparation.Result;
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
                throw new AggregateException(
                    "The detail shutdown assertion failed and cleanup also failed.",
                    bodyFailure.SourceException,
                    cleanupFailure);
            }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        ShutdownPreparationResult result = preparationResult
            ?? throw new AssertFailedException("Shutdown preparation did not complete.");
        Assert.IsFalse(result.SlowWaitLogged);
        Assert.AreEqual(0, warnings.Count);
    }

    /// <summary>サマリー構築へ停止取消が届いても、構築の終端とidleより先には停止準備を完了しません。</summary>
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
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        ShutdownPreparationResult? preparationResult = null;

        try
        {
            await cancellationObserved.Task.ConfigureAwait(false);
            Assert.IsFalse(summaryIdle.IsCompleted);
            Assert.IsFalse(preparation.IsCompleted);
        }
        catch (Exception exception)
        {
            bodyFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            try
            {
                workspace.CompletePlaylistSummaryDataBuild(buildRequest);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            try
            {
                // summary の cancellation 通知後に、idle と shutdown preparation を所有する。
                await Task.WhenAll(summaryIdle, preparation).ConfigureAwait(false);
                preparationResult = preparation.Result;
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
                throw new AggregateException(
                    "The summary shutdown assertion failed and cleanup also failed.",
                    bodyFailure.SourceException,
                    cleanupFailure);
            }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        ShutdownPreparationResult result = preparationResult
            ?? throw new AssertFailedException("Shutdown preparation did not complete.");
        Assert.IsFalse(result.SlowWaitLogged);
        Assert.AreEqual(0, warnings.Count);
    }

    /// <summary>実reload cleanupのgate保持中に停止開始を同期公開し、cleanup終端まで準備を完了させずGC・遅延警告も実行しません。</summary>
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
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
            var dispatcherEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcherRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                },
                logShutdownWarning: warnings.Add);
            Assert.IsTrue(workspace.QueuePlaylistReloadCleanup(isFullReload: true, tableCount: 1));
            await dispatcherEntered.Task.ConfigureAwait(false);
            Task cleanupIdle = workspace.WaitForPlaylistReloadCleanupIdleAsync();
            Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("reload_cleanup");
            ExceptionDispatchInfo? bodyFailure = null;
            Exception? cleanupFailure = null;
            ShutdownPreparationResult? preparationResult = null;

            try
            {
                // 同期dispatch委譲が戻った時点で確認し、markの通知待ちによって遅延を見逃しません。
                Assert.IsTrue(Volatile.Read(ref shutdownRequested), "停止準備開始時点でcoordinated shutdownを公開します。");
                Assert.IsFalse(cleanupIdle.IsCompleted);
                Assert.IsFalse(preparation.IsCompleted);
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                dispatcherRelease.TrySetResult(true);
                try
                {
                    // reload cleanup の gate 解放後に、idle と preparation を同じ finally で回収する。
                    await Task.WhenAll(cleanupIdle, preparation).ConfigureAwait(false);
                    preparationResult = preparation.Result;
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            if (bodyFailure != null)
            {
                if (cleanupFailure != null)
                {
                    throw new AggregateException(
                        "The reload cleanup assertion failed and cleanup also failed.",
                        bodyFailure.SourceException,
                        cleanupFailure);
                }
                bodyFailure.Throw();
            }
            if (cleanupFailure != null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }

            ShutdownPreparationResult result = preparationResult
                ?? throw new AssertFailedException("Shutdown preparation did not complete.");
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

    /// <summary>導入処理の実受付内でgateを保持した間は取消後も停止準備を待ち、処理receiptの終端後に遅延警告なしで完了します。</summary>
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
                new DelegatePackageInstallMutationPort((_, _, _, _) =>
                {
                    installEntered.Set();
                    releaseInstall.Wait();
                    return new PackageInstallCommandResult([], null);
                }),
                new NoOpChartMutationPlaybackPort(),
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
            installEntered.Wait();

            Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("package_install");
            Assert.IsFalse(preparation.IsCompleted, "Preparation must wait for the package receipt.");
            releaseInstall.Set();

            ShutdownPreparationResult result = await preparation;
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

    /// <summary>再走査の実受付内でgateを保持した間は停止準備を完了せず、receipt終端後にも元の準備理由を保ちます。</summary>
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
                (current, progress, token, capability) =>
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
            maintenanceEntered.Wait();

            Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("maintenance_receipt");
            Assert.IsFalse(preparation.IsCompleted, "Preparation must wait for the maintenance receipt.");

            maintenanceRelease.TrySetResult(true);
            await maintenance.WaitForIdleAsync();
            ShutdownPreparationResult result = await preparation;
            Assert.AreEqual("maintenance_receipt", result.Reason);
            Assert.IsFalse(result.SlowWaitLogged);
        }
        finally
        {
            maintenanceRelease.TrySetResult(true);
            DeleteDirectory(root);
        }
    }

    /// <summary>フォルダ変更の実受付内でgateを保持した間は停止準備を完了せず、receipt終端後にも元の準備理由を保ちます。</summary>
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
                new NoOpChartMutationPlaybackPort(),
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap(),
                action => action(),
                CreateAcceptedDialogService());
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            ShellShutdownWorkflowOwner owner = CreateDirectOwner(
                viewModel,
                folderAutoRenameWorkflow: folder);

            folder.AttachLibrary(library);
            await folder.RequestStartAllAsync(root);
            folderEntered.Wait();

            Task<ShutdownPreparationResult> preparation = owner.PrepareForStartupUpdateAsync("folder_receipt");
            Assert.IsFalse(preparation.IsCompleted, "Preparation must wait for the folder receipt.");

            folderRelease.TrySetResult(true);
            await folder.WaitForIdleAsync();
            ShutdownPreparationResult result = await preparation;
            Assert.AreEqual("folder_receipt", result.Reason);
            Assert.IsFalse(result.SlowWaitLogged);
        }
        finally
        {
            folderRelease.TrySetResult(true);
            DeleteDirectory(root);
        }
    }

    /// <summary>実プレイリスト出力の受付中にCloseすると取消後の出力Taskと受付cleanupを待ち、出力副作用を始めず以後のP受付も拒否します。</summary>
    [TestMethod]
    public async Task WindowClose_AwaitsActualPlaylistOutputAndAdmissionCleanupAfterCancellation()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ShellShutdownWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindowViewModel? viewModel = null;
        Task? output = null;
        Task<ShellShutdownWorkflowCompletionReceipt>? close = null;
        try
        {
            string db = Path.Combine(root, "song.db");
            StartupLibraryConstructionTestSupport.CreateSongDatabase(db);
            PlaylistPersistenceRepository.EnsureSchema(db);
            var playlist = new TestBmsPlaylist(db, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () =>
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                    return new BeatorajaBmtOptionsSnapshot
                    { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = Path.Combine(root, "table") };
                },
                () => new CustomFolderOutputSettingsSnapshot())
            { BMSTables = [] };
            viewModel = MainWindowViewModelTestFactory.Create();
            ShellShutdownWorkflowOwner owner = CreateDirectOwner(viewModel, markShutdown: _ => marked.TrySetResult());
            owner.AttachPlaylist(playlist);
            output = Task.Run(() => playlist.BmtOutput.ExportAllAsync("shutdown_output"));
            Task reached = await Task.WhenAny(entered.Task, output);
            if (reached == output) { await output; }
            Assert.AreSame(entered.Task, reached);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _));
            close = owner.RequestWindowCloseAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(marked.Task, close, "shell-shutdown.marked");
            Assert.IsFalse(close.IsCompleted, "出力受付内の実Taskとcleanupを待ちます。");
            Assert.IsFalse(output.IsCompleted);
            Assert.IsFalse(playlist.WaitForPlaylistMutationIdleAsync().IsCompleted);
            release.TrySetResult();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => output);
            Assert.IsTrue((await close).CloseAllowed);
            Assert.IsTrue(playlist.WaitForPlaylistMutationIdleAsync().IsCompleted);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _), "Close後は出力終端のPにも新規要求を受理しません。");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "table")), "終了取消後に出力の副作用を始めません。");
        }
        finally
        {
            release.TrySetResult();
            try
            {
                if (output != null) { try { await output; } catch (OperationCanceledException) { } }
                if (close != null) { await close; }
            }
            finally { viewModel?.SettingDialog.Dispose(); Directory.Delete(root, true); }
        }
    }

    /// <summary>実構成の再起動失敗通知を保持した間は終端を完了せず、通知後に最小保存済みモード・履歴を維持して終了入口を一度実行します。</summary>
    [TestMethod]
    public void ComposedOperationModeRestartFailureAwaitsNotificationBeforeTerminalCompletion()
        => RunComposedRestartFailure(notificationUnavailable: false, notificationThrows: false);

    /// <summary>実構成の再起動失敗を通知できなくてもreportへ到達し、終端を完了してモード・履歴を保存し、無関係なdraftを保存しません。</summary>
    [TestMethod]
    public void ComposedOperationModeRestartNotificationUnavailableCompletesTerminal()
        => RunComposedRestartFailure(notificationUnavailable: true, notificationThrows: false);

    /// <summary>実構成の再起動失敗通知Taskが例外でもreportへ到達し、終端を完了してモード・履歴を保存し、無関係なdraftを保存しません。</summary>
    [TestMethod]
    public void ComposedOperationModeRestartNotificationExceptionCompletesTerminal()
        => RunComposedRestartFailure(notificationUnavailable: false, notificationThrows: true);

    /// <summary>Windowの実Close接続は上位成功ケースへ任せ、実compositionの再起動・通知・report接続と通知待機、専用XMLの最小保存を観測します。</summary>
    private static void RunComposedRestartFailure(bool notificationUnavailable, bool notificationThrows)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "composed-restart-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string settingsPath = Path.Combine(root, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
            settings.OperationModeLR2DB = false;
            settings.PlayHistorySelectedDisplayTargetIdentity = "history-initial";
            settings.ShowScoreViewerRegisterConfirmMsg = false;
            settings.Save();
            var settingsSession = new PersistentRestartSettingsSession(settings);
            var startFailure = new InvalidOperationException("replacement-process-start-marker");
            var lifetime = new FailedRestartLifetime(startFailure);
            var notificationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationRelease = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = new FileDbReportRecordingDialogs
            {
                MessageHandler = _ =>
                {
                    notificationEntered.TrySetResult();
                    return notificationRelease.Task;
                }
            };
            var reportedFailures = new List<Exception>();
            var composition = new ApplicationComposition(
                settingsEditSession: settingsSession,
                defaultBmsPlayerFactory: () => new FakeBmsPlayer(),
                uiScheduler: new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                restartFailureDialogs: dialogs,
                reportRestartFailure: reportedFailures.Add);
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
            ShellShutdownWorkflowOwner owner = viewModel.ShellShutdownWorkflow;
            Task<bool>? modeRequest = null;
            Task<ShellShutdownWorkflowCompletionReceipt>? close = null;
            Task? terminal = null;
            async Task FinishAcceptedCloseAsync(Task<ShellShutdownWorkflowCompletionReceipt> preparation)
            {
                ShellShutdownWorkflowCompletionReceipt receipt = await preparation;
                Assert.IsTrue(receipt.PreparationSucceeded);
                Assert.IsTrue(receipt.CloseAllowed);
                await owner.CompleteTerminalShutdownAsync();
            }
            void OnRestartRequested()
            {
                close = owner.RequestWindowCloseAsync();
                terminal = FinishAcceptedCloseAsync(close);
            }
            owner.OperationModeRestartRequested += OnRestartRequested;
            try
            {
                // 再起動の最小保存で、この未確定draftをXMLへ混ぜてはいけません。
                viewModel.SettingDialog.ShowScoreViewerRegisterConfirmMsg = true;
                modeRequest = owner.RequestOperationModeRestartAsync(new OperationModeRestartRequest(true, "history-initial"));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(modeRequest, "composed-restart.mode-admission");
                Assert.IsTrue(modeRequest.Result);
                Assert.IsNotNull(close);
                Assert.IsNotNull(terminal);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(notificationEntered.Task, terminal, "composed-restart.failure-notification"),
                    "composed-restart.notification-arrival");
                Assert.IsFalse(terminal.IsCompleted, "通知Taskの実終端まで終了処理を待ちます。");
                Assert.AreEqual(1, lifetime.RestartCount);
                Assert.AreEqual(0, lifetime.ShutdownCount);
                Assert.AreEqual(1, settingsSession.ModeSaveCount);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Assert.AreEqual(1, dialogs.Messages.Count);
                StringAssert.Contains(dialogs.Messages[0].MessageBoxText, Resources.Error_RestartApplicationFailed);
                StringAssert.Contains(dialogs.Messages[0].MessageBoxText, startFailure.Message);
                if (notificationThrows)
                {
                    notificationRelease.TrySetException(new IOException("restart-notification-marker"));
                }
                else
                {
                    notificationRelease.TrySetResult(notificationUnavailable
                        ? UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)
                        : UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "composed-restart.terminal-completion");
                Assert.AreEqual(notificationUnavailable || notificationThrows ? 1 : 0, reportedFailures.Count);
                Assert.AreEqual(0, lifetime.ShutdownCount);
                // 明示終了入口の呼出し順は上位MainWindow成功ケースが保証し、ここでは入口の一回性を確認します。
                owner.RequestTerminalApplicationShutdown();
                owner.RequestTerminalApplicationShutdown();
                Assert.AreEqual(1, lifetime.ShutdownCount);
                Assert.AreEqual(1, lifetime.RestartCount);
                Assert.AreEqual(1, settingsSession.SaveCount);
                Settings persisted = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                Assert.IsTrue(persisted.OperationModeLR2DB);
                Assert.AreEqual("history-initial", persisted.PlayHistorySelectedDisplayTargetIdentity);
                Assert.IsFalse(persisted.ShowScoreViewerRegisterConfirmMsg);
            }
            finally
            {
                notificationRelease.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                try
                {
                    if (modeRequest != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(modeRequest, "composed-restart.mode-cleanup"); }
                    close ??= owner.RequestWindowCloseAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(close, "composed-restart.close-cleanup");
                    terminal ??= owner.CompleteTerminalShutdownAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "composed-restart.terminal-cleanup");
                }
                finally
                {
                    owner.OperationModeRestartRequested -= OnRestartRequested;
                    viewModel.SettingDialog.Dispose();
                    viewModel.RegularChartList.Dispose();
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    /// <summary>実再起動境界を失敗させ、最終終了入口の呼出し回数を記録します。</summary>
    private sealed class FailedRestartLifetime(Exception startFailure) : IApplicationLifetimePort
    {
        internal int RestartCount { get; private set; }
        internal int ShutdownCount { get; private set; }
        public bool IsFirstStartup => false;
        public void CompleteFirstStartup() { }
        public void MarkCoordinatedShutdownStarted(string reason) { }
        public void RequestShutdown() => ShutdownCount++;
        public Task RestartApplicationAsync() { RestartCount++; return Task.FromException(startFailure); }
    }

    /// <summary>実XML保存へ委譲し、通常保存とモード専用保存を区別して記録します。</summary>
    private sealed class PersistentRestartSettingsSession(Settings settings) : ISettingsEditSession
    {
        private readonly SettingsEditSession inner = new(settings);
        internal int SaveCount { get; private set; }
        internal int ModeSaveCount { get; private set; }
        public Settings Values => inner.Values;
        public void Reload() => inner.Reload();
        public void Save() { SaveCount++; inner.Save(); }
        public void SaveOperationModeForRestart(bool mode, string historyIdentity)
        {
            ModeSaveCount++;
            inner.SaveOperationModeForRestart(mode, historyIdentity);
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
            GetPrivateField<ApplicationComposition>(viewModel, "applicationComposition").OperationAdmission,
            GetPrivateField<ApplicationComposition>(viewModel, "applicationComposition").PlaylistOperationAdmission,
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

    private static BMSLibrary CreateLibrary(string root, string fileName, MainWindowViewModel? owner = null)
    {
        string path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, []);
        using (var initialize = new LR2SongDBExtended(path))
        {
        }
        return owner == null
            ? new TestBmsLibrary(path, null, null, string.Empty)
            : MainWindowViewModelTestFactory.CreateLibrary(
                path, MainWindowViewModelTestFactory.GetComposition(owner).SettingsEditSession.Values, owner);
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

        public FolderAutoRenameExecutionResult RenameSelectedWithProgress(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            IFolderAutoRenameProgressWriter progressWriter) =>
            throw new InvalidOperationException("selected folder mutation was not expected");

        public AutoRenameBatchResult RenameAllWithReceiptWithProgress(
            BMSLibrary library,
            string parentDirectory,
            IFolderAutoRenameProgressWriter progressWriter)
        {
            entered.Set();
            release.Task.GetAwaiter().GetResult();
            return new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
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

    private class FakeBmsPlayer : IBMSPlayer
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

    private sealed class AsyncCloseBmsPlayer(Action onClose) : FakeBmsPlayer(onClose), INextSongPreloadPlayer
    {
        public PlaybackStartOperation BeginStart(string path, Action<object, EventArgs>? onExit, bool allowPreload,
            Action<Exception>? onPlaybackFailure = null)
            => throw new NotSupportedException();

        public Task PrepareNextAsync(NextSongPreloadInput input, Action<Exception> onFailure) => Task.CompletedTask;

        public Task CloseAsync()
        {
            // 完了済みTaskでも、呼出側の同期部分をUIで実行しない契約を確認します。
            CloseProcess();
            return Task.CompletedTask;
        }
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
            Values = values ?? MainWindowViewModelTestFactory.CreateIsolatedSettings();
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
