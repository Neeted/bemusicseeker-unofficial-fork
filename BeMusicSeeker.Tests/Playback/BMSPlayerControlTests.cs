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
            completeOnTick: true,
            cleanupFailure: cleanupFailure);

        InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => player.Start());

        Assert.AreSame(cleanupFailure, actual);
        Assert.AreEqual(1, player.CleanupCount);
        Assert.AreEqual(PlayState.Stopped, player.PlayState);
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
        bool completeOnTick = false,
        ManualResetEventSlim? cleanupGate = null) : BMSPlayer<NullImageLoader>(bms)
    {
        private readonly TaskCompletionSource cleanupEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int tickCount;
        private int cleanupCount;

        internal Task CleanupEntered => cleanupEntered.Task;

        internal int TickCount => Volatile.Read(ref tickCount);

        internal int CleanupCount => Volatile.Read(ref cleanupCount);

        internal CancellationToken PlaybackCancellationToken =>
            taskTokenSource?.Token ?? throw new InvalidOperationException("Playback has not started.");

        public override void LoadResources()
        {
        }

        protected override void OnPlaybackStarting()
        {
            if (preparationFailure != null)
            {
                throw preparationFailure;
            }
        }

        protected override PlaybackTickResult OnPlaybackTick(TimeSpan wallClockTime)
        {
            Interlocked.Increment(ref tickCount);
            if (playbackFailure != null)
            {
                throw playbackFailure;
            }
            return new PlaybackTickResult(wallClockTime, completeOnTick);
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
