using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    [TestMethod]
    public async Task Start_PreservesPlaybackFailureWhenCleanupAlsoFails()
    {
        using var directory = new TemporaryDirectory();
        Exception playbackFailure = new InvalidOperationException("playback failed");
        Exception cleanupFailure = new InvalidOperationException("cleanup failed");
        using var player = new PlaybackProbe(
            new BMSFile(WriteChart(directory)),
            playbackFailure: playbackFailure,
            cleanupFailure: cleanupFailure);

        AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(() => player.Start());

        Assert.AreSame(playbackFailure, exception.InnerExceptions[0]);
        Assert.AreSame(cleanupFailure, exception.InnerExceptions[1]);
        Assert.AreEqual(1, player.CleanupCount);
        Assert.AreEqual(PlayState.Stopped, player.PlayState);
    }

    [TestMethod]
    public async Task Start_PropagatesCleanupFailureAfterNaturalCompletion()
    {
        using var directory = new TemporaryDirectory();
        Exception cleanupFailure = new InvalidOperationException("cleanup failed");
        using var player = new PlaybackProbe(
            new BMSFile(WriteChart(directory)),
            playbackDuration: TimeSpan.Zero,
            initialClockTime: TimeSpan.Zero,
            cleanupFailure: cleanupFailure);
        player.PlaybackRate = float.Epsilon;

        InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => player.Start());

        Assert.AreSame(cleanupFailure, actual);
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
        BMSFile bms,
        Exception? playbackFailure = null,
        Exception? preparationFailure = null,
        Exception? cleanupFailure = null,
        TimeSpan? playbackDuration = null,
        TimeSpan? initialClockTime = null,
        ManualResetEventSlim? cleanupGate = null) : BMSPlayer<NullImageLoader>(bms)
    {
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

        public override void LoadResources()
        {
        }

        protected override void OnPlaybackStarting()
        {
            Duration = playbackDuration ?? TimeSpan.FromHours(1);
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
