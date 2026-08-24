using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
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
        panel.StopPlayback();

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
        panel.CloseProcess();
        Assert.AreEqual(TimeSpan.FromSeconds(10), panel.CurrentlyPlayingDuration);
        dispatcher.RunAll();
        Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration);

        first.Duration = TimeSpan.FromSeconds(30);
        panel.CloseProcess();
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
        panel.StopPlayback(closeProcess: true);

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
        Task stop = Task.Run(() => panel.StopPlayback(closeProcess: true));
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

        panel.CloseProcess();
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

        ((IAudioDeviceTestPlaybackPort)panel).StopPlayback();

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

        panel.StopPlayback();
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

        panel.Start(forceNewPlay: false);

        Assert.IsFalse(panel.IsPlaying);
        Assert.IsTrue(panel.IsPaused);
        Assert.IsTrue(panel.IsStoppedOrPaused);
        CollectionAssert.IsSubsetOf(
            new[] { nameof(panel.IsPlaying), nameof(panel.IsPaused), nameof(panel.IsStoppedOrPaused) },
            changed);
        changed.Clear();

        panel.Start(forceNewPlay: false);

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
            panel.Start();
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
        panel.StopPlayback();
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
        panel.CloseProcess();
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

            panel.Start();

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

            panel.Start();

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
            panel.Start();

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
            panel.Start();

            Assert.IsNotNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);

            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            completion.SetException(failure);

            await failureNotification;
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.AreEqual(1, player.CloseProcessCount);
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
                panel.Start();
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
            panel.Start();

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

        panel.StopPlayback();
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

        panel.StopPlayback();
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
        panel.StopPlayback();
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
            panel.Start();
            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            completion.SetException(new IOException("recoverable player start failed"));
            await failureNotification;

            player.PlayStartTask = Task.CompletedTask;
            panel.Start();

            Assert.IsNotNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(2, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    [TestMethod]
    public void PlaybackPanel_DoesNotHideNotificationFailureOrRunFollowingStop()
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
            Assert.AreSame(notificationFailure, Assert.ThrowsException<InvalidOperationException>(() => panel.Start()));
            Assert.AreSame(playerFailure, dialogs.LastPlaybackFailure);
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.AreEqual(1, player.CloseProcessCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
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
            panel.Start();
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
            panel.Start();
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
            panel.Start();
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
    public async Task PlaybackPanel_TemporaryInstallAsyncStartFailureAndCancellationTearDown()
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

            panel.Start();
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
            Task closeObserved = player.WaitForCommandAsync("Close");
            try
            {
                panel.Start();
                secondCompletion.SetCanceled();
                await Task.WhenAll(stopped.Task, closeObserved);
            }
            finally
            {
                panel.PropertyChanged -= stoppedHandler;
            }
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreEqual(2, player.CloseProcessCount);
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

            acceptedPanel.Start();

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

            rejectedPanel.Start();

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

            Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() => panel.Start()));
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
    public void MainChartListPlaybackQueue_ForwardsLiveRowsAndSelection()
    {
        var firstRow = new object();
        var secondRow = new object();
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

            panel.Next();
            Assert.AreEqual(thirdPath, panel.NowPlayingBmsFile.path);
            Assert.AreEqual(2, panel.NowPlayingRowIndex);
            Assert.AreEqual(2, chartList.SelectedIndex);

            panel.Previous();
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
            panel.ExecuteTableRowActivation(0, expectedRow);

            Assert.IsFalse(player.Commands.Any(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.IsNull(panel.NowPlayingBmsFile);

            chartList.Rows[0] = expectedRow;
            panel.ExecuteTableRowActivation(0, expectedRow);

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
    public void PlaybackPanel_StopsWhenMutatedDirectoryContainsPlayingChart()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaybackContainment", Guid.NewGuid().ToString("N"));
        string parent = Path.Combine(root, "Parent");
        string chartPath = Path.Combine(parent, "Child", "chart.bms");
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(player);
        panel.BeginPlayback(new TestBmsFile(chartPath), 0);

        panel.StopIfPlayingChartDirectories(new[] { parent });

        Assert.IsNull(panel.NowPlayingBmsFile);
        Assert.AreEqual(1, player.CloseProcessCount);
    }

    [TestMethod]
    public void PlaybackPanel_ImplementsMutationPlaybackPortsWithBmsFiltering()
    {
        string chartPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaybackPorts", "chart.bms");
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(player);

        panel.BeginPlayback(new TestBmsFile(chartPath), 0);
        ((IDuplicateMaintenancePlaybackPort)panel).StopPlaybackForCharts(
            [CreatePlaybackTargetChart(ChartFileKind.Bmson, chartPath)]);
        Assert.AreEqual(0, player.CloseProcessCount);

        ((IDuplicateMaintenancePlaybackPort)panel).StopPlaybackForCharts(
            [CreatePlaybackTargetChart(ChartFileKind.Bms, chartPath)]);
        Assert.AreEqual(1, player.CloseProcessCount);

        panel.BeginPlayback(new TestBmsFile(chartPath), 0);
        ((ISelectedChartAudioConversionPlaybackPort)panel).StopPlayback();
        Assert.AreEqual(2, player.CloseProcessCount);
    }

    [TestMethod]
    public void PlaybackPanel_OwnsPanelStateTransitionsUsingViewHostAvailability()
    {
        PlayerPanelState originalPanelState = Settings.Default.PlayerPanelState;
        PlaybackPanelViewModel panel = CreatePanel(new FakeBmsPlayer());
        try
        {
            panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;

            Assert.IsTrue(panel.CanSelectPanelState(PlayerPanelState.TITLE_LARGE, false, false));
            Assert.IsFalse(panel.CanSelectPanelState(PlayerPanelState.BMS_PLAYER, false, true));
            Assert.IsFalse(panel.CanSelectPanelState(PlayerPanelState.MOVIE_PLAYER, true, false));
            Assert.IsFalse(panel.TrySelectPanelState(PlayerPanelState.BMS_PLAYER, false, true));
            Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panel.PlayerPanelState);

            panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
            panel.RotatePanelState(bmsPlayerSurfaceAvailable: true, moviePlayerSurfaceAvailable: false);
            Assert.AreEqual(PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);

            panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
            panel.RotatePanelState(bmsPlayerSurfaceAvailable: true, moviePlayerSurfaceAvailable: false);
            Assert.AreEqual(PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);

            panel.RotatePanelState(bmsPlayerSurfaceAvailable: false, moviePlayerSurfaceAvailable: true);
            Assert.AreEqual(PlayerPanelState.MOVIE_PLAYER, panel.PlayerPanelState);

            panel.RotatePanelState(bmsPlayerSurfaceAvailable: false, moviePlayerSurfaceAvailable: false);
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
    public void PlaybackPanelViewResolvesUnavailableMovieSurfaceWithoutChangingRequestedState()
    {
        Assert.AreEqual(
            PlayerPanelState.BMS_PLAYER,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.MOVIE_PLAYER, true, false));
        Assert.AreEqual(
            PlayerPanelState.TITLE_LARGE,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.MOVIE_PLAYER, false, false));
        Assert.AreEqual(
            PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.TITLE_SMALL | PlayerPanelState.MOVIE_PLAYER, true, false));
        Assert.AreEqual(
            PlayerPanelState.MOVIE_PLAYER,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.MOVIE_PLAYER, true, true));
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
        bool originalExternalBrowser = Settings.Default.UseExternalWebBrowser;
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
            Settings.Default.UseExternalWebBrowser = !originalExternalBrowser;
            Settings.Default.UseExternalPanelImage = !originalExternalPanelImage;
            Settings.Default.StagefilePath = "playback-panel-stage.png";
            panel.NotifySettingsChanged();

            Assert.IsFalse(panel.CanSeek);
            Assert.IsFalse(panel.CanChangeHighSpeed);
            Assert.IsFalse(panel.CanShowInfo);
            Assert.IsFalse(panel.CanShowEffect);
            Assert.IsFalse(panel.CanChangePlayside);
            Assert.AreEqual(!originalExternalBrowser, panel.UseExternalWebBrowser);
            Assert.AreEqual(!originalExternalPanelImage, panel.UseExternalPanelImage);
            Assert.AreEqual("playback-panel-stage.png", panel.StagefilePath);
            CollectionAssert.IsSubsetOf(
                new[] { "PlayerPanelState", "CanSeek", "CanChangeHighSpeed", "CanShowInfo", "CanShowEffect", "CanChangePlayside", "UseExternalWebBrowser", "UseExternalPanelImage", "StagefilePath" },
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
            Settings.Default.UseExternalWebBrowser = originalExternalBrowser;
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
        var bmsFile = new TestBmsFile(chartPath);
        ChartFile chart = ChartPackageTestExtensions
            .CreateEntryWithInstallDestination(bmsFile, installDirectory)
            .Chart;
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { LibraryChartRow.FromChartFile(chart) },
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

        public bool UseExternalWebBrowser { get; set; }

        public bool UseExternalPanelImage { get; set; }

        public string StagefilePath { get; set; } = string.Empty;

        public int PlayerVolume { get; set; }

        public bool UsesLr2Database { get; set; }
    }

    private sealed class FakeBmsPlayer : IBMSPlayer, IExternalWindowPlayer
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
            var key = (command, expectedOccurrence);
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
            if (commandSignals.TryGetValue((command, occurrence), out TaskCompletionSource<object?> signal))
            {
                signal.TrySetResult(null);
            }
        }
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
            while (actions.TryDequeue(out Action action))
            {
                action();
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
