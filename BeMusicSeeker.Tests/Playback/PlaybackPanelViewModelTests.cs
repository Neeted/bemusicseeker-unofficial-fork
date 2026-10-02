using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// Arbitrary filtered Quick runs share one testhost. This fixture mutates the
// process-global playback modes, player selection, volume, panel state,
// stagefile, and external-panel settings in Settings.Default.
[DoNotParallelize]
public sealed class PlaybackPanelViewModelTests
{
    [TestMethod]
    public async Task SongStart_PreparesOnceAndUsesLiveTargetAfterListAndModeChanges()
    {
        string[] paths = [Path.GetTempFileName(), Path.GetTempFileName(), Path.GetTempFileName()];
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)new TestBmsFile(path)).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("再生開始に失敗しました。"),
            new ChartFileOperationSynchronizer());
        try
        {
            await panel.StartAtIndex(0);
            Assert.AreEqual(paths[1], player.Preloads.Single().Path);
            await panel.Start(forceNewPlay: false); // 同じ曲のPause
            panel.CurrentlyPlayingTime = TimeSpan.FromSeconds(1);
            rows.Rows = new List<object> { new TestBmsFile(paths[0]), new TestBmsFile(paths[2]) };
            panel.RepeatPlayMode = true;
            Assert.AreEqual(1, player.Preloads.Count, "曲の途中では候補を差し替えません。");
            await panel.Next();
            Assert.AreEqual(paths[2], panel.NowPlayingBmsFile.path, "先読みしたBではなく現在の送り先Cへ進みます。");
            Assert.AreEqual(paths[0], player.Preloads.Last().Path, "Cの開始で次の一件を準備します。");
        }
        finally
        {
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            foreach (string path in paths) { File.Delete(path); }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FileMutation_StopsCurrentSongBeforeWritingAndRejectsPlaybackWhileBusy(bool failStop)
    {
        string[] paths = [Path.GetTempFileName(), Path.GetTempFileName()];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)new TestBmsFile(path)).ToList() };
        var operations = new ChartFileOperationSynchronizer();
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("再生開始に失敗しました。"), operations);
        var store = new PhysicalChartDeletionStore();
        var mutation = new SelectedChartMutationWorkflowOwner(
            () => (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary)),
            operations, new ChartMutationActivityOwner(), panel, new FileDbReportRecordingDialogs(), new UnusedPendingDeletionDialog(), store);
        var target = new ChartOperationTarget(CreatePlaybackTargetChart(ChartFileKind.Bms, paths[1]), null,
            ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.RemoveFromLibrary);
        Task<SelectedChartMutationResult>? deletion = null;
        try
        {
            await panel.StartAtIndex(0);
            player.CloseCompletion = () => { entered.TrySetResult(); return cleanup.Task; };
            deletion = mutation.DeleteAsync(new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));
            await entered.Task;
            await panel.StartAtIndex(1);
            Assert.AreEqual(1, player.Starts.Count, "変更受付中の再生を後から実行する予約にはしません。");
            Assert.AreEqual(0, store.Mutations, "停止完了前にファイルを変更しません。");
            if (failStop) { cleanup.SetException(new IOException("停止処理が失敗しました。")); }
            else { cleanup.SetResult(); }
            SelectedChartMutationResult result = await deletion;
            Assert.AreEqual(!failStop, result.Succeeded);
            Assert.AreEqual(failStop ? 0 : 1, store.Mutations);
            Assert.IsNull(panel.NowPlayingBmsFile, "変更対象が次曲だけでも現在曲を止めます。");
            Assert.IsTrue(File.Exists(paths[0]));
            Assert.AreEqual(failStop, File.Exists(paths[1]));
            Assert.IsFalse(operations.IsActive);
            Assert.AreEqual(1, player.Starts.Count, "変更後には自動再開しません。");
        }
        finally
        {
            cleanup.TrySetResult();
            if (deletion != null) { await deletion; }
            player.CloseCompletion = null;
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            foreach (string path in paths) { File.Delete(path); }
        }
    }

    [TestMethod]
    public async Task InternalReady_StartsNextPreparationBeforeCompletionOrUiPublication()
    {
        string firstPath = Path.GetTempFileName();
        string secondPath = Path.GetTempFileName();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Ready = ready.Task, Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = new List<object> { new TestBmsFile(firstPath), new TestBmsFile(secondPath) } };
        var dispatcher = new QueuedPlaybackUiDispatcher();
        var panel = new PlaybackPanelViewModel(player, dispatcher, new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("Valid input failed."),
            new ChartFileOperationSynchronizer());
        Task start = panel.StartAtIndex(0);
        try
        {
            await player.StartObserved.Task;
            Assert.IsTrue(panel.NowPlayingBmsFile.status.HasFlag(BMSFile.BMSFileStatus.LOADING));
            Assert.IsFalse(start.IsCompleted);
            Assert.AreEqual(0, player.Preloads.Count);
            ready.SetResult();
            await start;
            Assert.IsTrue(panel.IsPlaying);
            Assert.IsFalse(completion.Task.IsCompleted);
            Assert.AreEqual(secondPath, player.Preloads.Single().Path);
            Assert.AreEqual(0, panel.NowPlayingRowIndex);
            Assert.AreEqual(0, rows.SelectedIndex);
        }
        finally
        {
            ready.TrySetResult();
            await start;
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            dispatcher.RunAll();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task ReceivedNextRequests_KeepEachStartWhileEarlierReadyIsPending()
    {
        string[] paths = [Path.GetTempFileName(), Path.GetTempFileName(), Path.GetTempFileName()];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)new TestBmsFile(path)).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("Valid input failed."), new ChartFileOperationSynchronizer());
        Task? firstNext = null;
        Task? secondNext = null;
        try
        {
            await panel.StartAtIndex(0);
            player.Ready = ready.Task;
            firstNext = panel.Next();
            secondNext = panel.Next();
            CollectionAssert.AreEqual(paths.Take(2).ToArray(), player.Starts.ToArray());
            Assert.IsFalse(firstNext.IsCompleted);
            Assert.IsFalse(secondNext.IsCompleted);
            ready.SetResult();
            await Task.WhenAll(firstNext, secondNext);
            CollectionAssert.AreEqual(paths, player.Starts.ToArray());
            Assert.AreEqual(paths[2], panel.NowPlayingBmsFile.path);
        }
        finally
        {
            ready.TrySetResult();
            if (firstNext != null) { await firstNext; }
            if (secondNext != null) { await secondNext; }
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            foreach (string path in paths) { File.Delete(path); }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InternalReady_InvalidInputNotifiesAndSkipsOnce(bool asynchronous)
    {
        string firstPath = Path.GetTempFileName();
        string nextPath = Path.GetTempFileName();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidDataException("Invalid selected chart.");
        if (!asynchronous) { ready.SetException(failure); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer
        {
            BeginOperation = path => path == firstPath
                ? new PlaybackStartOperation(ready.Task, ready.Task)
                : new PlaybackStartOperation(Task.CompletedTask, completion.Task)
        };
        int notices = 0;
        var rows = new MainChartListViewModel { Rows = new List<object> { new TestBmsFile(firstPath), new TestBmsFile(nextPath) } };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => notices++, new ChartFileOperationSynchronizer());
        Task start = panel.StartAtIndex(0);
        try
        {
            await player.StartObserved.Task;
            if (asynchronous)
            {
                Assert.IsTrue(panel.NowPlayingBmsFile.status.HasFlag(BMSFile.BMSFileStatus.LOADING));
                ready.SetException(failure);
            }
            await start;
            Assert.AreEqual(1, notices);
            CollectionAssert.AreEqual(new[] { firstPath, nextPath }, player.Starts.ToArray());
            Assert.AreEqual(nextPath, panel.NowPlayingBmsFile.path);
            Assert.IsTrue(panel.IsPlaying);
        }
        finally
        {
            ready.TrySetException(failure);
            await start;
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            File.Delete(firstPath);
            File.Delete(nextPath);
        }
    }

    [TestMethod]
    public async Task InternalShortSong_CallbackAndCompletionBeforeReadyCauseOneNaturalStop()
    {
        string path = Path.GetTempFileName();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Ready = ready.Task };
        var rows = new MainChartListViewModel { Rows = new List<object> { new TestBmsFile(path) } };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("Valid input failed."), new ChartFileOperationSynchronizer());
        Task closed = player.WaitForCommandAsync("Close");
        Task start = panel.StartAtIndex(0);
        try
        {
            await player.StartObserved.Task;
            player.ExitHandler?.Invoke(player, EventArgs.Empty);
            player.ExitHandler?.Invoke(player, EventArgs.Empty);
            Assert.IsFalse(closed.IsCompleted);
            ready.SetResult();
            await start;
            await closed;
            Assert.AreEqual(1, player.CloseProcessCount);
            Assert.IsNull(panel.NowPlayingBmsFile);
        }
        finally
        {
            ready.TrySetResult();
            await start;
            await panel.CloseProcess();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task NaturalSinglePlayStop_NotifiesPreloadCleanupFailureOnceWithoutStartingNextSong()
    {
        string[] paths = [Path.GetTempFileName(), Path.GetTempFileName()];
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var dialogs = new FakePlaybackDialogService();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)new TestBmsFile(path)).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore { SinglePlay = true, RepeatPlay = false }, dialogs,
            _ => Assert.Fail("Valid input failed."), new ChartFileOperationSynchronizer());
        var failure = new InvalidOperationException("The next-song decoder cleanup failed.");
        try
        {
            await panel.StartAtIndex(0);
            Assert.AreEqual(paths[1], player.Preloads.Single().Path);
            player.CloseCompletion = () => { closeEntered.TrySetResult(); return closeCompletion.Task; };

            player.ExitHandler?.Invoke(player, EventArgs.Empty);
            completion.SetResult();
            await closeEntered.Task;
            closeCompletion.SetException(failure);
            await dialogs.WaitForPlaybackFailureAsync();

            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            CollectionAssert.AreEqual(new[] { paths[0] }, player.Starts.ToArray());
            Assert.AreEqual(1, player.CloseProcessCount);
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.IsFalse(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
        }
        finally
        {
            completion.TrySetResult();
            closeCompletion.TrySetResult();
            player.CloseCompletion = null;
            await panel.CloseForShutdown();
            await completion.Task;
            foreach (string path in paths) { File.Delete(path); }
        }
    }

    [TestMethod]
    public async Task AdoptedPreloadFailure_AfterStopNotifiesOnceWithoutRestarting()
    {
        string[] paths = [Path.GetTempFileName(), Path.GetTempFileName()];
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextPreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer
        {
            BeginOperation = path =>
            {
                if (path == paths[0]) { return new PlaybackStartOperation(Task.CompletedTask, firstCompletion.Task); }
                nextStartEntered.TrySetResult();
                return new PlaybackStartOperation(nextPreparation.Task, nextPreparation.Task);
            }
        };
        var dialogs = new FakePlaybackDialogService();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)new TestBmsFile(path)).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, _ => Assert.Fail("Valid input failed."),
            new ChartFileOperationSynchronizer());
        var failure = new InvalidOperationException("The adopted next-song preparation failed.");
        Task? next = null;
        Task? stop = null;
        try
        {
            await panel.StartAtIndex(0);
            Assert.AreEqual(paths[1], player.Preloads.Single().Path);
            next = panel.Next();
            await nextStartEntered.Task;
            stop = panel.StopPlayback(closeProcess: true);
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.IsFalse(next.IsCompleted);
            Assert.IsFalse(stop.IsCompleted);

            nextPreparation.SetException(failure);
            await Task.WhenAll(next, stop);
            await dialogs.WaitForPlaybackFailureAsync();

            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            CollectionAssert.AreEqual(paths, player.Starts.ToArray());
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.IsFalse(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
        }
        finally
        {
            nextPreparation.TrySetCanceled();
            firstCompletion.TrySetResult();
            if (next != null) { await next; }
            if (stop != null) { await stop; }
            await panel.CloseForShutdown();
            await firstCompletion.Task;
            foreach (string path in paths) { File.Delete(path); }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InternalReplacementAndShutdown_WaitForCloseTerminal(bool shutdown)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer
        {
            Duration = TimeSpan.FromSeconds(10),
            CloseCompletion = () => { entered.TrySetResult(); return cleanup.Task; }
        };
        var replacement = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(20) };
        PlaybackPanelViewModel panel = CreatePanel(player);
        Task terminal = shutdown ? panel.CloseForShutdown() : panel.ReplacePlayerAsync(replacement);
        try
        {
            await entered.Task;
            Assert.IsFalse(terminal.IsCompleted);
            Assert.AreEqual(TimeSpan.FromSeconds(10), panel.CurrentlyPlayingDuration);
            cleanup.SetResult();
            await terminal;
            if (!shutdown) { Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration); }
        }
        finally
        {
            cleanup.TrySetResult();
            await terminal;
            await panel.CloseProcess();
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreloadCandidate_DoesNotSkipTemporaryTargetAndAllowsPendingTargetWithoutCopy(bool destinationExists)
    {
        string root = Path.Combine(Path.GetTempPath(), "bms-preload-temporary-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "destination");
        if (destinationExists) { Directory.CreateDirectory(destination); }
        string currentPath = Path.Combine(root, "current.bms");
        string nextPath = Path.Combine(root, "pending.bms");
        string thirdPath = Path.Combine(root, "third.bms");
        foreach (string path in new[] { currentPath, nextPath, thirdPath }) { File.WriteAllText(path, string.Empty); }
        ChartFile nextChart = ChartPackageTestExtensions.CreateEntryWithInstallDestination(new TestBmsFile(nextPath), destination).Chart;
        var rows = new MainChartListViewModel { Rows = new List<object> { new TestBmsFile(currentPath), LibraryChartRow.FromChartFile(nextChart), new TestBmsFile(thirdPath) } };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("Valid input failed."), new ChartFileOperationSynchronizer());
        try
        {
            await panel.StartAtIndex(0);
            Assert.AreEqual(destinationExists ? null : nextPath, player.NextSongPath);
            Assert.AreEqual(destinationExists ? 0 : 1, player.Preloads.Count);
            Assert.AreEqual(currentPath, panel.NowPlayingBmsFile.path);
            Assert.AreEqual(0, rows.SelectedIndex);
        }
        finally
        {
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task LegacyStart_WaitsForPreviousCloseAndRetainsInputUntilInvocation(
        bool temporaryTarget, bool failAtInvocation)
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-legacy-start-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(directory, "source");
        string destination = Path.Combine(directory, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        string currentPath = Path.Combine(directory, "current.bms");
        string targetPath = Path.Combine(source, "next.bms");
        string playbackPath = temporaryTarget ? Path.Combine(destination, "next.bms") : targetPath;
        File.WriteAllText(currentPath, string.Empty);
        File.WriteAllText(targetPath, string.Empty);
        string songDbPath = Path.Combine(directory, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var currentFile = new TestBmsFile(currentPath);
        var targetFile = new TestBmsFile(targetPath);
        object targetRow = temporaryTarget
            ? LibraryChartRow.FromChartFile(ChartPackageTestExtensions
                .CreateEntryWithInstallDestination(targetFile, destination).Chart)
            : targetFile;
        var chartList = new MainChartListViewModel { Rows = new List<object> { currentFile, targetRow } };
        var player = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList), new InMemoryPlaybackSettingsStore(),
            dialogs, _ => Assert.Fail("有効な入力の開始順序を確認します。"), new ChartFileOperationSynchronizer());
        panel.AttachLibrary(new TestBmsLibrary(songDbPath));
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseClose = new ManualResetEventSlim();
        Task close = Task.CompletedTask;
        Task start = Task.CompletedTask;
        int startedCount = 0;
        bool inputExistedAtInvocation = false;
        var failure = new IOException("external player start failed");
        try
        {
            long generation = panel.BeginPlayback(currentFile, 0);
            Assert.IsTrue(await panel.TryPlayStart(generation, currentPath, null));
            panel.PlaybackStarted += (_, _) => Interlocked.Increment(ref startedCount);
            player.BeforeClose = () => { closeEntered.TrySetResult(); releaseClose.Wait(); };
            close = panel.StopPlayback(closeProcess: true);
            await closeEntered.Task;
            player.PlayStartTask = completion.Task;
            player.PlayStartException = failAtInvocation ? failure : null;
            player.BeforePlayStart = () => inputExistedAtInvocation = File.Exists(playbackPath);

            start = panel.StartAtIndex(1);

            Assert.IsFalse(start.IsCompleted, "旧playerの終了中は新しい開始呼出しを待ちます。");
            Assert.IsTrue(File.Exists(playbackPath), "開始呼出し前の入力を除去しません。");
            Assert.IsFalse(player.Commands.Contains("PlayStart:" + playbackPath));
            Assert.AreEqual(0, startedCount);
            Assert.IsTrue(panel.NowPlayingBmsFile?.status.HasFlag(BMSFile.BMSFileStatus.LOADING) == true);
            Assert.IsFalse(panel.IsPlaying);
            releaseClose.Set();
            await close;
            await start;

            Assert.IsTrue(inputExistedAtInvocation);
            Assert.AreEqual(1, player.Commands.Count(command => command == "PlayStart:" + playbackPath));
            Assert.IsTrue(File.Exists(targetPath));
            if (temporaryTarget) { Assert.IsFalse(File.Exists(playbackPath)); }
            if (failAtInvocation)
            {
                Assert.AreSame(failure, dialogs.LastPlaybackFailure);
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.AreEqual(0, startedCount);
                Assert.IsNull(panel.NowPlayingBmsFile);
            }
            else
            {
                Assert.IsFalse(completion.Task.IsCompleted, "選曲と一時配置はplayerの返却Taskの終了まで保持しません。");
                Assert.AreEqual(1, startedCount);
                Assert.IsTrue(panel.IsPlaying);
                Assert.AreEqual(targetPath, panel.NowPlayingBmsFile.path);
                Task notified = dialogs.WaitForPlaybackFailureAsync();
                completion.SetException(failure);
                await notified;
                Assert.AreSame(failure, dialogs.LastPlaybackFailure);
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.AreEqual(1, startedCount);
                Assert.IsNull(panel.NowPlayingBmsFile);
            }
        }
        finally
        {
            releaseClose.Set();
            completion.TrySetResult();
            await Task.WhenAll(close, start);
            player.BeforeClose = null;
            await panel.StopPlayback(closeProcess: true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task TemporaryCopy_IsRetainedUntilReadyAndRemovedBeforeCompletion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-temporary-ready-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(directory, "source");
        string destination = Path.Combine(directory, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        string chartPath = Path.Combine(source, "chart.bms");
        File.WriteAllText(chartPath, string.Empty);
        File.WriteAllBytes(Path.Combine(directory, "song.db"), []);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Ready = ready.Task, Completion = completion.Task };
        var operations = new ChartFileOperationSynchronizer();
        PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(chartPath, destination,
            Path.Combine(directory, "song.db"), player, new FakePlaybackDialogService(), out _, operations);
        Task start = panel.StartAtIndex(0);
        try
        {
            string copiedPath = await player.StartObserved.Task;
            Assert.IsFalse(player.LastAllowPreload);
            Assert.IsTrue(File.Exists(copiedPath));
            Assert.IsFalse(start.IsCompleted);
            ready.SetResult();
            await start;
            Assert.IsFalse(File.Exists(copiedPath));
            Assert.IsTrue(File.Exists(chartPath));
            Assert.IsFalse(completion.Task.IsCompleted);
            Assert.AreEqual(chartPath, panel.NowPlayingBmsFile.path);
        }
        finally
        {
            ready.TrySetResult();
            await start;
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PlaybackCommands_ExecuteThroughPlaybackOwner()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(player);

        Task closeObserved = player.WaitForCommandAsync("Close");
        panel.StopCommand.Execute();
        await closeObserved;

        Task fastForwardStartObserved = player.WaitForCommandAsync("FastForwardStart");
        panel.FastForwardStartCommand.Execute();
        await fastForwardStartObserved;

        Task fastForwardEndObserved = player.WaitForCommandAsync("FastForwardEnd");
        panel.FastForwardEndCommand.Execute();
        await fastForwardEndObserved;

        Task showEffectObserved = player.WaitForCommandAsync("ShowEffect");
        panel.ShowEffectCommand.Execute();
        await showEffectObserved;

        Task changePlaysideObserved = player.WaitForCommandAsync("ChangePlayside");
        panel.ChangePlaysideCommand.Execute();
        await changePlaysideObserved;

        Task increaseHighSpeedObserved = player.WaitForCommandAsync("IncreaseHighSpeed");
        panel.IncreaseHighSpeedCommand.Execute();
        await increaseHighSpeedObserved;

        Task decreaseHighSpeedObserved = player.WaitForCommandAsync("DecreaseHighSpeed");
        panel.DecreaseHighSpeedCommand.Execute();
        await decreaseHighSpeedObserved;
    }

    [TestMethod]
    public void PlaybackPanel_TracksTelemetryAndDelegatesPlayerCommands()
    {
        var player = new FakeBmsPlayer
        {
            Duration = TimeSpan.FromSeconds(120),
            StopTime = TimeSpan.FromSeconds(115),
            BmsDuration = TimeSpan.FromSeconds(100),
            MusicDuration = TimeSpan.FromSeconds(110),
            CurrentVoices = 2,
            MaxVoices = 8,
            NoteDensity = 3,
            NoteDensityMax = 9,
            Bpm = 180,
            MinBpm = 120,
            MaxBpm = 240,
            Total = 75.5,
            Combo = 12,
            Notes = 345,
            Measure = 16,
            LastMeasure = 64
        };
        PlaybackPanelViewModel panel = CreatePanel(player);

        Assert.AreEqual(player.Duration, panel.CurrentlyPlayingDuration);
        Assert.AreEqual(player.StopTime, panel.CurrentlyPlayingStopTime);
        Assert.AreEqual(player.BmsDuration, panel.CurrentlyPlayingBmsDuration);
        Assert.AreEqual(player.MusicDuration, panel.CurrentlyPlayingMusicDuration);
        Assert.AreEqual(player.CurrentVoices, panel.CurrentlyPlayingCurrentVoices);
        Assert.AreEqual(player.MaxVoices, panel.CurrentlyPlayingMaxVoices);
        Assert.AreEqual(player.NoteDensity, panel.CurrentlyPlayingNoteDensity);
        Assert.AreEqual(player.NoteDensityMax, panel.CurrentlyPlayingNoteDensityMax);
        Assert.AreEqual(player.Bpm, panel.CurrentlyPlayingBpm);
        Assert.AreEqual(player.MinBpm, panel.CurrentlyPlayingMinBpm);
        Assert.AreEqual(player.MaxBpm, panel.CurrentlyPlayingMaxBpm);
        Assert.AreEqual(player.Total, panel.CurrentlyPlayingTotal);
        Assert.AreEqual(player.Combo, panel.CurrentlyPlayingCombo);
        Assert.AreEqual(player.Notes, panel.CurrentlyPlayingNotes);
        Assert.AreEqual(player.Measure, panel.CurrentlyPlayingMeasure);
        Assert.AreEqual(player.LastMeasure, panel.CurrentlyPlayingLastMeasure);

        player.Duration = TimeSpan.FromSeconds(200);
        player.CurrentTime = TimeSpan.FromSeconds(5);
        player.Raise(nameof(IBMSPlayer.Duration));
        player.Raise(nameof(IBMSPlayer.CurrentTime));
        Assert.AreEqual(player.Duration, panel.CurrentlyPlayingDuration);
        Assert.AreEqual(player.CurrentTime, panel.CurrentlyPlayingTime);

        panel.CurrentlyPlayingTime = TimeSpan.FromSeconds(15);
        Assert.AreEqual(TimeSpan.FromSeconds(15), player.CurrentTime);

        long playbackGeneration = panel.BeginPlayback(new BMSFile(), 0);
        Assert.IsTrue(panel.TryPlayStart(playbackGeneration, "chart.bms", null).GetAwaiter().GetResult());
        panel.TogglePause();
        panel.RestartPlayingBmsFile();
        panel.FastForwardStart();
        panel.FastForwardEnd();
        panel.FastBackwardStart();
        panel.FastBackwardEnd();
        panel.ShowInfo();
        panel.ShowEffect();
        panel.ChangePlayside();
        panel.IncreaseHighSpeed();
        panel.DecreaseHighSpeed();
        int originalVolume = Settings.Default.uBMplayVolume;
        try
        {
            panel.PlayerVolume = originalVolume == 100 ? 99 : originalVolume + 1;
            Assert.IsTrue(player.Commands.Contains("VolumeChanged"));
        }
        finally
        {
            Settings.Default.uBMplayVolume = originalVolume;
        }

        CollectionAssert.AreEqual(
            new[]
            {
                "PlayStart:chart.bms",
                "Pause",
                "Restart",
                "FastForwardStart",
                "FastForwardEnd",
                "FastBackwardStart",
                "FastBackwardEnd",
                "ShowInfo",
                "ShowEffect",
                "ChangePlayside",
                "IncreaseHighSpeed",
                "DecreaseHighSpeed",
                "VolumeChanged"
            },
            player.Commands.ToArray());
    }

    [TestMethod]
    public void PlaybackPanel_DispatchesTelemetryAndSuppressesStalePlaybackEventsThroughUiBoundary()
    {
        var player = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var dispatcher = new QueuedPlaybackUiDispatcher();
        var panel = new PlaybackPanelViewModel(
            player,
            dispatcher,
            new MainChartListPlaybackQueue(new MainChartListViewModel()),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer());
        int startingCount = 0;
        int startedCount = 0;
        panel.PlaybackStarting += (_, _) => startingCount++;
        panel.PlaybackStarted += (_, _) => startedCount++;

        player.Duration = TimeSpan.FromSeconds(20);
        player.Raise(nameof(IBMSPlayer.Duration));
        long generation = panel.BeginPlayback(new BMSFile(), 0);
        panel.StopPlayback().GetAwaiter().GetResult();

        Assert.AreEqual(TimeSpan.FromSeconds(10), panel.CurrentlyPlayingDuration);
        Assert.AreEqual(0, startingCount);
        Assert.AreEqual(0, startedCount);
        Assert.AreEqual(2, dispatcher.PendingCount);

        dispatcher.RunAll();

        Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration);
        Assert.AreEqual(0, startingCount);
        Assert.AreEqual(0, startedCount);
        panel.NotifyPlaybackStarted(generation);
        dispatcher.RunAll();
        Assert.AreEqual(0, startedCount);
    }

    [TestMethod]
    public async Task PlaybackPanel_CloseRefreshUsesUiBoundaryAndIgnoresReplacedPlayer()
    {
        var first = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var dispatcher = new QueuedPlaybackUiDispatcher();
        var panel = new PlaybackPanelViewModel(
            first,
            dispatcher,
            new MainChartListPlaybackQueue(new MainChartListViewModel()),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer());

        first.Duration = TimeSpan.FromSeconds(20);
        panel.CloseProcess().GetAwaiter().GetResult();
        Assert.AreEqual(TimeSpan.FromSeconds(10), panel.CurrentlyPlayingDuration);
        dispatcher.RunAll();
        Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration);

        first.Duration = TimeSpan.FromSeconds(30);
        panel.CloseProcess().GetAwaiter().GetResult();
        var replacement = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(40) };
        Task pendingActions = dispatcher.WaitForPendingActionsAsync(2);
        Task replacementTask = panel.ReplacePlayerAsync(replacement);
        await Task.WhenAny(pendingActions, replacementTask);
        if (replacementTask.IsCompleted)
        {
            await replacementTask;
        }
        await pendingActions;
        dispatcher.RunAll();
        await replacementTask;

        Assert.AreEqual(TimeSpan.FromSeconds(40), panel.CurrentlyPlayingDuration);
    }

    [TestMethod]
    public void PlaybackPanel_StopDoesNotHoldSessionGuardAcrossPlayerClose()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(player);
        var replacementSession = new BMSFile();
        bool sessionProbeCompleted = false;
        player.BeforeClose = () =>
        {
            Task probe = Task.Run(() => panel.BeginPlayback(replacementSession, 7));
            sessionProbeCompleted = probe.Wait(TimeSpan.FromSeconds(2));
        };

        panel.BeginPlayback(new BMSFile(), 3);
        panel.StopPlayback(closeProcess: true).GetAwaiter().GetResult();

        Assert.IsTrue(sessionProbeCompleted);
        Assert.AreSame(replacementSession, panel.NowPlayingBmsFile);
        Assert.AreEqual(7, panel.NowPlayingRowIndex);
    }

    [TestMethod]
    public void PlaybackPanel_StopSerializesWithInFlightPlayerStartAndClosesItAfterward()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(player);
        var startEntered = new ManualResetEventSlim();
        var releaseStart = new ManualResetEventSlim();
        player.BeforePlayStart = () =>
        {
            startEntered.Set();
            releaseStart.Wait(TimeSpan.FromSeconds(5));
        };
        long generation = panel.BeginPlayback(new BMSFile(), 0);

        Task<bool> start = Task.Run(
            () => panel.TryPlayStart(generation, "race.bms", null));
        Assert.IsTrue(startEntered.Wait(TimeSpan.FromSeconds(5)));
        var stop = Task.Run(() => panel.StopPlayback(closeProcess: true));
        Assert.IsFalse(stop.Wait(TimeSpan.FromMilliseconds(100)));

        releaseStart.Set();
        Assert.IsTrue(stop.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(start.Wait(TimeSpan.FromSeconds(5)));
        CollectionAssert.AreEqual(
            new[] { "PlayStart:race.bms", "Close" },
            player.Commands.ToArray());
        Assert.IsNull(panel.NowPlayingBmsFile);
    }

    [TestMethod]
    public void PlaybackPanel_ReplacementClosesOldPlayerDetachesEventsAndKeepsWindowHost()
    {
        var first = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var second = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(20) };
        PlaybackPanelViewModel panel = CreatePanel(first);
        IExternalPlayerWindowHost host = new Win32ExternalPlayerWindowHost(new IntPtr(42));
        panel.AttachWindowHost(host);

        panel.ReplacePlayerAsync(second).GetAwaiter().GetResult();

        Assert.AreEqual(1, first.CloseProcessCount);
        Assert.AreSame(host, second.WindowHost);
        Assert.AreEqual(second.Duration, panel.CurrentlyPlayingDuration);

        first.Duration = TimeSpan.FromSeconds(99);
        first.Raise(nameof(IBMSPlayer.Duration));
        Assert.AreEqual(second.Duration, panel.CurrentlyPlayingDuration);

        panel.CloseProcess().GetAwaiter().GetResult();
        Assert.AreEqual(1, second.CloseProcessCount);
    }

    [TestMethod]
    public void PlaybackPanel_SettingsRuntimePortSeparatesReplacementAndAudioTestStop()
    {
        var first = new FakeBmsPlayer();
        var replacement = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(first);
        IExternalPlayerWindowHost host = new Win32ExternalPlayerWindowHost(new IntPtr(42));
        panel.AttachWindowHost(host);
        var playingFile = new BMSFile();
        panel.BeginPlayback(playingFile, 0);

        var settingsRuntime = (ISettingsDialogPlaybackRuntimePort)panel;
        settingsRuntime.ApplyPlayerSettingsAsync(replacement).GetAwaiter().GetResult();

        Assert.AreEqual(1, first.CloseProcessCount);
        Assert.AreSame(host, replacement.WindowHost);
        Assert.IsNull(panel.NowPlayingBmsFile);
        Assert.AreEqual(-1, panel.NowPlayingRowIndex);

        ((IAudioDeviceTestPlaybackPort)panel).StopPlayback().GetAwaiter().GetResult();

        Assert.AreEqual(1, replacement.CloseProcessCount);
        settingsRuntime.NotifySettingsChanged();
    }

    [TestMethod]
    public void PlaybackPanel_SettingsReplacementReturnsTaskWhilePreviousPlayerClosesOffCallerLane()
    {
        using var closeEntered = new ManualResetEventSlim();
        using var releaseClose = new ManualResetEventSlim();
        var first = new FakeBmsPlayer
        {
            BeforeClose = () =>
            {
                closeEntered.Set();
                releaseClose.Wait();
            }
        };
        var replacement = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(first);

        Task replacementTask =
            ((ISettingsDialogPlaybackRuntimePort)panel).ApplyPlayerSettingsAsync(replacement);

        Assert.IsTrue(closeEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(replacementTask.IsCompleted);
        releaseClose.Set();
        Assert.IsTrue(replacementTask.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, first.CloseProcessCount);
        Assert.AreEqual(replacement.Duration, panel.CurrentlyPlayingDuration);
    }

    [TestMethod]
    public void PlaybackPanel_SettingsRuntimeApplyFailurePreservesPreviousPlayerAndSession()
    {
        var first = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var replacement = new FakeBmsPlayer { ThrowOnTelemetryRead = true };
        PlaybackPanelViewModel panel = CreatePanel(first);
        var playingFile = new BMSFile();
        panel.BeginPlayback(playingFile, 0);

        try
        {
            ((ISettingsDialogPlaybackRuntimePort)panel)
                .ApplyPlayerSettingsAsync(replacement)
                .GetAwaiter()
                .GetResult();
            Assert.Fail("Expected replacement preparation to fail.");
        }
        catch (InvalidOperationException)
        {
        }

        Assert.AreSame(playingFile, panel.NowPlayingBmsFile);
        Assert.AreEqual(0, first.CloseProcessCount);
        Assert.AreEqual(0, replacement.CloseProcessCount);
        first.Duration = TimeSpan.FromSeconds(20);
        first.Raise(nameof(IBMSPlayer.Duration));
        Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration);
    }

    [TestMethod]
    public void PlaybackPanel_OwnsSessionStateAndIgnoresStaleExitCallbacks()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(player);
        var first = new BMSFile();
        var second = new BMSFile();
        int exitCount = 0;
        int startingCount = 0;
        int startedCount = 0;
        panel.PlaybackStarting += (_, _) => startingCount++;
        panel.PlaybackStarted += (_, _) => startedCount++;

        long firstGeneration = panel.BeginPlayback(first, 3);
        Assert.IsTrue(panel.TryPlayStart(firstGeneration, "first.bms", (_, _) => exitCount++).GetAwaiter().GetResult());
        Action<object, EventArgs> firstExit = player.ExitHandler!;
        panel.NotifyPlaybackStarted(firstGeneration);

        Assert.AreSame(first, panel.NowPlayingBmsFile);
        Assert.AreEqual(3, panel.NowPlayingRowIndex);
        Assert.IsTrue(panel.IsPlaying);
        Assert.AreEqual(1, startingCount);
        Assert.AreEqual(1, startedCount);

        long secondGeneration = panel.BeginPlayback(second, 4);
        Assert.IsTrue(panel.TryPlayStart(secondGeneration, "second.bms", (_, _) => exitCount++).GetAwaiter().GetResult());
        Action<object, EventArgs> secondExit = player.ExitHandler!;
        firstExit(player, EventArgs.Empty);
        Assert.AreEqual(0, exitCount);

        secondExit(player, EventArgs.Empty);
        Assert.AreEqual(1, exitCount);

        panel.StopPlayback().GetAwaiter().GetResult();
        secondExit(player, EventArgs.Empty);
        Assert.AreEqual(1, exitCount);
        Assert.IsNull(panel.NowPlayingBmsFile);
        Assert.AreEqual(-1, panel.NowPlayingRowIndex);
        Assert.IsTrue(panel.IsStoppedOrPaused);
        Assert.AreEqual(0, player.CloseProcessCount);
    }

    [TestMethod]
    public void PlaybackPanel_TogglePausePublishesPlayingAndPausedStates()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(player);
        var file = new BMSFile();
        var changed = new List<string>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        long generation = panel.BeginPlayback(file, 0);
        Assert.IsTrue(panel.TryPlayStart(generation, "chart.bms", null).GetAwaiter().GetResult());

        Assert.IsTrue(panel.IsPlaying);
        Assert.IsFalse(panel.IsPaused);
        Assert.IsFalse(panel.IsStoppedOrPaused);
        changed.Clear();

        panel.Start(forceNewPlay: false).GetAwaiter().GetResult();

        Assert.IsFalse(panel.IsPlaying);
        Assert.IsTrue(panel.IsPaused);
        Assert.IsTrue(panel.IsStoppedOrPaused);
        CollectionAssert.IsSubsetOf(
            new[] { nameof(panel.IsPlaying), nameof(panel.IsPaused), nameof(panel.IsStoppedOrPaused) },
            changed);
        changed.Clear();

        panel.Start(forceNewPlay: false).GetAwaiter().GetResult();

        Assert.IsTrue(panel.IsPlaying);
        Assert.IsFalse(panel.IsPaused);
        Assert.IsFalse(panel.IsStoppedOrPaused);
        Assert.AreEqual(2, player.Commands.Count(command => command == "Pause"));
        CollectionAssert.IsSubsetOf(
            new[] { nameof(panel.IsPlaying), nameof(panel.IsPaused), nameof(panel.IsStoppedOrPaused) },
            changed);
    }

    [TestMethod]
    public void PlaybackPanel_PlayerExitAdvancesExactlyOnceToTheNextChart()
    {
        string firstPath = Path.GetTempFileName();
        string secondPath = Path.GetTempFileName();
        bool originalRepeat = Settings.Default.RepeatPlayMode;
        bool originalFolderSkip = Settings.Default.FolderSkipPlayMode;
        bool originalSingle = Settings.Default.SinglePlayMode;
        var player = new FakeBmsPlayer();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(firstPath), new TestBmsFile(secondPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            Settings.Default.RepeatPlayMode = false;
            Settings.Default.FolderSkipPlayMode = false;
            Settings.Default.SinglePlayMode = false;
            panel.Start().GetAwaiter().GetResult();
            Action<object, EventArgs> firstExit = player.ExitHandler!;

            firstExit(player, EventArgs.Empty);

            Assert.AreEqual(secondPath, panel.NowPlayingBmsFile.path);
            Assert.AreEqual(1, panel.NowPlayingRowIndex);
            Assert.AreEqual(1, chartList.SelectedIndex);
            Assert.IsFalse(chartList.Rows.Cast<TestBmsFile>().ElementAt(0).status.HasFlag(BMSFile.BMSFileStatus.PLAYALL));
            Assert.IsTrue(panel.IsPlaying);
            CollectionAssert.AreEqual(
                new[] { "PlayStart:" + firstPath, "PlayStart:" + secondPath },
                player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());

            firstExit(player, EventArgs.Empty);

            Assert.AreEqual(1, panel.NowPlayingRowIndex);
            Assert.AreEqual(2, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
        }
        finally
        {
            Settings.Default.RepeatPlayMode = originalRepeat;
            Settings.Default.FolderSkipPlayMode = originalFolderSkip;
            Settings.Default.SinglePlayMode = originalSingle;
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public void PlaybackPanel_StopReplaceAndCloseInvalidatePreparedOrRunningSessions()
    {
        var firstPlayer = new FakeBmsPlayer();
        var replacementPlayer = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(firstPlayer);

        long stoppedGeneration = panel.BeginPlayback(new BMSFile(), 1);
        panel.StopPlayback().GetAwaiter().GetResult();
        Assert.IsFalse(panel.TryPlayStart(stoppedGeneration, "stopped.bms", null).GetAwaiter().GetResult());
        Assert.IsFalse(firstPlayer.Commands.Any(command => command == "PlayStart:stopped.bms"));

        long replacedGeneration = panel.BeginPlayback(new BMSFile(), 2);
        panel.ReplacePlayerAsync(replacementPlayer).GetAwaiter().GetResult();
        Assert.IsFalse(panel.TryPlayStart(replacedGeneration, "replaced.bms", null).GetAwaiter().GetResult());
        Assert.IsFalse(replacementPlayer.Commands.Any(command => command == "PlayStart:replaced.bms"));

        long runningGeneration = panel.BeginPlayback(new BMSFile(), 3);
        int exitCount = 0;
        Assert.IsTrue(panel.TryPlayStart(runningGeneration, "running.bms", (_, _) => exitCount++).GetAwaiter().GetResult());
        Action<object, EventArgs> exit = replacementPlayer.ExitHandler!;
        panel.CloseProcess().GetAwaiter().GetResult();
        exit(replacementPlayer, EventArgs.Empty);

        Assert.AreEqual(0, exitCount);
        Assert.AreEqual(1, replacementPlayer.CloseProcessCount);
    }

    [TestMethod]
    public void PlaybackPanel_StopsAfterAllRepeatCandidatesAreUnavailable()
    {
        bool originalRepeat = Settings.Default.RepeatPlayMode;
        var chartList = new MainChartListViewModel
        {
            Rows = Enumerable.Range(0, 10000).Select(_ => (object)new BMSFile()).ToList(),
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            new FakeBmsPlayer(),
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            Settings.Default.RepeatPlayMode = true;

            panel.Start().GetAwaiter().GetResult();

            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
        }
        finally
        {
            Settings.Default.RepeatPlayMode = originalRepeat;
        }
    }

    [TestMethod]
    public void PlaybackPanel_StopsAfterManyInvalidChartsWithoutRecursiveAdvance()
    {
        string chartPath = Path.GetTempFileName();
        bool originalRepeat = Settings.Default.RepeatPlayMode;
        bool originalFolderSkip = Settings.Default.FolderSkipPlayMode;
        var player = new FakeBmsPlayer { PlayStartException = new InvalidDataException("invalid chart") };
        var chartList = new MainChartListViewModel
        {
            Rows = Enumerable.Range(0, 4096).Select(_ => (object)new TestBmsFile(chartPath)).ToList(),
            SelectedIndex = 0
        };
        int warningCount = 0;
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            new FakePlaybackDialogService(),
            _ => warningCount++,
            new ChartFileOperationSynchronizer());
        try
        {
            Settings.Default.RepeatPlayMode = false;
            Settings.Default.FolderSkipPlayMode = false;

            panel.Start().GetAwaiter().GetResult();

            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.AreEqual(4096, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(4096, warningCount);
        }
        finally
        {
            Settings.Default.RepeatPlayMode = originalRepeat;
            Settings.Default.FolderSkipPlayMode = originalFolderSkip;
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public void PlaybackPanel_NotifiesAndStopsAfterPlayerStartFailure()
    {
        string chartPath = Path.GetTempFileName();
        var failure = new IOException("player start failed");
        var player = new FakeBmsPlayer { PlayStartException = failure };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        dialogs.BeforePlaybackFailureNotification = () =>
        {
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
        };
        try
        {
            panel.Start().GetAwaiter().GetResult();

            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public async Task PlaybackPanel_ObservesAsynchronousPlayerStartFailure()
    {
        string chartPath = Path.GetTempFileName();
        var failure = new IOException("async player start failed");
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            panel.Start().GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);

            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            completion.SetException(failure);

            await failureNotification;
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.AreEqual(0, player.CloseProcessCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public async Task PlaybackPanel_DefersExitUntilAsynchronousStartSucceeds()
    {
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        PlaybackPanelViewModel panel = CreatePanel(player);
        var file = new BMSFile();
        int exitCount = 0;
        long generation = panel.BeginPlayback(file, 0);
        Task<bool> playStartTask = panel.TryPlayStart(generation, "chart.bms", (_, _) => exitCount++);

        Action<object, EventArgs> exitHandler = player.ExitHandler!;
        exitHandler(player, EventArgs.Empty);

        Assert.AreEqual(0, exitCount);
        Assert.AreSame(file, panel.NowPlayingBmsFile);

        completion.SetResult(new object());

        Assert.IsTrue(await playStartTask);
        Assert.AreEqual(1, exitCount);
        Assert.AreSame(file, panel.NowPlayingBmsFile);
    }

    [TestMethod]
    public async Task PlaybackPanel_DropsExitAfterAsynchronousStartFailure()
    {
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var failure = new IOException("async player start failed before exit");
        PlaybackPanelViewModel panel = CreatePanel(player);
        var file = new BMSFile();
        int exitCount = 0;
        long generation = panel.BeginPlayback(file, 0);
        Task<bool> playStartTask = panel.TryPlayStart(generation, "chart.bms", (_, _) => exitCount++);

        completion.SetException(failure);

        await Assert.ThrowsExceptionAsync<IOException>(async () => await playStartTask);
        Assert.IsNull(panel.NowPlayingBmsFile);
        player.ExitHandler!(player, EventArgs.Empty);

        Assert.AreEqual(0, exitCount);
        Assert.AreEqual(-1, panel.NowPlayingRowIndex);
    }

    [TestMethod]
    public async Task PlaybackPanel_StopsCurrentPlaybackWhenAsyncPlayerStartIsCanceled()
    {
        string chartPath = Path.GetTempFileName();
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            var stopped = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler stoppedHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(PlaybackPanelViewModel.NowPlayingBmsFile)
                    && panel.NowPlayingBmsFile == null)
                {
                    stopped.TrySetResult(null);
                }
            };
            panel.PropertyChanged += stoppedHandler;
            try
            {
                panel.StartCommand.Execute();
                completion.SetCanceled();

                await stopped.Task;
            }
            finally
            {
                panel.PropertyChanged -= stoppedHandler;
            }
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public void PlaybackPanel_TreatsAlreadyCanceledPlayerStartAsStoppedWithoutFailureDialog()
    {
        string chartPath = Path.GetTempFileName();
        var player = new FakeBmsPlayer
        {
            PlayStartTask = Task.FromCanceled<object>(new CancellationToken(canceled: true))
        };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            panel.Start().GetAwaiter().GetResult();

            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public async Task PlaybackPanel_IgnoresLateStartFailureAfterNewGenerationBegins()
    {
        var firstCompletion = new TaskCompletionSource<object>();
        var secondCompletion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = firstCompletion.Task };
        var dialogs = new FakePlaybackDialogService();
        PlaybackPanelViewModel panel = CreatePanel(player, dialogs);
        var firstFile = new BMSFile();
        var secondFile = new BMSFile();

        long firstGeneration = panel.BeginPlayback(firstFile, 0);
        Task<bool> firstObservationTask = panel.TryPlayStart(firstGeneration, "first.bms", null);

        panel.StopPlayback().GetAwaiter().GetResult();
        long secondGeneration = panel.BeginPlayback(secondFile, 1);
        player.PlayStartTask = secondCompletion.Task;
        Task<bool> secondObservationTask = panel.TryPlayStart(secondGeneration, "second.bms", null);

        firstCompletion.SetException(new IOException("stale player start failed"));

        await Assert.ThrowsExceptionAsync<IOException>(
            async () => await firstObservationTask);
        Assert.AreSame(secondFile, panel.NowPlayingBmsFile);
        Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        secondCompletion.SetCanceled();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            async () => await secondObservationTask);
        Assert.IsNull(panel.NowPlayingBmsFile);
    }

    [TestMethod]
    public async Task PlaybackPanel_IgnoresLateStartFailureAfterStopAndReplacement()
    {
        var firstCompletion = new TaskCompletionSource<object>();
        var secondCompletion = new TaskCompletionSource<object>();
        var firstPlayer = new FakeBmsPlayer { PlayStartTask = firstCompletion.Task };
        var replacementPlayer = new FakeBmsPlayer { PlayStartTask = secondCompletion.Task };
        var dialogs = new FakePlaybackDialogService();
        PlaybackPanelViewModel panel = CreatePanel(firstPlayer, dialogs);
        var firstFile = new BMSFile();
        long firstGeneration = panel.BeginPlayback(firstFile, 0);
        Task<bool> firstStart = panel.TryPlayStart(firstGeneration, "first.bms", null);

        panel.StopPlayback().GetAwaiter().GetResult();
        firstCompletion.SetException(new IOException("stale after stop"));
        await Assert.ThrowsExceptionAsync<IOException>(async () => await firstStart);
        Assert.IsNull(panel.NowPlayingBmsFile);
        Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);

        var secondFile = new BMSFile();
        long secondGeneration = panel.BeginPlayback(secondFile, 1);
        firstPlayer.PlayStartTask = secondCompletion.Task;
        Task<bool> secondStart = panel.TryPlayStart(secondGeneration, "second.bms", null);
        panel.ReplacePlayerAsync(replacementPlayer).GetAwaiter().GetResult();
        secondCompletion.SetException(new IOException("stale after replacement"));

        await Assert.ThrowsExceptionAsync<IOException>(async () => await secondStart);
        Assert.AreSame(secondFile, panel.NowPlayingBmsFile);
        Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        panel.StopPlayback().GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task PlaybackPanel_RecoversAfterAsynchronousPlayerStartFailure()
    {
        string chartPath = Path.GetTempFileName();
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            panel.Start().GetAwaiter().GetResult();
            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            completion.SetException(new IOException("recoverable player start failed"));
            await failureNotification;

            player.PlayStartTask = Task.CompletedTask;
            panel.Start().GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(2, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreEqual(0, player.CloseProcessCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public void PlaybackPanel_DirectStartContainsNotificationFailureWithoutFollowingPlayback()
    {
        string chartPath = Path.GetTempFileName();
        var playerFailure = new IOException("player start failed");
        var notificationFailure = new InvalidOperationException("notification failed");
        var player = new FakeBmsPlayer { PlayStartException = playerFailure };
        var dialogs = new FakePlaybackDialogService { PlaybackFailureException = notificationFailure };
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            panel.Start().GetAwaiter().GetResult();
            Assert.AreSame(playerFailure, dialogs.LastPlaybackFailure);
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.AreEqual(0, player.CloseProcessCount);
            Assert.AreEqual(
                1,
                player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public async Task PlaybackPanel_CommandFailureUsesPlaybackNotification()
    {
        var operationFailure = new IOException("player stop failed");
        var notificationFailure = new InvalidOperationException("notification failed");
        var player = new FakeBmsPlayer
        {
            BeforeClose = () => throw operationFailure
        };
        var dialogs = new FakePlaybackDialogService
        {
            PlaybackFailureException = notificationFailure
        };
        PlaybackPanelViewModel panel = CreatePanel(player, dialogs);
        Task failureNotification = dialogs.WaitForPlaybackFailureAsync();

        panel.StopCommand.Execute();

        await failureNotification.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreSame(operationFailure, dialogs.LastPlaybackFailure);
    }

    [TestMethod]
    public async Task PlaybackPanel_ContainsAsynchronousNotificationFailure()
    {
        string chartPath = Path.GetTempFileName();
        var playerFailure = new IOException("async player start failed");
        var notificationFailure = new InvalidOperationException("async notification failed");
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var dialogs = new FakePlaybackDialogService { PlaybackFailureException = notificationFailure };
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            panel.Start().GetAwaiter().GetResult();
            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            completion.SetException(playerFailure);

            await failureNotification;
            Assert.AreSame(playerFailure, dialogs.LastPlaybackFailure);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public void PlaybackPanel_DoesNotStopReplacementAfterSynchronousStartFailure()
    {
        string chartPath = Path.GetTempFileName();
        var failure = new IOException("synchronous player start failed");
        var player = new FakeBmsPlayer { PlayStartTask = Task.FromException<object>(failure) };
        var replacement = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        Task replacementTask = Task.CompletedTask;
        player.BeforePlayStart = () => replacementTask = panel.ReplacePlayerAsync(replacement);
        try
        {
            panel.Start().GetAwaiter().GetResult();
            replacementTask.GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(0, replacement.CloseProcessCount);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public void PlaybackPanel_DoesNotStopReplacementAfterSynchronousStartCancellation()
    {
        string chartPath = Path.GetTempFileName();
        var player = new FakeBmsPlayer
        {
            PlayStartTask = Task.FromCanceled<object>(new CancellationToken(canceled: true))
        };
        var replacement = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(chartPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
        Task replacementTask = Task.CompletedTask;
        player.BeforePlayStart = () => replacementTask = panel.ReplacePlayerAsync(replacement);
        try
        {
            panel.Start().GetAwaiter().GetResult();
            replacementTask.GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(0, replacement.CloseProcessCount);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public async Task PlaybackPanel_TemporaryInstallAsyncStartFailureAndCancellationClearStateWithoutClosingPlayer()
    {
        bool originalLr2Body = Settings.Default.UsePlayerLR2body;
        bool originalLr2Database = Settings.Default.OperationModeLR2DB;
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaybackAsyncFailure_" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "Source");
        string installDirectory = Path.Combine(root, "Install");
        string chartPath = Path.Combine(sourceDirectory, "chart.bms");
        string songDbPath = Path.Combine(root, "song.db");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(chartPath, "#TITLE test");
        File.WriteAllBytes(songDbPath, []);
        var firstCompletion = new TaskCompletionSource<object>();
        var secondCompletion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = firstCompletion.Task };
        var dialogs = new FakePlaybackDialogService();
        try
        {
            Settings.Default.UsePlayerLR2body = true;
            Settings.Default.OperationModeLR2DB = true;
            PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(
                chartPath,
                installDirectory,
                songDbPath,
                player,
                dialogs);

            panel.Start().GetAwaiter().GetResult();
            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            firstCompletion.SetException(new IOException("temporary install start failed"));
            await failureNotification;

            player.PlayStartTask = secondCompletion.Task;
            var stopped = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler stoppedHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(PlaybackPanelViewModel.NowPlayingBmsFile)
                    && panel.NowPlayingBmsFile == null)
                {
                    stopped.TrySetResult(null);
                }
            };
            panel.PropertyChanged += stoppedHandler;
            try
            {
                panel.Start().GetAwaiter().GetResult();
                secondCompletion.SetCanceled();
                await stopped.Task;
            }
            finally
            {
                panel.PropertyChanged -= stoppedHandler;
            }
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreEqual(0, player.CloseProcessCount);
        }
        finally
        {
            Settings.Default.UsePlayerLR2body = originalLr2Body;
            Settings.Default.OperationModeLR2DB = originalLr2Database;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PlaybackPanel_TemporaryInstallConfirmationControlsPlaybackWorkflow()
    {
        bool originalLr2Body = Settings.Default.UsePlayerLR2body;
        bool originalLr2Database = Settings.Default.OperationModeLR2DB;
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaybackDialog_" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "Source");
        string installDirectory = Path.Combine(root, "Install");
        string chartPath = Path.Combine(sourceDirectory, "chart.bms");
        string songDbPath = Path.Combine(root, "song.db");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(chartPath, "#TITLE test");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            Settings.Default.UsePlayerLR2body = true;
            Settings.Default.OperationModeLR2DB = true;

            var acceptedPlayer = new FakeBmsPlayer();
            var acceptedDialogs = new FakePlaybackDialogService { TemporaryInstallConfirmationResult = true };
            PlaybackPanelViewModel acceptedPanel = CreateTemporaryInstallPanel(
                chartPath,
                installDirectory,
                songDbPath,
                acceptedPlayer,
                acceptedDialogs);
            int startedCount = 0;
            acceptedPanel.PlaybackStarted += (_, _) => startedCount++;

            acceptedPanel.Start().GetAwaiter().GetResult();

            Assert.AreEqual(1, acceptedDialogs.TemporaryInstallConfirmationCount);
            Assert.IsTrue(acceptedPlayer.Commands.Contains("PlayStart:" + Path.Combine(installDirectory, Path.GetFileName(chartPath))));
            Assert.AreEqual(1, startedCount);
            Assert.IsNotNull(acceptedPanel.NowPlayingBmsFile);

            var rejectedPlayer = new FakeBmsPlayer();
            var rejectedDialogs = new FakePlaybackDialogService { TemporaryInstallConfirmationResult = false };
            PlaybackPanelViewModel rejectedPanel = CreateTemporaryInstallPanel(
                chartPath,
                installDirectory,
                songDbPath,
                rejectedPlayer,
                rejectedDialogs);

            rejectedPanel.Start().GetAwaiter().GetResult();

            Assert.AreEqual(1, rejectedDialogs.TemporaryInstallConfirmationCount);
            Assert.IsFalse(rejectedPlayer.Commands.Any(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(1, rejectedPlayer.CloseProcessCount);
            Assert.IsNull(rejectedPanel.NowPlayingBmsFile);
            Assert.AreEqual(-1, rejectedPanel.NowPlayingRowIndex);
        }
        finally
        {
            Settings.Default.UsePlayerLR2body = originalLr2Body;
            Settings.Default.OperationModeLR2DB = originalLr2Database;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PlaybackPanel_TemporaryInstallDialogFailureStopsAndPropagates()
    {
        bool originalLr2Body = Settings.Default.UsePlayerLR2body;
        bool originalLr2Database = Settings.Default.OperationModeLR2DB;
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaybackDialogFailure_" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "Source");
        string installDirectory = Path.Combine(root, "Install");
        string chartPath = Path.Combine(sourceDirectory, "chart.bms");
        string songDbPath = Path.Combine(root, "song.db");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(chartPath, "#TITLE test");
        File.WriteAllBytes(songDbPath, []);
        var failure = new InvalidOperationException("confirmation failed");
        var player = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService { TemporaryInstallConfirmationException = failure };
        try
        {
            Settings.Default.UsePlayerLR2body = true;
            Settings.Default.OperationModeLR2DB = true;
            PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(
                chartPath,
                installDirectory,
                songDbPath,
                player,
                dialogs);

            Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() => panel.Start().GetAwaiter().GetResult()));
            Assert.AreEqual(1, dialogs.TemporaryInstallConfirmationCount);
            Assert.AreEqual(1, player.CloseProcessCount);
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
        }
        finally
        {
            Settings.Default.UsePlayerLR2body = originalLr2Body;
            Settings.Default.OperationModeLR2DB = originalLr2Database;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaybackPanel_TableRowActivationNotifiesTemporaryInstallMoveFailure()
    {
        bool originalLr2Body = Settings.Default.UsePlayerLR2body;
        bool originalLr2Database = Settings.Default.OperationModeLR2DB;
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaybackActivationMoveFailure_" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "Source");
        string installDirectory = Path.Combine(root, "Install");
        string chartPath = Path.Combine(sourceDirectory, "chart.bms");
        string installedChartPath = Path.Combine(installDirectory, "chart.bms");
        string songDbPath = Path.Combine(root, "song.db");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(chartPath, "#TITLE source");
        File.WriteAllText(installedChartPath, "#TITLE installed");
        File.WriteAllBytes(songDbPath, []);
        var player = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        try
        {
            Settings.Default.UsePlayerLR2body = true;
            Settings.Default.OperationModeLR2DB = true;
            LibraryChartRow activationRow;
            PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(
                chartPath,
                installDirectory,
                songDbPath,
                player,
                dialogs,
                out activationRow);
            int startedCount = 0;
            panel.PlaybackStarted += (_, _) => startedCount++;

            using (FileStream sourceHandle = new(
                chartPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
                Assert.IsTrue(panel.HandleTableRowActivation(0, activationRow));
                await failureNotification.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.IsNotNull(dialogs.LastPlaybackFailure);
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.AreEqual(0, startedCount);
            Assert.IsFalse(player.Commands.Any(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.IsTrue(File.Exists(chartPath));
            Assert.IsTrue(File.Exists(installedChartPath));
        }
        finally
        {
            Settings.Default.UsePlayerLR2body = originalLr2Body;
            Settings.Default.OperationModeLR2DB = originalLr2Database;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MainChartListPlaybackQueue_ResolvesSnapshotBeforeReplacementWithoutReusingOldProvider()
    {
        string path = Path.GetTempFileName();
        string replacementPath = Path.GetTempFileName();
        try
        {
            ChartFile source = ChartFileProjection.FromBmsFile(new TestBmsFile(path), includeResourceReferences: false);
            var row = LibraryChartRow.FromChartFile(source);
            int providerReads = 0;
            bool disposed = false;
            row.SetChartTransientStateProvider((_, _) =>
            {
                Assert.IsFalse(disposed, "The disposed provider must never be accessed.");
                providerReads++;
                return ChartFileTransientState.Empty;
            });
            var chartList = new MainChartListViewModel { Rows = new List<object> { row } };
            var queue = new MainChartListPlaybackQueue(chartList);
            (BMSFile PlayerFile, ChartFile Chart) snapshot = queue.GetPlaybackFiles(0);
            int readsBeforeReplacement = providerReads;
            Assert.IsTrue(readsBeforeReplacement > 0);
            chartList.RowsReplacing += (_, _) => disposed = true;
            chartList.PrepareRowsReplacement();
            (BMSFile PlayerFile, ChartFile Chart) duringReplacement = await Task.Run(() => queue.GetPlaybackFiles(0));
            Assert.IsNull(duringReplacement.PlayerFile);
            Assert.IsNull(duringReplacement.Chart);
            Assert.AreEqual(path, snapshot.PlayerFile.path);
            Assert.AreEqual(path, snapshot.Chart.Path);
            Assert.AreEqual(readsBeforeReplacement, providerReads);
            chartList.Rows = new List<object> { new TestBmsFile(replacementPath) };
            Assert.AreEqual(replacementPath, queue.GetPlaybackFiles(0).PlayerFile.path);
        }
        finally
        {
            File.Delete(path);
            File.Delete(replacementPath);
        }
    }

    [TestMethod]
    public void MainChartListPlaybackQueue_ForwardsLiveRowsAndSelection()
    {
        object firstRow = new object();
        object secondRow = new object();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { firstRow },
            SelectedIndex = -1
        };
        var queue = new MainChartListPlaybackQueue(chartList);
        var changed = new List<string>();
        chartList.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        chartList.Rows = new List<object> { secondRow, firstRow };
        queue.SelectedIndex = 1;

        Assert.AreEqual(2, queue.Count);
        Assert.AreSame(secondRow, queue.GetRow(0));
        Assert.AreEqual(1, chartList.SelectedIndex);
        CollectionAssert.Contains(changed, nameof(MainChartListViewModel.SelectedIndex));
    }

    [TestMethod]
    public async Task PlaybackPanel_StartNextAndPreviousFollowLiveQueueSelection()
    {
        string firstPath = Path.GetTempFileName();
        string secondPath = Path.GetTempFileName();
        string thirdPath = Path.GetTempFileName();
        bool originalRepeat = Settings.Default.RepeatPlayMode;
        bool originalFolderSkip = Settings.Default.FolderSkipPlayMode;
        bool originalSingle = Settings.Default.SinglePlayMode;
        var player = new FakeBmsPlayer();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object>
            {
                new TestBmsFile(firstPath),
                new TestBmsFile(secondPath),
                new TestBmsFile(thirdPath)
            },
            SelectedIndex = 1
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            Settings.Default.RepeatPlayMode = false;
            Settings.Default.FolderSkipPlayMode = false;
            Settings.Default.SinglePlayMode = false;

            panel.HandleTableSelection(chartList.Rows[1]);
            Assert.AreEqual(secondPath, panel.DisplayedBmsPlayerFile.path);
            Task playStartObserved = player.WaitForCommandAsync("PlayStart:" + secondPath);
            Assert.IsTrue(panel.HandleTableRowActivation(1, chartList.Rows[1]));
            await playStartObserved;
            Assert.AreEqual(secondPath, panel.NowPlayingBmsFile.path);
            Assert.AreEqual(1, panel.NowPlayingRowIndex);
            Assert.AreEqual(1, chartList.SelectedIndex);
            panel.HandleTableSelection(chartList.Rows[0]);
            Assert.AreEqual(secondPath, panel.DisplayedBmsPlayerFile.path);
            Assert.IsFalse(panel.HandleTableRowActivation(0, new object()));

            panel.Next().GetAwaiter().GetResult();
            Assert.AreEqual(thirdPath, panel.NowPlayingBmsFile.path);
            Assert.AreEqual(2, panel.NowPlayingRowIndex);
            Assert.AreEqual(2, chartList.SelectedIndex);

            panel.Previous().GetAwaiter().GetResult();
            Assert.AreEqual(secondPath, panel.NowPlayingBmsFile.path);
            Assert.AreEqual(1, panel.NowPlayingRowIndex);
            Assert.AreEqual(1, chartList.SelectedIndex);
            CollectionAssert.AreEqual(
                new[] { "PlayStart:" + secondPath, "PlayStart:" + thirdPath, "PlayStart:" + secondPath },
                player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());
        }
        finally
        {
            Settings.Default.RepeatPlayMode = originalRepeat;
            Settings.Default.FolderSkipPlayMode = originalFolderSkip;
            Settings.Default.SinglePlayMode = originalSingle;
            File.Delete(firstPath);
            File.Delete(secondPath);
            File.Delete(thirdPath);
        }
    }

    // U3T4a/c: 通常停止は再開可能だが、terminal 受付閉鎖後は直接入口と古い exit も無効。
    [TestMethod]
    public async Task PlaybackPanel_ShutdownRejectsPlaybackInputsButOrdinaryStopAllowsRestart()
    {
        string firstPath = Path.GetTempFileName();
        string secondPath = Path.GetTempFileName();
        var player = new FakeBmsPlayer();
        var rows = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(firstPath), new TestBmsFile(secondPath) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(rows), new InMemoryPlaybackSettingsStore(),
            new FakePlaybackDialogService(), _ => { }, new ChartFileOperationSynchronizer());
        try
        {
            panel.Start().GetAwaiter().GetResult();
            panel.StopPlayback(closeProcess: true).GetAwaiter().GetResult();
            panel.Start().GetAwaiter().GetResult();
            Assert.AreEqual(2, player.Commands.Count(command => command == "PlayStart:" + firstPath));
            Action<object, EventArgs> exit = player.ExitHandler!;
            panel.BeginShutdown();
            long prepared = panel.BeginPlayback((BMSFile)rows.Rows[0]!, 0);
            string[] before = player.Commands.ToArray();

            await Task.Run(() =>
            {
                exit(player, EventArgs.Empty);
                panel.Next().GetAwaiter().GetResult();
                panel.Previous().GetAwaiter().GetResult();
                panel.Start().GetAwaiter().GetResult();
                panel.StartAtIndex(1).GetAwaiter().GetResult();
                panel.ExecuteTableRowActivation(1, rows.Rows[1]).GetAwaiter().GetResult();
                panel.StopPlayback(closeProcess: true).GetAwaiter().GetResult();
                panel.CloseProcess().GetAwaiter().GetResult();
                panel.TogglePause();
                panel.RestartPlayingBmsFile();
                panel.FastForwardStart();
                panel.FastForwardEnd();
                panel.FastBackwardStart();
                panel.FastBackwardEnd();
                panel.ShowInfo();
                panel.ShowEffect();
                panel.ChangePlayside();
                panel.IncreaseHighSpeed();
                panel.DecreaseHighSpeed();
                panel.PlayerVolume = 37;
                panel.CurrentlyPlayingTime = TimeSpan.FromSeconds(17);
            });
            Assert.IsFalse(await panel.TryPlayStart(prepared, firstPath, (sender, args) => panel.Next(sender, args).GetAwaiter().GetResult()));
            Assert.IsFalse(panel.HandleTableRowActivation(1, rows.Rows[1]));
            Assert.IsFalse(panel.NextCommand.CanExecute);
            Assert.IsFalse(panel.StartCommand.CanExecute);
            CollectionAssert.AreEqual(before, player.Commands.ToArray());
            Assert.AreEqual(TimeSpan.Zero, player.CurrentTime);
            panel.CloseForShutdown().GetAwaiter().GetResult();
            Assert.AreEqual(2, player.CloseProcessCount);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    // U3T4b: queue 解決中の Next は terminal 受付を再開できず、所有 Task は両方回収する。
    [TestMethod]
    public async Task PlaybackPanel_ShutdownDrainsEarlierNavigationWithoutStartingAnotherChart()
    {
        string firstPath = Path.GetTempFileName();
        string secondPath = Path.GetTempFileName();
        using var releaseRow = new ManualResetEventSlim();
        var enteredRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new FakeBmsPlayer();
        var rows = new MainChartListViewModel
        {
            Rows = new List<object> { new TestBmsFile(firstPath), new TestBmsFile(secondPath) },
            SelectedIndex = 0
        };
        var queue = new GatedPlaybackChartQueue(new MainChartListPlaybackQueue(rows), enteredRow, releaseRow);
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(),
            queue, new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(),
            _ => { }, new ChartFileOperationSynchronizer());
        Task? next = null;
        Task? terminal = null;
        try
        {
            panel.Start().GetAwaiter().GetResult();
            next = Task.Run(() => panel.Next());
            await enteredRow.Task.WaitAsync(TimeSpan.FromSeconds(5));
            panel.BeginShutdown();
            player.BeforeClose = () => Assert.IsTrue(queue.RowResolved, "曲解決を player close が追い越さない。");
            terminal = Task.Run(() =>
            {
                terminalStarted.SetResult();
                panel.CloseForShutdown().GetAwaiter().GetResult();
            });
            await terminalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseRow.Set();
            await Task.WhenAll(next, terminal).WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "PlayStart:" + firstPath, "Close" }, player.Commands.ToArray());
        }
        finally
        {
            releaseRow.Set();
            try
            {
                await Task.WhenAll(next ?? Task.CompletedTask, terminal ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                File.Delete(firstPath);
                File.Delete(secondPath);
            }
        }
    }

    [TestMethod]
    public async Task NaturalExit_QueuedBehindManualNextDoesNotAdvanceTheReplacementSong()
    {
        string[] paths = [Path.GetTempFileName(), Path.GetTempFileName(), Path.GetTempFileName(), Path.GetTempFileName()];
        using var releaseRow = new ManualResetEventSlim();
        var enteredRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)new TestBmsFile(path)).ToList() };
        var queue = new GatedPlaybackChartQueue(new MainChartListPlaybackQueue(rows), enteredRow, releaseRow);
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), queue,
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("Valid input failed."),
            new ChartFileOperationSynchronizer());
        var start = Task.Run(() => panel.StartAtIndex(0));
        Task? manualNext = null;
        Task? sentinel = null;
        try
        {
            await enteredRow.Task;
            manualNext = panel.Next();
            player.ExitHandler?.Invoke(player, EventArgs.Empty);
            sentinel = panel.Next();
            releaseRow.Set();
            await Task.WhenAll(start, manualNext, sentinel);
            CollectionAssert.AreEqual(paths.Take(3).ToArray(), player.Starts.ToArray());
            Assert.AreEqual(paths[2], panel.NowPlayingBmsFile.path);
        }
        finally
        {
            releaseRow.Set();
            await Task.WhenAll(start, manualNext ?? Task.CompletedTask, sentinel ?? Task.CompletedTask);
            await panel.CloseForShutdown();
            foreach (string path in paths) { File.Delete(path); }
        }
    }

    private sealed class GatedPlaybackChartQueue(
        IPlaybackChartQueue inner, TaskCompletionSource entered, ManualResetEventSlim release, int gatedIndex = 1) : IPlaybackChartQueue
    {
        private int rowResolved;
        private int claimed;
        private int resolveCount;
        internal int ResolveCount => Volatile.Read(ref resolveCount);
        internal bool RowResolved => Volatile.Read(ref rowResolved) != 0;
        public int Count => inner.Count;
        public int SelectedIndex { get => inner.SelectedIndex; set => inner.SelectedIndex = value; }
        public object GetRow(int index)
        {
            Interlocked.Increment(ref resolveCount);
            object row = inner.GetRow(index);
            if (index == gatedIndex && Interlocked.Exchange(ref claimed, 1) == 0)
            {
                entered.TrySetResult();
                release.Wait();
                Interlocked.Exchange(ref rowResolved, 1);
            }
            return row;
        }
    }

    [TestMethod]
    public void PlaybackPanel_RowActivationDropsRequestWhenQueueSlotChanged()
    {
        string expectedPath = Path.GetTempFileName();
        string replacementPath = Path.GetTempFileName();
        var expectedRow = new TestBmsFile(expectedPath);
        var replacementRow = new TestBmsFile(replacementPath);
        var player = new FakeBmsPlayer();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { replacementRow },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer());
        try
        {
            panel.ExecuteTableRowActivation(0, expectedRow).GetAwaiter().GetResult();

            Assert.IsFalse(player.Commands.Any(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.IsNull(panel.NowPlayingBmsFile);

            chartList.Rows[0] = expectedRow;
            panel.ExecuteTableRowActivation(0, expectedRow).GetAwaiter().GetResult();

            Assert.AreEqual(expectedPath, panel.NowPlayingBmsFile.path);
            CollectionAssert.Contains(player.Commands.ToArray(), "PlayStart:" + expectedPath);
        }
        finally
        {
            File.Delete(expectedPath);
            File.Delete(replacementPath);
        }
    }

    [TestMethod]
    public void PlaybackPanel_OwnsPanelStateTransitionsUsingViewHostAvailability()
    {
        PlayerPanelState originalPanelState = Settings.Default.PlayerPanelState;
        PlaybackPanelViewModel panel = CreatePanel(new FakeBmsPlayer());
        try
        {
            panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;

            Assert.IsTrue(panel.CanSelectPanelState(PlayerPanelState.TITLE_LARGE, false));
            Assert.IsFalse(panel.CanSelectPanelState(PlayerPanelState.BMS_PLAYER, false));
            Assert.IsFalse(panel.TrySelectPanelState(PlayerPanelState.BMS_PLAYER, false));
            Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panel.PlayerPanelState);

            panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
            panel.RotatePanelState(bmsPlayerSurfaceAvailable: true);
            Assert.AreEqual(PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);

            panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
            panel.RotatePanelState(bmsPlayerSurfaceAvailable: true);
            Assert.AreEqual(PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);

            panel.RotatePanelState(bmsPlayerSurfaceAvailable: false);
            Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panel.PlayerPanelState);

            panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER;
            panel.ToggleCompactPanel();
            Assert.AreEqual(PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);
        }
        finally
        {
            Settings.Default.PlayerPanelState = originalPanelState;
        }
    }

    [TestMethod]
    public void PlaybackPanelViewResolvesUnavailableBmsSurfaceWithImageFallbackAndCompactPreserved()
    {
        Assert.AreEqual(
            PlayerPanelState.BMS_PLAYER,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.BMS_PLAYER, true));
        Assert.AreEqual(
            PlayerPanelState.TITLE_LARGE,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.BMS_PLAYER, false));
        Assert.AreEqual(
            PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER, true));
        Assert.AreEqual(
            PlayerPanelState.TITLE_SMALL,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER, false));
    }

    [TestMethod]
    public void PlaybackPanelView_CompiledTreeMaterializesCurrentSurfaceAndHeader()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window
            {
                Width = 640d,
                Height = 360d,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            try
            {
                var settings = new InMemoryPlaybackSettingsStore
                {
                    PlayerPanelState = PlayerPanelState.BMS_PLAYER,
                    UsesBmiIdxView = true
                };
                PlaybackPanelViewModel panel = CreatePanel(new FakeBmsPlayer(), settings);
                string[] values = new string[29];
                values[0] = "0123456789abcdef0123456789abcdef";
                values[1] = "Current Title";
                values[2] = "[Current Subtitle]";
                values[3] = "Current Artist";
                values[7] = Path.Combine(Path.GetTempPath(), "current.bms");
                var current = BMSFile.FromSongTableRawValues(values);
                panel.SetBmsPlayerHeader(current);

                var view = new PlaybackPanelView { DataContext = panel };
                window.Content = view;
                windowTest.ShowAndWaitForContentRendered(window);
                FlushRenderQueue(window);

                var playerHost = (WindowsFormsHost)view.FindName("windowsFormsHost");
                var artwork = (Image)view.FindName("gridBMSPlayerImage");
                var playButton = (Button)view.FindName("buttonBMSPlayerControlsPlayAndPauseButton");
                var title = (TextBlock)view.FindName("gridBMSPlayerControlsTitle");
                var subtitle = (TextBlock)view.FindName("gridBMSPlayerControlsSubtitle");
                var artist = (TextBlock)view.FindName("gridBMSPlayerControlsArtist");

                Assert.AreSame(panel, view.DataContext);
                Assert.AreSame(panel, playerHost.DataContext);
                Assert.IsNotNull(artwork.Source);
                Assert.AreSame(panel, playButton.DataContext);
                Assert.AreSame(panel, title.DataContext);
                Assert.AreEqual("Current Title", title.Text);
                Assert.AreEqual("[Current Subtitle]", subtitle.Text);
                Assert.AreEqual("Current Artist", artist.Text);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        });
    }

    [TestMethod]
    public void PlaybackPanelView_InitiallySynchronizesCompactStateWithoutAnimation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window { Width = 640d, Height = 360d, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                PlaybackPanelViewModel panel = CreatePanel(new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER;
                var view = new PlaybackPanelView { DataContext = panel };
                window.Content = view;

                windowTest.ShowAndWaitForContentRendered(window);
                FlushRenderQueue(window);

                Assert.AreEqual(PlayerPanelState.TITLE_SMALL, view.EffectivePlayerPanelState);
                AssertPlaybackPanelFinalState(view, compact: true);
                AssertPlaybackPanelHasNoAnimationClocks(view);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        });
    }

    [TestMethod]
    public void PlaybackPanelView_InitiallySynchronizesExpandedStateWithoutAnimation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window { Width = 640d, Height = 360d, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                PlaybackPanelViewModel panel = CreatePanel(new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                var view = new PlaybackPanelView { DataContext = panel };
                window.Content = view;

                windowTest.ShowAndWaitForContentRendered(window);
                FlushRenderQueue(window);

                Assert.AreEqual(PlayerPanelState.TITLE_LARGE, view.EffectivePlayerPanelState);
                AssertPlaybackPanelFinalState(view, compact: false);
                AssertPlaybackPanelHasNoAnimationClocks(view);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        });
    }

    [TestMethod]
    public void PlaybackPanelView_DataContextReplacementSynchronizesBothDirectionsWithoutAnimation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window { Width = 640d, Height = 360d, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                PlaybackPanelViewModel expandedPanel = CreatePanel(new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                expandedPanel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                PlaybackPanelViewModel compactPanel = CreatePanel(new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                compactPanel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
                var view = new PlaybackPanelView { DataContext = expandedPanel };
                window.Content = view;
                windowTest.ShowAndWaitForContentRendered(window);
                FlushRenderQueue(window);

                view.DataContext = compactPanel;
                AssertPlaybackPanelFinalState(view, compact: true);
                AssertPlaybackPanelHasNoAnimationClocks(view);

                view.DataContext = expandedPanel;
                AssertPlaybackPanelFinalState(view, compact: false);
                AssertPlaybackPanelHasNoAnimationClocks(view);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        });
    }

    [TestMethod]
    public void PlaybackPanelView_SameViewModelStateChangesUseTransitionsAndReloadSynchronizesImmediately()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new Window { Width = 640d, Height = 360d, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                PlaybackPanelViewModel panel = CreatePanel(new FakeBmsPlayer(), new InMemoryPlaybackSettingsStore());
                panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                var view = new PlaybackPanelView { DataContext = panel };
                window.Content = view;
                windowTest.ShowAndWaitForContentRendered(window);
                FlushRenderQueue(window);

                panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
                FlushRenderQueue(window);
                AssertPlaybackPanelHasTransitionClocks(view, compactTransition: true);

                panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                FlushRenderQueue(window);
                AssertPlaybackPanelHasTransitionClocks(view, compactTransition: false);

                window.Content = null;
                FlushRenderQueue(window);
                Assert.IsFalse(view.IsLoaded);
                AssertPlaybackPanelHasNoAnimationClocks(view);

                panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
                window.Content = view;
                FlushRenderQueue(window);
                Assert.IsTrue(view.IsLoaded);
                AssertPlaybackPanelFinalState(view, compact: true);
                AssertPlaybackPanelHasNoAnimationClocks(view);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        });
    }

    [TestMethod]
    public void PlaybackPanelView_ReloadRejectsEventsQueuedByThePreviousSubscription()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var queuedDispatcher = new QueuedPlaybackUiDispatcher();
            var panel = new PlaybackPanelViewModel(
                new FakeBmsPlayer(),
                queuedDispatcher,
                new MainChartListPlaybackQueue(new MainChartListViewModel()),
                new InMemoryPlaybackSettingsStore(),
                new FakePlaybackDialogService(),
                _ => { },
                new ChartFileOperationSynchronizer());
            var view = new PlaybackPanelView { DataContext = panel };
            var window = new Window
            {
                Width = 640d,
                Height = 360d,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            int startingCount = 0;
            int startedCount = 0;
            view.PlaybackStarting += (_, _) => startingCount++;
            view.PlaybackStarted += (_, _) => startedCount++;

            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                FlushRenderQueue(window);

                long oldGeneration = panel.BeginPlayback(new BMSFile(), 0);
                panel.NotifyPlaybackStarted(oldGeneration);
                window.Content = null;
                FlushRenderQueue(window);
                window.Content = view;
                FlushRenderQueue(window);

                queuedDispatcher.RunAll();
                Assert.AreEqual(0, startingCount);
                Assert.AreEqual(0, startedCount);

                long currentGeneration = panel.BeginPlayback(new BMSFile(), 0);
                panel.NotifyPlaybackStarted(currentGeneration);
                queuedDispatcher.RunAll();
                Assert.AreEqual(1, startingCount);
                Assert.AreEqual(1, startedCount);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        });
    }

    [TestMethod]
    public void PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var player = new FakeBmsPlayer();
            PlaybackPanelViewModel panel = CreatePanel(player);
            var replacementPlayer = new FakeBmsPlayer();
            PlaybackPanelViewModel replacementPanel = CreatePanel(replacementPlayer);
            var view = new PlaybackPanelView { DataContext = panel };
            int viewStartingCount = 0;
            int viewStartedCount = 0;
            view.PlaybackStarting += (_, _) => viewStartingCount++;
            view.PlaybackStarted += (_, _) => viewStartedCount++;
            var window = new Window
            {
                Width = 640d,
                Height = 360d,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.IsTrue(view.IsLoaded);

                long generation = panel.BeginPlayback(new BMSFile(), 0);
                panel.NotifyPlaybackStarted(generation);
                Assert.AreEqual(1, viewStartingCount);
                Assert.AreEqual(1, viewStartedCount);

                Task restartObserved = player.WaitForCommandAsync("Restart");
                view.HandlePreviousButtonClick(1);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    restartObserved,
                    "PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart.initial-restart");
                while (player.Commands.TryDequeue(out _))
                {
                }

                view.HandlePreviousButtonClick(1);
                view.DataContext = replacementPanel;
                PumpDispatcherFor(TimeSpan.FromMilliseconds(700));
                Assert.IsFalse(player.Commands.Contains("Restart"));
                Assert.IsFalse(replacementPlayer.Commands.Contains("Restart"));

                Task replacementRestartObserved = replacementPlayer.WaitForCommandAsync("Restart");
                view.HandlePreviousButtonClick(1);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    replacementRestartObserved,
                    "PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart.replacement-restart");
                while (replacementPlayer.Commands.TryDequeue(out _))
                {
                }

                var queuedDispatcher = new QueuedPlaybackUiDispatcher();
                var queuedPanel = new PlaybackPanelViewModel(
                    new FakeBmsPlayer(),
                    queuedDispatcher,
                    new MainChartListPlaybackQueue(new MainChartListViewModel()),
                    new SettingsPlaybackSettingsStore(() => Settings.Default),
                    new FakePlaybackDialogService(),
                    _ => { },
                    new ChartFileOperationSynchronizer());
                view.DataContext = queuedPanel;
                long queuedGeneration = queuedPanel.BeginPlayback(new BMSFile(), 0);
                queuedPanel.NotifyPlaybackStarted(queuedGeneration);
                view.DataContext = replacementPanel;
                queuedDispatcher.RunAll();
                Assert.AreEqual(1, viewStartingCount);
                Assert.AreEqual(1, viewStartedCount);

                var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
                banner.Background = Brushes.Red;
                banner.BorderThickness = new Thickness(1);
                view.DataContext = CreatePanel(new FakeBmsPlayer());
                Assert.IsNull(banner.Background);
                Assert.AreEqual(new Thickness(0), banner.BorderThickness);
                view.DataContext = replacementPanel;

                Exception? backgroundException = null;
                var backgroundThread = new Thread(() =>
                {
                    try
                    {
                        replacementPanel.SetBmsPlayerHeader(new BMSFile());
                    }
                    catch (Exception ex)
                    {
                        backgroundException = ex;
                    }
                });
                backgroundThread.Start();
                backgroundThread.Join();
                PumpDispatcherFor(TimeSpan.FromMilliseconds(50));
                Assert.IsNull(backgroundException);

                view.HandlePreviousButtonClick(1);
                window.Content = null;
                PumpDispatcherFor(TimeSpan.FromMilliseconds(700));

                Assert.IsFalse(view.IsLoaded);
                Assert.IsFalse(replacementPlayer.Commands.Contains("Restart"));
            }
            finally
            {
                window.Content = null;
                if (view.IsLoaded)
                {
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                }
                window.Close();
            }
        });
    }

    [TestMethod]
    public void PlaybackPanel_OwnsPersistedPanelAndPlaybackModeBindings()
    {
        PlayerPanelState originalPanelState = Settings.Default.PlayerPanelState;
        bool originalRepeat = Settings.Default.RepeatPlayMode;
        bool originalFolderSkip = Settings.Default.FolderSkipPlayMode;
        bool originalSingle = Settings.Default.SinglePlayMode;
        PlaybackPanelViewModel panel = CreatePanel(new FakeBmsPlayer());
        try
        {
            panel.PlayerPanelState = PlayerPanelState.BMS_PLAYER | PlayerPanelState.TITLE_SMALL;
            panel.RepeatPlayMode = !originalRepeat;
            panel.FolderSkipPlayMode = !originalFolderSkip;
            panel.SinglePlayMode = !originalSingle;

            Assert.AreEqual(panel.PlayerPanelState, Settings.Default.PlayerPanelState);
            Assert.AreEqual(panel.RepeatPlayMode, Settings.Default.RepeatPlayMode);
            Assert.AreEqual(panel.FolderSkipPlayMode, Settings.Default.FolderSkipPlayMode);
            Assert.AreEqual(panel.SinglePlayMode, Settings.Default.SinglePlayMode);
        }
        finally
        {
            Settings.Default.PlayerPanelState = originalPanelState;
            Settings.Default.RepeatPlayMode = originalRepeat;
            Settings.Default.FolderSkipPlayMode = originalFolderSkip;
            Settings.Default.SinglePlayMode = originalSingle;
        }
    }

    [TestMethod]
    public void PlaybackPanel_RefreshesCapabilityBindingsAfterSettingsChange()
    {
        bool originalUbMplay = Settings.Default.UsePlayeruBMplay;
        bool originalLr2 = Settings.Default.UsePlayerLR2body;
        bool originalBmi = Settings.Default.UsePlayerBMIIDXView;
        bool originalExternalPanelImage = Settings.Default.UseExternalPanelImage;
        string originalStagefilePath = Settings.Default.StagefilePath;
        PlaybackPanelViewModel panel = CreatePanel(new FakeBmsPlayer());
        var changed = new HashSet<string>(StringComparer.Ordinal);
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        try
        {
            Settings.Default.UsePlayeruBMplay = false;
            Settings.Default.UsePlayerLR2body = true;
            Settings.Default.UsePlayerBMIIDXView = false;
            Settings.Default.UseExternalPanelImage = !originalExternalPanelImage;
            Settings.Default.StagefilePath = "playback-panel-stage.png";
            panel.NotifySettingsChanged();

            Assert.IsFalse(panel.CanSeek);
            Assert.IsFalse(panel.CanChangeHighSpeed);
            Assert.IsFalse(panel.CanShowInfo);
            Assert.IsFalse(panel.CanShowEffect);
            Assert.IsFalse(panel.CanChangePlayside);
            Assert.AreEqual(!originalExternalPanelImage, panel.UseExternalPanelImage);
            Assert.AreEqual("playback-panel-stage.png", panel.StagefilePath);
            CollectionAssert.IsSubsetOf(
                new[] { "PlayerPanelState", "CanSeek", "CanChangeHighSpeed", "CanShowInfo", "CanShowEffect", "CanChangePlayside", "UseExternalPanelImage", "StagefilePath" },
                changed.ToArray());

            Settings.Default.UsePlayeruBMplay = true;
            Settings.Default.UsePlayerLR2body = false;
            panel.NotifySettingsChanged();
            Assert.IsTrue(panel.CanSeek);
            Assert.IsTrue(panel.CanChangeHighSpeed);
            Assert.IsTrue(panel.CanShowInfo);
            Assert.IsTrue(panel.CanShowEffect);
            Assert.IsTrue(panel.CanChangePlayside);
        }
        finally
        {
            Settings.Default.UsePlayeruBMplay = originalUbMplay;
            Settings.Default.UsePlayerLR2body = originalLr2;
            Settings.Default.UsePlayerBMIIDXView = originalBmi;
            Settings.Default.UseExternalPanelImage = originalExternalPanelImage;
            Settings.Default.StagefilePath = originalStagefilePath;
        }
    }

    private static PlaybackPanelViewModel CreatePanel(IBMSPlayer player)
    {
        return CreatePanel(player, new FakePlaybackDialogService());
    }

    private static PlaybackPanelViewModel CreatePanel(
        IBMSPlayer player,
        FakePlaybackDialogService dialogs)
    {
        return CreatePanel(player, new SettingsPlaybackSettingsStore(() => Settings.Default), dialogs);
    }

    private static PlaybackPanelViewModel CreatePanel(
        IBMSPlayer player,
        IPlaybackSettingsStore playbackSettings)
    {
        return CreatePanel(player, playbackSettings, new FakePlaybackDialogService());
    }

    private static PlaybackPanelViewModel CreatePanel(
        IBMSPlayer player,
        IPlaybackSettingsStore playbackSettings,
        FakePlaybackDialogService dialogs)
    {
        return new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(new MainChartListViewModel()),
            playbackSettings,
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer());
    }

    private static ChartFile CreatePlaybackTargetChart(ChartFileKind kind, string path)
    {
        return new ChartFile(
            kind,
            path,
            "playback-port-hash",
            null,
            "Title",
            "Title",
            "Artist",
            "Genre",
            "Folder",
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            kind == ChartFileKind.Bms ? new BMSFile { path = path } : null,
            null);
    }

    private static PlaybackPanelViewModel CreateTemporaryInstallPanel(
        string chartPath,
        string installDirectory,
        string songDbPath,
        IBMSPlayer player,
        IPlaybackDialogService dialogs)
    {
        return CreateTemporaryInstallPanel(
            chartPath,
            installDirectory,
            songDbPath,
            player,
            dialogs,
            out _);
    }

    private static PlaybackPanelViewModel CreateTemporaryInstallPanel(
        string chartPath,
        string installDirectory,
        string songDbPath,
        IBMSPlayer player,
        IPlaybackDialogService dialogs,
        out LibraryChartRow activationRow,
        ChartFileOperationSynchronizer? operations = null)
    {
        var bmsFile = new TestBmsFile(chartPath);
        ChartFile chart = ChartPackageTestExtensions
            .CreateEntryWithInstallDestination(bmsFile, installDirectory)
            .Chart;
        activationRow = LibraryChartRow.FromChartFile(chart);
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { activationRow },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => Settings.Default),
            dialogs,
            _ => { },
            operations ?? new ChartFileOperationSynchronizer());
        panel.AttachLibrary(new TestBmsLibrary(songDbPath));
        return panel;
    }

    private static void AssertPlaybackPanelFinalState(PlaybackPanelView view, bool compact)
    {
        var image = (Image)view.FindName("gridBMSPlayerImage");
        var blurEffect = (BlurEffect)image.Effect;
        var titlePanel = (DockPanel)view.FindName("gridBMSPlayerTitlePanel");
        var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
        var compactTitle = (TextBlock)view.FindName("gridBMSPlayerControlsTitle2");

        Assert.AreEqual(compact ? 110d : 286d, view.Height, 0.001d);
        Assert.AreEqual(compact ? 20d : 0d, blurEffect.Radius, 0.001d);
        Assert.AreEqual(compact ? new Thickness(0d, 0d, 0d, -40d) : new Thickness(0d), titlePanel.Margin);
        Assert.AreEqual(compact ? 1d : 0d, banner.Opacity, 0.001d);
        Assert.AreEqual(1d, compactTitle.Opacity, 0.001d);
    }

    private static void AssertPlaybackPanelHasNoAnimationClocks(PlaybackPanelView view)
    {
        var image = (Image)view.FindName("gridBMSPlayerImage");
        var blurEffect = (BlurEffect)image.Effect;
        var titlePanel = (DockPanel)view.FindName("gridBMSPlayerTitlePanel");
        var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
        var compactTitle = (TextBlock)view.FindName("gridBMSPlayerControlsTitle2");

        Assert.IsFalse(IsAnimated(view, FrameworkElement.HeightProperty), "Height must not have an AnimationClock.");
        Assert.IsFalse(IsAnimated(blurEffect, BlurEffect.RadiusProperty), "Blur radius must not have an AnimationClock.");
        Assert.IsFalse(IsAnimated(titlePanel, FrameworkElement.MarginProperty), "Large title margin must not have an AnimationClock.");
        Assert.IsFalse(IsAnimated(banner, UIElement.OpacityProperty), "Banner opacity must not have an AnimationClock.");
        Assert.IsFalse(IsAnimated(compactTitle, UIElement.OpacityProperty), "Compact title opacity must not have an AnimationClock.");
    }

    private static void AssertPlaybackPanelHasTransitionClocks(PlaybackPanelView view, bool compactTransition)
    {
        var image = (Image)view.FindName("gridBMSPlayerImage");
        var blurEffect = (BlurEffect)image.Effect;
        var titlePanel = (DockPanel)view.FindName("gridBMSPlayerTitlePanel");
        var banner = (Border)view.FindName("gridBMSPlayerControlsBanner");
        var compactTitle = (TextBlock)view.FindName("gridBMSPlayerControlsTitle2");

        Assert.IsTrue(IsAnimated(view, FrameworkElement.HeightProperty), "Height must enter the transition route.");
        Assert.IsTrue(IsAnimated(blurEffect, BlurEffect.RadiusProperty), "Blur radius must enter the transition route.");
        Assert.IsTrue(IsAnimated(titlePanel, FrameworkElement.MarginProperty), "Large title margin must enter the transition route.");
        Assert.IsTrue(IsAnimated(banner, UIElement.OpacityProperty), "Banner opacity must enter the transition route.");
        Assert.AreEqual(
            compactTransition,
            IsAnimated(compactTitle, UIElement.OpacityProperty),
            "Compact title fades only while entering compact mode.");
    }

    private static bool IsAnimated(DependencyObject target, DependencyProperty property) =>
        DependencyPropertyHelper.GetValueSource(target, property).IsAnimated;

    private static void FlushRenderQueue(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
    }

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class InMemoryPlaybackSettingsStore : IPlaybackSettingsStore
    {
        public PlayerPanelState PlayerPanelState { get; set; }

        public bool RepeatPlay { get; set; }

        public bool FolderSkipPlay { get; set; }

        public bool SinglePlay { get; set; }

        public bool UsesLr2Body { get; set; }

        public bool UsesUbMplay { get; set; }

        public bool UsesBmiIdxView { get; set; }

        public bool UseExternalPanelImage { get; set; }

        public string StagefilePath { get; set; } = string.Empty;

        public int PlayerVolume { get; set; }

        public bool UsesLr2Database { get; set; }
    }

    internal class FakeBmsPlayer : IBMSPlayer, IExternalWindowPlayer
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string ExePath { get; set; } = string.Empty;

        public IExternalPlayerWindowHost? WindowHost { get; private set; }

        public void AttachWindowHost(IExternalPlayerWindowHost windowHost)
        {
            WindowHost = windowHost;
        }

        private TimeSpan duration;

        public TimeSpan Duration
        {
            get
            {
                if (ThrowOnTelemetryRead)
                {
                    throw new InvalidOperationException("telemetry read failure");
                }
                return duration;
            }
            set => duration = value;
        }

        public bool ThrowOnTelemetryRead { get; set; }

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

        public Exception? PlayStartException { get; set; }

        public Task? PlayStartTask { get; set; }

        public Action? BeforePlayStart { get; set; }

        public Action? BeforeClose { get; set; }

        public Action<object, EventArgs>? ExitHandler { get; private set; }

        public ConcurrentQueue<string> Commands { get; } = new();

        private readonly ConcurrentDictionary<string, int> commandCounts = new(
            StringComparer.Ordinal);
        private readonly ConcurrentDictionary<
            (string Command, int Occurrence),
            TaskCompletionSource<object?>> commandSignals = new();

        internal Task WaitForCommandAsync(string command)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            int expectedOccurrence = commandCounts.TryGetValue(command, out int currentOccurrence)
                ? currentOccurrence + 1
                : 1;
            (string command, int expectedOccurrence) key = (command, expectedOccurrence);
            if (commandCounts.TryGetValue(command, out currentOccurrence)
                && currentOccurrence >= expectedOccurrence)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource<object?> signal = commandSignals.GetOrAdd(
                key,
                static _ => new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            if (commandCounts.TryGetValue(command, out currentOccurrence)
                && currentOccurrence >= expectedOccurrence)
            {
                signal.TrySetResult(null);
            }
            return signal.Task;
        }

        public void Raise(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void CloseProcess()
        {
            CloseProcessCount++;
            RecordCommand("Close");
            BeforeClose?.Invoke();
        }

        public Task PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null)
        {
            RecordCommand("PlayStart:" + bmsFilePath);
            ExitHandler = onExitEventHandler;
            BeforePlayStart?.Invoke();
            if (PlayStartException != null)
            {
                throw PlayStartException;
            }
            return PlayStartTask ?? Task.CompletedTask;
        }

        public void RestartPlayingBMSfile() => RecordCommand("Restart");

        public void PausePlayingBMSfileToggle() => RecordCommand("Pause");

        public void FastForwardPlayingBMSfileStart() => RecordCommand("FastForwardStart");

        public void FastForwardPlayingBMSfileEnd() => RecordCommand("FastForwardEnd");

        public void FastBackwardPlayingBMSfileStart() => RecordCommand("FastBackwardStart");

        public void FastBackwardPlayingBMSfileEnd() => RecordCommand("FastBackwardEnd");

        public void ShowInfo() => RecordCommand("ShowInfo");

        public void ShowEffect() => RecordCommand("ShowEffect");

        public void ChangePlayside() => RecordCommand("ChangePlayside");

        public void IncreaseHighSpeed() => RecordCommand("IncreaseHighSpeed");

        public void DecreaseHighSpeed() => RecordCommand("DecreaseHighSpeed");

        public void VolumeChanged() => RecordCommand("VolumeChanged");

        private void RecordCommand(string command)
        {
            Commands.Enqueue(command);
            int occurrence = commandCounts.AddOrUpdate(command, 1, static (_, count) => count + 1);
            if (commandSignals.TryGetValue((command, occurrence), out TaskCompletionSource<object?>? signal))
            {
                signal!.TrySetResult(null);
            }
        }
    }

    internal sealed class PreloadBmsPlayer : FakeBmsPlayer, INextSongPreloadPlayer
    {
        internal Task Ready { get; set; } = Task.CompletedTask;
        internal Task Completion { get; set; } = Task.CompletedTask;
        internal bool LastAllowPreload { get; private set; }
        internal ConcurrentQueue<NextSongPreloadInput> Preloads { get; } = new();
        internal ConcurrentQueue<string> Starts { get; } = new();
        internal Func<Task>? CloseCompletion { get; set; }
        internal Func<string, PlaybackStartOperation>? BeginOperation { get; set; }
        internal TaskCompletionSource<string> StartObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? NextSongPath { get; private set; }

        public PlaybackStartOperation BeginStart(string path, Action<object, EventArgs>? onExit, bool allowPreload,
            Action<Exception>? onPlaybackFailure = null)
        {
            _ = PlayStart(path, onExit);
            NextSongPath = null;
            LastAllowPreload = allowPreload;
            Starts.Enqueue(path);
            StartObserved.TrySetResult(path);
            return BeginOperation?.Invoke(path) ?? new PlaybackStartOperation(Ready, Completion);
        }

        public Task PrepareNextAsync(NextSongPreloadInput input, Action<Exception> onFailure)
        {
            NextSongPath = input.Path;
            Preloads.Enqueue(input);
            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            NextSongPath = null;
            CloseProcess();
            return CloseCompletion?.Invoke() ?? Task.CompletedTask;
        }
    }

    private sealed class UnusedPendingDeletionDialog : IPendingDeleteConfirmationDialogPort
    {
        public Task<UiInteractionResult<bool>> ShowAsync() => throw new NotSupportedException();
    }

    private sealed class PhysicalChartDeletionStore : ISelectedChartMutationStore
    {
        internal int Mutations { get; private set; }
        public IReadOnlyList<string> GetLibraryWholeFolderDeleteConfirmationPaths(BMSLibrary library, IReadOnlyList<LibraryChartRef> charts) => [];
        public LibraryChartRemovalOutcome RemoveLibraryCharts(BMSLibrary library, IReadOnlyList<LibraryChartRef> charts,
            IReadOnlyList<string> approvedWholeFolderDeletePaths)
        {
            Mutations++;
            foreach (LibraryChartRef chart in charts) { File.Delete(chart.Path); }
            foreach (string folder in approvedWholeFolderDeletePaths) { Directory.Delete(folder, recursive: true); }
            return new LibraryChartRemovalOutcome(charts.Select(chart => new LibraryChartRemovalTarget(chart.Path, LibraryChartRemovalState.Confirmed)), true, true);
        }
        public void RemovePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, bool sendToRecycleBin, bool deleteContainingPackageFoldersWhenNoBms) => throw new NotSupportedException();
        public LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(BMSLibrary library, IReadOnlyList<LibraryFileExtensionRenameBatch> batches) => throw new NotSupportedException();
        public void RenamePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension) => throw new NotSupportedException();
        public LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(BMSLibrary library, ChartLibraryMoveRequest request) => throw new NotSupportedException();
        public void SetBMSFilesEncoding(BMSLibrary library, IReadOnlyList<BMSFile> bmsFiles, string encoding) => throw new NotSupportedException();
    }

    private sealed class FakePlaybackDialogService : IPlaybackDialogService
    {
        internal bool TemporaryInstallConfirmationResult { get; set; } = true;

        internal Exception? TemporaryInstallConfirmationException { get; set; }

        internal int TemporaryInstallConfirmationCount { get; private set; }

        internal Exception? LastPlaybackFailure { get; private set; }

        internal int PlaybackFailureNotificationCount { get; private set; }

        private readonly TaskCompletionSource<object?> playbackFailureNotification =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WaitForPlaybackFailureAsync() => playbackFailureNotification.Task;

        internal Action? BeforePlaybackFailureNotification { get; set; }

        internal Exception? PlaybackFailureException { get; set; }

        public bool ConfirmTemporaryInstallPlayback()
        {
            TemporaryInstallConfirmationCount++;
            if (TemporaryInstallConfirmationException != null)
            {
                throw TemporaryInstallConfirmationException;
            }
            return TemporaryInstallConfirmationResult;
        }

        public void NotifyPlaybackFailure(Exception exception)
        {
            BeforePlaybackFailureNotification?.Invoke();
            LastPlaybackFailure = exception;
            PlaybackFailureNotificationCount++;
            playbackFailureNotification.TrySetResult(null);
            if (PlaybackFailureException != null)
            {
                throw PlaybackFailureException;
            }
        }
    }

    private sealed class ImmediatePlaybackUiDispatcher : IPlaybackUiDispatcher
    {
        public void Dispatch(Action action)
        {
            action();
        }

        public Task DispatchAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class QueuedPlaybackUiDispatcher : IPlaybackUiDispatcher
    {
        private readonly ConcurrentQueue<Action> actions = new();
        private readonly object pendingWaiterGate = new();
        private readonly List<(int MinimumCount, TaskCompletionSource<object?> Completion)> pendingWaiters = [];

        internal int PendingCount => actions.Count;

        internal Task WaitForPendingActionsAsync(int minimumCount)
        {
            if (minimumCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumCount));
            }
            if (PendingCount >= minimumCount)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pendingWaiterGate)
            {
                if (PendingCount >= minimumCount)
                {
                    completion.TrySetResult(null);
                }
                else
                {
                    pendingWaiters.Add((minimumCount, completion));
                }
            }
            return completion.Task;
        }

        public void Dispatch(Action action)
        {
            actions.Enqueue(action);
            CompletePendingWaiters();
        }

        public Task DispatchAsync(Action action)
        {
            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            actions.Enqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            CompletePendingWaiters();
            return completion.Task;
        }

        internal void RunAll()
        {
            while (actions.TryDequeue(out Action? action))
            {
                action!();
            }
        }

        private void CompletePendingWaiters()
        {
            List<TaskCompletionSource<object?>>? completed = null;
            lock (pendingWaiterGate)
            {
                int pendingCount = PendingCount;
                for (int index = pendingWaiters.Count - 1; index >= 0; index--)
                {
                    (int minimumCount, TaskCompletionSource<object?> completion) = pendingWaiters[index];
                    if (pendingCount < minimumCount)
                    {
                        continue;
                    }

                    completed ??= [];
                    completed.Add(completion);
                    pendingWaiters.RemoveAt(index);
                }
            }

            if (completed == null)
            {
                return;
            }

            foreach (TaskCompletionSource<object?> completion in completed)
            {
                completion.TrySetResult(null);
            }
        }
    }

    private sealed class TestBmsFile : BMSFile
    {
        internal TestBmsFile(string filePath)
        {
            path = filePath;
        }
    }
}
