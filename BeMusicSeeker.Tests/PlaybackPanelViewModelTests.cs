using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlaybackPanelViewModelTests
{
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
        Assert.IsTrue(panel.TryPlayStart(playbackGeneration, "chart.bms", null));
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
    public void PlaybackPanel_ReplacementClosesOldPlayerDetachesEventsAndKeepsHostHandle()
    {
        var first = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var second = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(20) };
        PlaybackPanelViewModel panel = CreatePanel(first);
        panel.AttachParentHandle(new IntPtr(42));

        panel.ReplacePlayer(second);

        Assert.AreEqual(1, first.CloseProcessCount);
        Assert.AreEqual(new IntPtr(42), second.ParentHandle);
        Assert.AreEqual(second.Duration, panel.CurrentlyPlayingDuration);

        first.Duration = TimeSpan.FromSeconds(99);
        first.Raise(nameof(IBMSPlayer.Duration));
        Assert.AreEqual(second.Duration, panel.CurrentlyPlayingDuration);

        panel.CloseProcess();
        Assert.AreEqual(1, second.CloseProcessCount);
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
        Assert.IsTrue(panel.TryPlayStart(firstGeneration, "first.bms", (_, _) => exitCount++));
        Action<object, EventArgs> firstExit = player.ExitHandler!;
        panel.NotifyPlaybackStarted(firstGeneration);

        Assert.AreSame(first, panel.NowPlayingBmsFile);
        Assert.AreEqual(3, panel.NowPlayingRowIndex);
        Assert.IsTrue(panel.IsPlaying);
        Assert.AreEqual(1, startingCount);
        Assert.AreEqual(1, startedCount);

        long secondGeneration = panel.BeginPlayback(second, 4);
        Assert.IsTrue(panel.TryPlayStart(secondGeneration, "second.bms", (_, _) => exitCount++));
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
    public void PlaybackPanel_StopReplaceAndCloseInvalidatePreparedOrRunningSessions()
    {
        var firstPlayer = new FakeBmsPlayer();
        var replacementPlayer = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(firstPlayer);

        long stoppedGeneration = panel.BeginPlayback(new BMSFile(), 1);
        panel.StopPlayback();
        Assert.IsFalse(panel.TryPlayStart(stoppedGeneration, "stopped.bms", null));
        Assert.IsFalse(firstPlayer.Commands.Any(command => command == "PlayStart:stopped.bms"));

        long replacedGeneration = panel.BeginPlayback(new BMSFile(), 2);
        panel.ReplacePlayer(replacementPlayer);
        Assert.IsFalse(panel.TryPlayStart(replacedGeneration, "replaced.bms", null));
        Assert.IsFalse(replacementPlayer.Commands.Any(command => command == "PlayStart:replaced.bms"));

        long runningGeneration = panel.BeginPlayback(new BMSFile(), 3);
        int exitCount = 0;
        Assert.IsTrue(panel.TryPlayStart(runningGeneration, "running.bms", (_, _) => exitCount++));
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
            () => Dispatcher.CurrentDispatcher,
            chartList,
            SettingsEditSession.CreateDefault(),
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
        var panel = new PlaybackPanelViewModel(
            player,
            () => Dispatcher.CurrentDispatcher,
            chartList,
            SettingsEditSession.CreateDefault(),
            new ChartFileOperationSynchronizer());
        try
        {
            Settings.Default.RepeatPlayMode = false;
            Settings.Default.FolderSkipPlayMode = false;

            panel.Start();

            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.AreEqual(-1, panel.NowPlayingRowIndex);
            Assert.AreEqual(4096, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
        }
        finally
        {
            Settings.Default.RepeatPlayMode = originalRepeat;
            Settings.Default.FolderSkipPlayMode = originalFolderSkip;
            File.Delete(chartPath);
        }
    }

    private static PlaybackPanelViewModel CreatePanel(IBMSPlayer player)
    {
        return new PlaybackPanelViewModel(
            player,
            () => Dispatcher.CurrentDispatcher,
            new MainChartListViewModel(),
            SettingsEditSession.CreateDefault(),
            new ChartFileOperationSynchronizer());
    }

    private sealed class FakeBmsPlayer : IBMSPlayer
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string ExePath { get; set; } = string.Empty;

        public IntPtr ParentHandle { get; set; }

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

        public Exception? PlayStartException { get; set; }

        public Action<object, EventArgs>? ExitHandler { get; private set; }

        public System.Collections.Generic.List<string> Commands { get; } = [];

        public void Raise(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void CloseProcess()
        {
            CloseProcessCount++;
            Commands.Add("Close");
        }

        public void PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null)
        {
            Commands.Add("PlayStart:" + bmsFilePath);
            ExitHandler = onExitEventHandler;
            if (PlayStartException != null)
            {
                throw PlayStartException;
            }
        }

        public void RestartPlayingBMSfile() => Commands.Add("Restart");

        public void PausePlayingBMSfileToggle() => Commands.Add("Pause");

        public void FastForwardPlayingBMSfileStart() => Commands.Add("FastForwardStart");

        public void FastForwardPlayingBMSfileEnd() => Commands.Add("FastForwardEnd");

        public void FastBackwardPlayingBMSfileStart() => Commands.Add("FastBackwardStart");

        public void FastBackwardPlayingBMSfileEnd() => Commands.Add("FastBackwardEnd");

        public void ShowInfo() => Commands.Add("ShowInfo");

        public void ShowEffect() => Commands.Add("ShowEffect");

        public void ChangePlayside() => Commands.Add("ChangePlayside");

        public void IncreaseHighSpeed() => Commands.Add("IncreaseHighSpeed");

        public void DecreaseHighSpeed() => Commands.Add("DecreaseHighSpeed");

        public void VolumeChanged() => Commands.Add("VolumeChanged");
    }

    private sealed class TestBmsFile : BMSFile
    {
        internal TestBmsFile(string filePath)
        {
            path = filePath;
        }
    }
}
