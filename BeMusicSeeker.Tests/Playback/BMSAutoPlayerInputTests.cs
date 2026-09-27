using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BMSAutoPlayerInputTests
{
    [TestInitialize]
    public void InitializeAudioRuntime()
    {
        BassAudioPlayer.Free();
        BassAudioRuntime.Shutdown();
        BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _);
    }

    [TestCleanup]
    public void ShutdownAudioRuntime()
    {
        BassAudioPlayer.Free();
        BassAudioRuntime.Shutdown();
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LoadResources_LoadsUsedAudioOnlyAndKeepsMissingFilesOptional(bool asParallel)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("unused-broken.wav"), [0x01, 0x02, 0x03]);
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#WAV01 unused-broken.wav\n"
            + "#WAV02 used.wav\n"
            + "#WAV03 missing.wav\n"
            + "#00111:0203\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));

        try
        {
            player.LoadResources(asParallel);

            Assert.IsNull(player.GetAudioPlayer(1), "An unused malformed definition must not be decoded.");
            Assert.IsNotNull(player.GetAudioPlayer(2), "The used audio file must be loaded.");
            Assert.IsNull(player.GetAudioPlayer(3), "A missing used file keeps its legacy optional behavior.");
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LoadResources_ReportsFailureForAnExistingUsedAudioFile(bool asParallel)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("broken.wav"), [0x01, 0x02, 0x03]);
        WriteChart(directory.File("chart.bms"),
            "#WAV02 broken.wav\n"
            + "#00111:02\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));

        try
        {
            InvalidDataException failure = Assert.ThrowsException<InvalidDataException>(
                () => player.LoadResources(asParallel));

            StringAssert.Contains(failure.Message, "broken.wav");
            StringAssert.Contains(failure.Message, nameof(AudioSourceLoadStage.DecodeWithBass));
            Assert.IsInstanceOfType<AudioSourceLoadException>(failure.InnerException);
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    [TestMethod]
    public async Task DisposeBeforeNextSong_StopsAndJoinsBeforeReplacingPublishedAudioPlayers()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#BPM 400\n#WAV02 used.wav\n#10011:02\n");
        var player = new StopObservingBMSAutoPlayer(
            new BMSFile(directory.File("chart.bms")),
            watchedIndex: 2);
        player.LoadResources(asParallel: false);

        Task play = player.Start();
        var failures = new List<Exception>();
        try
        {
            player.DisposeBeforeNextSong();
            Assert.IsTrue(player.SawPlayerBeforeStop, "Stop must see the old player's published audio array.");
            Assert.IsTrue(player.SawPlayerAfterStop, "The array must remain published through Stop's task join and reset.");
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await CaptureTaskCompletionAsync(play, failures, exception => exception is OperationCanceledException);
        CaptureCleanup(failures, directory.Dispose);
        ThrowFailures(failures);
    }

    [TestMethod]
    public void DisposeBeforeNextSong_UnconfirmedBassReleaseReturnsSourceReleaseAndKeepsSessionOwner()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#WAV02 used.wav\n#00111:02\n");
        var player = new DisposeFailureBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        player.LoadResources(asParallel: false);
        DisposeFailureAudioPlayer source = player.GetAudioPlayer(2)
            ?? throw new AssertFailedException("The BMS audio source was not created.");
        BassAudioSession session = BassAudioPlayer.ActiveSession
            ?? throw new AssertFailedException("The test audio session was not initialized.");
        BassAudioOwnedStream sourceStream = session.GetPlayerStreams().Single(stream =>
            stream.TryGetOwner<BassAudioPlayer>(out BassAudioPlayer owner)
            && ReferenceEquals(source, owner));
        int sourceHandle = sourceStream.Handle;

        try
        {
            BassAudioPlaybackException failure = Assert.ThrowsException<BassAudioPlaybackException>(
                player.DisposeBeforeNextSong);

            Assert.AreEqual(BassAudioPlaybackStage.SourceRelease, failure.Stage);
            Assert.AreEqual(sourceHandle, failure.SourceHandle);
            Assert.AreSame(session, failure.Session);
            Assert.IsFalse(source.NativeReleaseConfirmed);
            BassAudioOwnedStream owned = session.GetPlayerStreams().Single(stream => stream.Handle == sourceHandle);
            Assert.IsTrue(owned.TryGetOwner(out BassAudioPlayer owner));
            Assert.AreSame(source, owner);

            source.FailNextStreamFree = false;
            source.Dispose();
            Assert.IsTrue(source.NativeReleaseConfirmed);
            Assert.IsFalse(session.GetPlayerStreams().Any(stream => stream.Handle == sourceHandle));
        }
        finally
        {
            source.FailNextStreamFree = false;
            source.Dispose();
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LoadResources_DeduplicatesAliasesWithinOneLoadAndDecodesAgainForNextLoad(bool asParallel)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("audio.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#WAV02 audio.wav\n"
            + "#WAV03 ./AUDIO.WAV\n"
            + "#00111:0203\n");
        var first = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        var second = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        int readCount = 0;
        int decodeCount = 0;
        int createdCount = 0;
        var observer = new AudioSourceLoadPipelineObserver
        {
            InputReadCompleted = _ => Interlocked.Increment(ref readCount),
            DecodeCompleted = _ => Interlocked.Increment(ref decodeCount),
            PlayerCreated = (_, _) => Interlocked.Increment(ref createdCount)
        };

        try
        {
            first.LoadResources(asParallel, observer);

            Assert.AreEqual(1, readCount);
            Assert.AreEqual(1, decodeCount);
            Assert.AreEqual(2, createdCount);
            Assert.IsNotNull(first.GetAudioPlayer(2));
            Assert.IsNotNull(first.GetAudioPlayer(3));
            Assert.AreNotSame(first.GetAudioPlayer(2), first.GetAudioPlayer(3));

            second.LoadResources(asParallel, observer);

            Assert.AreEqual(2, readCount, "A later BMS load must read the file again.");
            Assert.AreEqual(2, decodeCount, "A later BMS load must decode the file again.");
            Assert.AreEqual(4, createdCount);
        }
        finally
        {
            first.DisposeLoadedAudio();
            second.DisposeLoadedAudio();
        }
    }

    [TestMethod]
    public async Task LoadResources_ParallelPipelineBoundsReadersDecodersAndQueuedInputs()
    {
        var directory = new TemporaryDirectory();
        int decoderLimit = Math.Max(1, Environment.ProcessorCount - 1);
        int inputCount = decoderLimit + 6;
        string chartPath = WriteManyAudioChart(directory, inputCount);
        var player = new TestBMSAutoPlayer(new BMSFile(chartPath));
        var initialReadersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerGate = new ManualResetEventSlim();
        var decodersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allInputReadsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoderGate = new ManualResetEventSlim();
        int firstReaders = 0;
        int completedReads = 0;
        int decoderStarts = 0;
        int activeReaders = 0;
        int maxReaders = 0;
        int inputReads = 0;
        int activeDecoders = 0;
        int maxDecoders = 0;
        int maxQueueDepth = 0;
        var observer = new AudioSourceLoadPipelineObserver
        {
            InputReadCompleted = _ =>
            {
                int current = Interlocked.Increment(ref activeReaders);
                UpdateMaximum(ref maxReaders, current);
                Interlocked.Increment(ref inputReads);
                if (Interlocked.Increment(ref completedReads) == inputCount)
                {
                    allInputReadsReady.TrySetResult();
                }
                int readerNumber = Interlocked.Increment(ref firstReaders);
                if (readerNumber <= 2)
                {
                    if (readerNumber == 2)
                    {
                        initialReadersReady.TrySetResult();
                    }
                    readerGate.Wait();
                }
                Interlocked.Decrement(ref activeReaders);
            },
            QueueDepthChanged = depth => UpdateMaximum(ref maxQueueDepth, depth),
            DecodeStarted = _ =>
            {
                int current = Interlocked.Increment(ref activeDecoders);
                UpdateMaximum(ref maxDecoders, current);
                int decoderNumber = Interlocked.Increment(ref decoderStarts);
                if (decoderNumber <= decoderLimit)
                {
                    if (decoderNumber == decoderLimit)
                    {
                        decodersReady.TrySetResult();
                    }
                }
                decoderGate.Wait();
            },
            DecodeCompleted = _ =>
            {
                Interlocked.Decrement(ref activeDecoders);
            }
        };
        var load = Task.Run(() => player.LoadResources(asParallel: true, observer));

        var failures = new List<Exception>();
        try
        {
            await WaitForSignalOrProducerAsync(initialReadersReady.Task, load, "Both bounded readers did not overlap.");
            Assert.AreEqual(2, Volatile.Read(ref maxReaders));
            readerGate.Set();
            await WaitForSignalOrProducerAsync(decodersReady.Task, load, "The configured decoder workers did not overlap.");
            await WaitForSignalOrProducerAsync(
                allInputReadsReady.Task,
                load,
                "Readers did not fill the bounded queue and reach their next reads.");
            Assert.AreEqual(decoderLimit, Volatile.Read(ref maxDecoders));
            Assert.AreEqual(4, Volatile.Read(ref maxQueueDepth));
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureCleanup(failures, readerGate.Set);
        CaptureCleanup(failures, decoderGate.Set);
        await CaptureTaskCompletionAsync(load, failures);
        if (failures.Count == 0)
        {
            CaptureCleanup(failures, () => Assert.AreEqual(inputCount, Volatile.Read(ref completedReads)));
        }
        CaptureCleanup(failures, player.DisposeLoadedAudio);
        CaptureCleanup(failures, readerGate.Dispose);
        CaptureCleanup(failures, decoderGate.Dispose);
        CaptureCleanup(failures, directory.Dispose);
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task LoadResources_ConsumerWorkerFailureCancelsBlockedReadersAndReclaimsQueuedInputs()
    {
        var directory = new TemporaryDirectory();
        int decoderLimit = Math.Max(1, Environment.ProcessorCount - 1);
        int inputCount = decoderLimit + 6;
        var player = new TestBMSAutoPlayer(new BMSFile(WriteManyAudioChart(directory, inputCount)));
        var readersFilledQueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoderWorkersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorkers = new ManualResetEventSlim();
        int queueDepth = 0;
        int inputReadCount = 0;
        int decoderWorkerCount = 0;
        int shouldFail = 0;
        var observer = new AudioSourceLoadPipelineObserver
        {
            InputReadCompleted = _ =>
            {
                if (Interlocked.Increment(ref inputReadCount) == 6)
                {
                    readersFilledQueue.TrySetResult();
                }
            },
            QueueDepthChanged = depth => UpdateMaximum(ref queueDepth, depth),
            DecoderWorkerStarting = () =>
            {
                if (Interlocked.Increment(ref decoderWorkerCount) == decoderLimit)
                {
                    decoderWorkersReady.TrySetResult();
                }
                releaseWorkers.Wait();
                if (Interlocked.CompareExchange(ref shouldFail, 1, 0) == 0)
                {
                    throw new InvalidOperationException("Injected decoder worker failure.");
                }
            }
        };
        Task<InvalidDataException> load = Task.Run(() => Assert.ThrowsException<InvalidDataException>(
            () => player.LoadResources(asParallel: true, observer)));

        var failures = new List<Exception>();
        try
        {
            await WaitForSignalOrProducerAsync(readersFilledQueue.Task, load, "Six reads did not reach the queue gate.");
            await WaitForSignalOrProducerAsync(decoderWorkersReady.Task, load, "Decoder workers did not reach the gate.");
            Assert.AreEqual(4, Volatile.Read(ref queueDepth));
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureCleanup(failures, releaseWorkers.Set);
        InvalidDataException? loadFailure = null;
        try
        {
            loadFailure = await load;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count == 0)
        {
            CaptureCleanup(failures, () => Assert.IsInstanceOfType<InvalidOperationException>(
                (loadFailure ?? throw new AssertFailedException("The pipeline did not report a resource failure.")).InnerException));
            CaptureCleanup(failures, () =>
            {
                BassAudioSession activeSession = BassAudioPlayer.ActiveSession
                    ?? throw new AssertFailedException("The null-device session was not active.");
                Assert.AreEqual(0, activeSession.GetPlayerStreams().Count,
                    "Players created before a worker failure must be reclaimed.");
            });
        }
        CaptureCleanup(failures, player.DisposeLoadedAudio);
        CaptureCleanup(failures, releaseWorkers.Dispose);
        CaptureCleanup(failures, directory.Dispose);
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task LoadResources_ReportsLowestIndexFailureAfterAllWorkersFinishAndKeepsPlayersPrivate()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("good.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("slow-broken.wav"), [1, 2, 3]);
        File.WriteAllBytes(directory.File("fast-broken.wav"), [4, 5, 6]);
        string chartPath = directory.File("chart.bms");
        WriteChart(chartPath,
            "#WAV02 good.wav\n"
            + "#WAV03 slow-broken.wav\n"
            + "#WAV04 fast-broken.wav\n"
            + "#00111:020304\n");
        var player = new TestBMSAutoPlayer(new BMSFile(chartPath));
        var slowReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSlowRead = new ManualResetEventSlim();
        var fastFailureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var goodPlayerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new AudioSourceLoadPipelineObserver
        {
            InputReadCompleted = path =>
            {
                if (path.EndsWith("slow-broken.wav", StringComparison.OrdinalIgnoreCase))
                {
                    slowReadStarted.TrySetResult();
                    allowSlowRead.Wait();
                }
            },
            ResourceFailed = (path, _) =>
            {
                if (path.EndsWith("fast-broken.wav", StringComparison.OrdinalIgnoreCase))
                {
                    fastFailureObserved.TrySetResult();
                }
            },
            PlayerCreated = (_, index) =>
            {
                if (index == 2)
                {
                    goodPlayerCreated.TrySetResult();
                }
            }
        };
        Task<InvalidDataException> load = Task.Run(() => Assert.ThrowsException<InvalidDataException>(
            () => player.LoadResources(asParallel: true, observer)));

        var failures = new List<Exception>();
        try
        {
            await WaitForSignalOrProducerAsync(slowReadStarted.Task, load, "The slow input did not reach its read gate.");
            await WaitForSignalOrProducerAsync(fastFailureObserved.Task, load, "The fast failure was not reported.");
            await WaitForSignalOrProducerAsync(goodPlayerCreated.Task, load, "The successful player was not created.");
            Assert.IsNull(player.GetAudioPlayer(2), "Partially constructed players must remain private.");
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureCleanup(failures, allowSlowRead.Set);
        InvalidDataException? loadFailure = null;
        try
        {
            loadFailure = await load;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count == 0)
        {
            CaptureCleanup(failures, () => StringAssert.Contains(
                (loadFailure ?? throw new AssertFailedException("The pipeline did not report a resource failure.")).Message,
                "slow-broken.wav"));
            CaptureCleanup(failures, () => Assert.IsInstanceOfType<AudioSourceLoadException>(
                (loadFailure ?? throw new AssertFailedException("The pipeline did not report a resource failure.")).InnerException));
            CaptureCleanup(failures, () =>
            {
                BassAudioSession activeSession = BassAudioPlayer.ActiveSession
                    ?? throw new AssertFailedException("The null-device session was not active.");
                Assert.AreEqual(0, activeSession.GetPlayerStreams().Count,
                    "Successful players created before a resource failure must be released.");
            });
        }
        CaptureCleanup(failures, player.DisposeLoadedAudio);
        CaptureCleanup(failures, allowSlowRead.Dispose);
        CaptureCleanup(failures, directory.Dispose);
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task LoadResources_SessionReplacementDuringReadCannotCreateSourcesOnReplacementSession()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        string chartPath = directory.File("chart.bms");
        WriteChart(chartPath, "#WAV02 used.wav\n#00111:02\n");
        var player = new TestBMSAutoPlayer(new BMSFile(chartPath));
        var inputRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDecode = new ManualResetEventSlim();
        var observer = new AudioSourceLoadPipelineObserver
        {
            InputReadCompleted = _ =>
            {
                inputRead.TrySetResult();
                allowDecode.Wait();
            }
        };
        Task<InvalidDataException> load = Task.Run(() => Assert.ThrowsException<InvalidDataException>(
            () => player.LoadResources(asParallel: true, observer)));
        BassAudioSession? replacement = null;

        var failures = new List<Exception>();
        try
        {
            await WaitForSignalOrProducerAsync(inputRead.Task, load, "The input did not reach the decode gate.");
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out replacement);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureCleanup(failures, allowDecode.Set);
        InvalidDataException? loadFailure = null;
        try
        {
            loadFailure = await load;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count == 0)
        {
            CaptureCleanup(failures, () => Assert.IsInstanceOfType<InvalidOperationException>(
                (loadFailure ?? throw new AssertFailedException("The pipeline did not report a resource failure.")).InnerException));
            CaptureCleanup(failures, () =>
            {
                BassAudioSession newSession = replacement
                    ?? throw new AssertFailedException("The replacement session was not created.");
                Assert.AreEqual(0, newSession.GetPlayerStreams().Count);
            });
        }
        CaptureCleanup(failures, player.DisposeLoadedAudio);
        CaptureCleanup(failures, allowDecode.Dispose);
        CaptureCleanup(failures, directory.Dispose);
        ThrowFailures(failures);
    }

    private static void WriteChart(string path, string body)
    {
        File.WriteAllText(
            path,
            "#PLAYER 1\n#TITLE audio input\n#ARTIST test\n" + body,
            Encoding.ASCII);
    }

    private static string WriteManyAudioChart(TemporaryDirectory directory, int audioCount)
    {
        var chart = new StringBuilder("#PLAYER 1\n#TITLE parallel input\n#ARTIST test\n");
        var notes = new StringBuilder();
        for (int index = 0; index < audioCount; index++)
        {
            string wavId = FormatWavId(index + 2);
            string fileName = "audio" + index + ".wav";
            File.WriteAllBytes(directory.File(fileName), BuildPcmWave());
            chart.Append("#WAV").Append(wavId).Append(' ').Append(fileName).Append('\n');
            notes.Append(wavId);
        }
        chart.Append("#00111:").Append(notes).Append('\n');
        string path = directory.File("chart.bms");
        File.WriteAllText(path, chart.ToString(), Encoding.ASCII);
        return path;
    }

    private static string FormatWavId(int value)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        return string.Concat(digits[value / digits.Length], digits[value % digits.Length]);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static async Task CaptureTaskCompletionAsync(
        Task task,
        List<Exception> failures,
        Func<Exception, bool>? expectedException = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (expectedException?.Invoke(exception) == true)
        {
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task WaitForSignalOrProducerAsync(Task signal, Task producer, string missingSignalMessage)
    {
        if (!signal.IsCompleted)
        {
            await Task.WhenAny(signal, producer).ConfigureAwait(false);
        }
        if (signal.IsCompleted)
        {
            await signal.ConfigureAwait(false);
            return;
        }

        await producer.ConfigureAwait(false);
        throw new AssertFailedException(missingSignalMessage);
    }

    private static void CaptureCleanup(List<Exception> failures, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Count > 1)
        {
            throw new AggregateException("The test failure and cleanup failures are retained.", failures);
        }
    }

    private static byte[] BuildPcmWave()
    {
        byte[] wave = new byte[46];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), 38);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 88200);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44), 16384);
        return wave;
    }

    private sealed class TestBMSAutoPlayer(BMSFile bms) : BMSAutoPlayer<BassAudioPlayer>(bms)
    {
        internal BassAudioPlayer? GetAudioPlayer(int index) => AudioPlayers[index];

        internal void DisposeLoadedAudio()
        {
            foreach (BassAudioPlayer? player in AudioPlayers)
            {
                player?.Dispose();
            }
        }
    }

    private sealed class StopObservingBMSAutoPlayer(BMSFile bms, int watchedIndex)
        : BMSAutoPlayer<BassAudioPlayer>(bms)
    {
        internal bool SawPlayerBeforeStop { get; private set; }

        internal bool SawPlayerAfterStop { get; private set; }

        public override void Stop()
        {
            SawPlayerBeforeStop = AudioPlayers[watchedIndex] != null;
            base.Stop();
            SawPlayerAfterStop = AudioPlayers[watchedIndex] != null;
        }
    }

    private sealed class DisposeFailureBMSAutoPlayer(BMSFile bms)
        : BMSAutoPlayer<DisposeFailureAudioPlayer>(bms)
    {
        internal DisposeFailureAudioPlayer? GetAudioPlayer(int index) => AudioPlayers[index];
    }

    private sealed class DisposeFailureAudioPlayer : BassAudioPlayer
    {
        internal DisposeFailureAudioPlayer(string fileName, DecodedAudio source, BassAudioSession session)
            : base(fileName, source, session)
        {
        }

        internal bool FailNextStreamFree { get; set; } = true;

        internal override bool FreeNativePlayerStream(int handle)
        {
            if (FailNextStreamFree)
            {
                FailNextStreamFree = false;
                return false;
            }
            return base.FreeNativePlayerStream(handle);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory()
        {
            Directory.CreateDirectory(path);
        }

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose()
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
