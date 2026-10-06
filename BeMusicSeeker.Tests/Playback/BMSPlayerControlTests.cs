using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BMSPlayerControlTests
{
    [DataTestMethod]
    [DataRow("normal-ln", new long[] { 4800000 })]
    [DataRow("ln-normal", new long[] { 4800000, 9600000 })]
    [DataRow("short-long", new long[] { 4800000, 9600000 })]
    [DataRow("long-short", new long[] { 4800000, 14400000 })]
    [DataRow("stable-channel-order", new long[] { 4800000, 4800000, 4800000, 9600000, 9600000, 14400000, 14400000, 14420000 })]
    [DataRow("later-channel-earlier-head", new long[] { 4800000, 9600000, 12000000, 14400000, 14400000, 14400000 })]
    public void BmsonPlayableConflictsPreserveRegistrationOrderForProgressAndBackwardSeek(string scenario, long[] expectedTicks)
    {
        PlaybackChart chart = BmsonPlaybackParserTests.Parse(BmsonPlaybackParserTests.ConflictingPlayableInput(scenario));
        using var player = new PlaybackProbe(chart);
        player.LoadResources(); // probeの表示長だけを設定し、音声資源とnative出力は使いません。
        Assert.AreEqual(expectedTicks.Length, player.Chart.TotalNoteCount);
        long[] boundaries = expectedTicks.Distinct().Order().ToArray();
        foreach (long boundary in boundaries.Concat(boundaries.Reverse()))
        {
            player.CurrentTime = TimeSpan.FromTicks(boundary - 1);
            Assert.AreEqual(expectedTicks.Count(tick => tick < boundary), player.Combo, "境界直前と戻りseekの進行");
            player.CurrentTime = TimeSpan.FromTicks(boundary);
            Assert.AreEqual(expectedTicks.Count(tick => tick <= boundary), player.Combo, "境界の進行");
        }
    }

    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public async Task BmsonInitialLinesDoNotAdvanceMeasureButQuantizedPositiveLinesDo(int initialLines, bool quantizedToZero)
    {
        string lines = "[" + string.Concat(Enumerable.Repeat("{\"y\":0},", initialLines)) + "{\"y\":240},{\"y\":480}]";
        PlaybackChart chart = BmsonPlaybackParserTests.Parse("{\"info\":{\"init_bpm\":" + (quantizedToZero ? "1e1000" : "120")
            + "},\"lines\":" + lines + ",\"sound_channels\":[]}");
        Assert.AreEqual(2, chart.LastMeasure);
        using var player = new PlaybackProbe(chart, initialClockTime: TimeSpan.Zero);
        player.PlaybackRate = float.Epsilon;
        Task<TimeSpan> initial = player.ArmNextTimeApplication();
        Task playback = player.Start();
        try
        {
            Assert.AreEqual(TimeSpan.Zero, await initial);
            Assert.AreEqual(quantizedToZero ? 2 : 0, player.CurrentMeasure);
            if (!quantizedToZero)
            {
                Assert.AreEqual(TimeSpan.FromSeconds(0.5), await player.SetClockAndArmNextTimeApplication(TimeSpan.FromSeconds(0.5)));
                Assert.AreEqual(1, player.CurrentMeasure);
                Assert.AreEqual(TimeSpan.FromSeconds(1), await player.SetClockAndArmNextTimeApplication(TimeSpan.FromSeconds(1)));
                Assert.AreEqual(2, player.CurrentMeasure);
            }
        }
        finally
        {
            player.Stop();
            try { await playback; } catch (OperationCanceledException) { }
        }
        using var seeker = new PlaybackProbe(chart);
        seeker.LoadResources();
        foreach ((double seconds, int measure) in new[] { (1d, 2), (0.5d, 1), (0d, 0), (1d, 2) })
        {
            seeker.CurrentTime = TimeSpan.FromSeconds(seconds);
            Assert.AreEqual(quantizedToZero ? 2 : measure, seeker.CurrentMeasure);
        }
    }
    [TestMethod]
    public async Task Start_PreservesPlaybackFailureWhenCleanupAlsoFails()
    {
        using var directory = new TemporaryDirectory();
        Exception playbackFailure = new InvalidOperationException("playback failed");
        Exception cleanupFailure = new InvalidOperationException("cleanup failed");
        var player = new PlaybackProbe(
            new BMSFile(WriteChart(directory)),
            playbackFailure: playbackFailure,
            cleanupFailure: cleanupFailure);

        try
        {
            AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(() => player.Start());

            Assert.AreSame(playbackFailure, exception.InnerExceptions[0]);
            Assert.AreSame(cleanupFailure, exception.InnerExceptions[1]);
            Assert.AreEqual(1, player.CleanupCount);
            Assert.AreEqual(PlayState.Stopped, player.PlayState);
        }
        finally
        {
            try { player.Dispose(); }
            catch (AggregateException failure) when (failure.InnerExceptions.Count == 2
                && ReferenceEquals(playbackFailure, failure.InnerExceptions[0])
                && ReferenceEquals(cleanupFailure, failure.InnerExceptions[1]))
            { }
        }
    }

    [TestMethod]
    public async Task Start_PropagatesCleanupFailureAfterNaturalCompletion()
    {
        using var directory = new TemporaryDirectory();
        Exception cleanupFailure = new InvalidOperationException("cleanup failed");
        var player = new PlaybackProbe(
            new BMSFile(WriteChart(directory)),
            playbackDuration: TimeSpan.Zero,
            initialClockTime: TimeSpan.Zero,
            cleanupFailure: cleanupFailure);
        try
        {
            player.PlaybackRate = float.Epsilon;

            InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => player.Start());

            Assert.AreSame(cleanupFailure, actual);
            Assert.AreEqual(1, player.CleanupCount);
            Assert.AreEqual(PlayState.Stopped, player.PlayState);
        }
        finally
        {
            try { player.Dispose(); }
            catch (InvalidOperationException failure) when (ReferenceEquals(cleanupFailure, failure)) { }
        }
    }

    [TestMethod]
    public async Task BmsonDisplayCountsLogicalPositionsAndNaturalCompletionWaitsForCleanup()
    {
        PlaybackChart chart = BmsonPlaybackParserTests.Parse("{\"info\":{\"init_bpm\":120},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0,\"l\":240},{\"x\":2,\"y\":120}]}],\"bpm_events\":[{\"y\":240,\"bpm\":60}],\"stop_events\":[{\"y\":240,\"duration\":240}]}");
        using var cleanupGate = new ManualResetEventSlim(initialState: false);
        using var player = new PlaybackProbe(chart, playbackDuration: chart.Duration, initialClockTime: chart.Duration,
            cleanupGate: cleanupGate);
        player.LoadResources();
        player.CurrentTime = TimeSpan.FromSeconds(0.25);
        Assert.AreEqual(2, player.Combo);
        player.CurrentTime = TimeSpan.FromSeconds(0.5);
        Assert.AreEqual(3, player.Combo);
        Assert.AreEqual(60d, player.CurrentBpm);
        Assert.AreEqual(TimeSpan.FromSeconds(1), player.StopTime);
        player.CurrentTime = TimeSpan.FromSeconds(0.25);
        Assert.AreEqual(2, player.Combo);
        player.PlaybackRate = float.Epsilon;
        Task playback = player.Start();
        try
        {
            await player.CleanupEntered;
            Assert.IsFalse(playback.IsCompleted, "Startは自然終了の後処理が完了する前に戻ってはいけません。");
            Assert.AreEqual(chart.Duration, player.CurrentTime);
            Assert.AreEqual(3, player.Combo);
        }
        finally
        {
            cleanupGate.Set();
            await playback;
        }

        Assert.AreEqual(chart.Duration, player.CurrentTime);
        Assert.AreEqual(3, player.Combo);
        Assert.AreEqual(1, player.CleanupCount);
        Assert.AreEqual(PlayState.Stopped, player.PlayState);
    }

    [TestMethod]
    public async Task Start_UsesDurationClockForNonFrameAlignedNaturalCompletion()
    {
        using var directory = new TemporaryDirectory();
        var duration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1);
        Assert.AreNotEqual(0L, duration.Ticks * 48000 % TimeSpan.TicksPerSecond,
            "The playback duration must fall between 48 kHz frame boundaries.");
        using var player = new PlaybackProbe(
            new BMSFile(WriteChart(directory)),
            playbackDuration: duration,
            initialClockTime: duration - TimeSpan.FromTicks(1));
        player.PlaybackRate = float.Epsilon;
        Task<TimeSpan> belowDuration = player.ArmNextTimeApplication();

        Task playback = player.Start();
        Assert.AreEqual(duration - TimeSpan.FromTicks(1), await belowDuration);
        Assert.IsFalse(playback.IsCompleted,
            "The playback clock below Duration must continue even when Duration is off the audio frame grid.");

        Task<TimeSpan> atDuration = player.SetClockAndArmNextTimeApplication(duration);
        Assert.AreEqual(duration, await atDuration);
        await playback;

        Assert.AreEqual(duration, player.CurrentTime);
        Assert.AreEqual(1, player.CleanupCount);
        Assert.AreEqual(PlayState.Stopped, player.PlayState);

        using var overshootPlayer = new PlaybackProbe(
            new BMSFile(WriteChart(directory)),
            playbackDuration: duration,
            initialClockTime: duration + TimeSpan.FromTicks(1));
        overshootPlayer.PlaybackRate = float.Epsilon;
        Task<TimeSpan> overshootApplication = overshootPlayer.ArmNextTimeApplication();

        Task overshootPlayback = overshootPlayer.Start();
        Assert.AreEqual(duration, await overshootApplication,
            "A clock beyond Duration must be displayed at Duration.");
        await overshootPlayback;

        Assert.AreEqual(duration, overshootPlayer.CurrentTime);
        Assert.AreEqual(1, overshootPlayer.CleanupCount);
        Assert.AreEqual(PlayState.Stopped, overshootPlayer.PlayState);
    }

    [TestMethod]
    public async Task Start_CleansUpOnceWhenPreparationFails()
    {
        using var directory = new TemporaryDirectory();
        Exception preparationFailure = new InvalidOperationException("preparation failed");
        using var player = new PlaybackProbe(
            new BMSFile(WriteChart(directory)),
            preparationFailure: preparationFailure);

        InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => player.Start());

        Assert.AreSame(preparationFailure, actual);
        Assert.AreEqual(0, player.TickCount);
        Assert.AreEqual(1, player.CleanupCount);
        Assert.AreEqual(PlayState.Stopped, player.PlayState);
    }

    [TestMethod]
    public async Task Stop_WaitsForCleanupBeforeResettingPlaybackState()
    {
        using var directory = new TemporaryDirectory();
        using var cleanupGate = new ManualResetEventSlim(initialState: false);
        using var player = new PlaybackProbe(
            new BMSFile(WriteChart(directory)),
            cleanupGate: cleanupGate);

        Task playback = player.Start();
        var stop = Task.Run(player.Stop);

        try
        {
            await player.CleanupEntered;
            Assert.IsFalse(stop.IsCompleted, "Stopは再生後処理の完了前に戻ってはいけません。");
        }
        finally
        {
            cleanupGate.Set();
        }

        await stop;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => playback);
        Assert.AreEqual(1, player.CleanupCount);
        Assert.AreEqual(PlayState.Stopped, player.PlayState);
        Assert.AreEqual(TimeSpan.Zero, player.CurrentTime);
    }

    [TestMethod]
    public async Task Stop_CancelsBeforeWorkerStartsAndJoinsItsCleanup()
    {
        using var directory = new TemporaryDirectory();
        using var scheduler = new QueuedTaskScheduler();
        using var player = new PlaybackProbe(new BMSFile(WriteChart(directory)));
        Task<Task> startDispatch = Task.Factory.StartNew(
            player.Start,
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler);
        scheduler.ExecuteNext();
        Task playback = await startDispatch;
        var cancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = player.PlaybackCancellationToken.Register(
            () => cancellationRequested.TrySetResult());

        var stop = Task.Run(player.Stop);
        await cancellationRequested.Task;
        Assert.IsFalse(stop.IsCompleted, "Stopは未開始の再生Taskをcleanup完了まで待つ必要があります。");
        Assert.AreEqual(1, scheduler.PendingCount);

        var worker = Task.Run(scheduler.ExecuteNext);
        await worker;
        await stop;
        scheduler.ExecuteAll();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => playback);

        Assert.AreEqual(0, player.TickCount);
        Assert.AreEqual(1, player.CleanupCount);
        Assert.AreEqual(PlayState.Stopped, player.PlayState);
    }

    private static string WriteChart(TemporaryDirectory directory)
    {
        string path = directory.File("control-chart.bms");
        File.WriteAllText(
            path,
            "#PLAYER 1\n#TITLE control boundary\n#BPM 120\n#00011:00\n",
            Encoding.ASCII);
        return path;
    }

    private sealed class PlaybackProbe(
        PlaybackChart bms,
        Exception? playbackFailure = null,
        Exception? preparationFailure = null,
        Exception? cleanupFailure = null,
        TimeSpan? playbackDuration = null,
        TimeSpan? initialClockTime = null,
        ManualResetEventSlim? cleanupGate = null) : BMSPlayer<NullImageLoader>(bms)
    {
        internal PlaybackProbe(BMSFile bms, Exception? playbackFailure = null, Exception? preparationFailure = null,
            Exception? cleanupFailure = null, TimeSpan? playbackDuration = null, TimeSpan? initialClockTime = null,
            ManualResetEventSlim? cleanupGate = null)
            : this(PlaybackChart.FromBms(bms), playbackFailure, preparationFailure, cleanupFailure, playbackDuration, initialClockTime, cleanupGate) { }

        private readonly TaskCompletionSource cleanupEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<TimeSpan>? nextTimeApplication;
        private int tickCount;
        private int cleanupCount;

        internal Task CleanupEntered => cleanupEntered.Task;

        internal int TickCount => Volatile.Read(ref tickCount);

        internal int CleanupCount => Volatile.Read(ref cleanupCount);

        internal CancellationToken PlaybackCancellationToken =>
            taskTokenSource?.Token ?? throw new InvalidOperationException("Playback has not started.");

        internal Task<TimeSpan> ArmNextTimeApplication()
        {
            lock (playbackControlSync)
            {
                return CreateNextTimeApplication();
            }
        }

        internal Task<TimeSpan> SetClockAndArmNextTimeApplication(TimeSpan time)
        {
            lock (playbackControlSync)
            {
                Task<TimeSpan> application = CreateNextTimeApplication();
                timerOffset = time;
                timer.Reset();
                return application;
            }
        }

        private Task<TimeSpan> CreateNextTimeApplication()
        {
            var source = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<TimeSpan> task = source.Task;
            _ = Interlocked.Exchange(ref nextTimeApplication, source);
            return task;
        }

        /// <summary>音源を使わないテスト用の再生長を準備します。</summary>
        public override void LoadResources()
        {
            Duration = playbackDuration ?? TimeSpan.FromHours(1);
        }

        protected override void OnPlaybackStarting()
        {
            LoadResources();
            if (initialClockTime is TimeSpan time)
            {
                timerOffset = time;
                timer.Reset();
            }
            if (preparationFailure != null)
            {
                throw preparationFailure;
            }
        }

        protected override void OnPlaybackTick(TimeSpan playbackTime)
        {
            Interlocked.Increment(ref tickCount);
            if (playbackFailure != null)
            {
                throw playbackFailure;
            }
        }

        protected override void ForwardTo(TimeSpan time)
        {
            base.ForwardTo(time);
            Interlocked.Exchange(ref nextTimeApplication, null)?.TrySetResult(CurrentTime);
        }

        protected override void OnPlaybackStopping()
        {
            Interlocked.Increment(ref cleanupCount);
            cleanupEntered.TrySetResult();
            cleanupGate?.Wait();
            if (cleanupFailure != null)
            {
                throw cleanupFailure;
            }
        }
    }

    private sealed class QueuedTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly ConcurrentQueue<Task> tasks = new();

        internal int PendingCount => tasks.Count;

        protected override IEnumerable<Task>? GetScheduledTasks() => tasks.ToArray();

        protected override void QueueTask(Task task) => tasks.Enqueue(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        internal void ExecuteNext()
        {
            if (!tasks.TryDequeue(out Task? task))
            {
                throw new InvalidOperationException("再生Taskが制御schedulerへ登録されていません。");
            }
            _ = TryExecuteTask(task);
        }

        internal void ExecuteAll()
        {
            while (tasks.TryDequeue(out Task? task))
            {
                _ = TryExecuteTask(task);
            }
        }

        public void Dispose()
        {
            ExecuteAll();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker.BMSPlayerControl." + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory()
        {
            Directory.CreateDirectory(path);
        }

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose() => Directory.Delete(path, recursive: true);
    }
}
