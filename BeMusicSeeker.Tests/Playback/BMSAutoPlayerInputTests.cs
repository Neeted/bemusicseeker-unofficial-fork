using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.BMS;
using Ribbit.Logging;
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
    public void LoadResources_WarnsOncePerMissingOrCorruptPathWhileContinuing(bool asParallel)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("unused-broken.wav"), [0x01, 0x02, 0x03]);
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("broken.wav"), [0x04, 0x05, 0x06]);
        WriteChart(directory.File("chart.bms"),
            "#WAV01 unused-broken.wav\n"
            + "#WAV02 used.wav\n"
            + "#WAV03 missing.wav\n"
            + "#WAV04 broken.wav\n"
            + "#WAV05 ./BROKEN.WAV\n"
            + "#00111:02030405\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        int readCount = 0;
        var observer = new AudioSourceLoadPipelineObserver
        {
            OpenInput = path =>
            {
                Interlocked.Increment(ref readCount);
                return File.OpenRead(path);
            }
        };
        _ = NLogWrapper.GetLogger(nameof(BMSAutoPlayer));
        LoggingConfiguration? originalConfiguration = LogManager.Configuration;
        var warningTarget = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
        var testConfiguration = new LoggingConfiguration();
        testConfiguration.AddRule(LogLevel.Warn, LogLevel.Warn, warningTarget, nameof(BMSAutoPlayer));
        LogManager.Configuration = testConfiguration;

        try
        {
            player.LoadResources(asParallel, observer);

            Assert.IsNull(player.GetAudioResource(1), "An unused malformed definition must not be decoded.");
            Assert.IsNotNull(player.GetAudioResource(2), "The used audio file must be loaded.");
            Assert.IsNull(player.GetAudioResource(3), "A missing explicit source is omitted after its read failure.");
            Assert.IsNull(player.GetAudioResource(4));
            Assert.IsNull(player.GetAudioResource(5));
            Assert.AreEqual(3, readCount, "Each unique explicit path is attempted once; unused files are not opened.");
            Assert.AreEqual(2, player.OmittedAudioSources.Count);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    Path.GetFullPath(directory.File("missing.wav")),
                    Path.GetFullPath(directory.File("broken.wav"))
                },
                player.OmittedAudioSources.Select(omission => omission.Path).ToArray());
            Assert.AreEqual(2, warningTarget.Logs.Count, "The missing source and broken aliases each produce one warning.");
            string warnings = string.Join(Environment.NewLine, warningTarget.Logs);
            StringAssert.Contains(warnings, "missing.wav");
            StringAssert.Contains(warnings, "broken.wav");
            StringAssert.Contains(warnings, nameof(AudioSourceLoadException));
            StringAssert.Contains(warnings, nameof(FileNotFoundException));
        }
        finally
        {
            try
            {
                player.DisposeLoadedAudio();
            }
            finally
            {
                try
                {
                    LogManager.Flush();
                }
                finally
                {
                    LogManager.Configuration = originalConfiguration;
                }
            }
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LoadResources_AbortsWhenEveryExplicitMissingAudioFails(bool asParallel)
    {
        using var directory = new TemporaryDirectory();
        WriteChart(directory.File("chart.bms"),
            "#WAV02 missing.wav\n"
            + "#WAV03 also-missing.wav\n"
            + "#00111:0203\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));

        try
        {
            InvalidDataException failure = Assert.ThrowsException<InvalidDataException>(
                () => player.LoadResources(asParallel));

            StringAssert.Contains(failure.Message, "missing.wav");
            StringAssert.Contains(failure.Message, nameof(AudioSourceLoadStage.InspectContainer));
            Assert.IsInstanceOfType<AudioSourceLoadException>(failure.InnerException);
            Assert.AreEqual(AudioSourceLoadStage.InspectContainer,
                ((AudioSourceLoadException)failure.InnerException).Stage);
            Assert.IsNull(player.GetAudioResource(2));
            Assert.IsNull(player.GetAudioResource(3));
            Assert.AreEqual(0, player.OmittedAudioSources.Count, "An all-failed chart does not publish partial omission state.");
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    [TestMethod]
    public void LoadResources_UndefinedAndEmptyWavDefinitionsDoNotRequestFiles()
    {
        using var directory = new TemporaryDirectory();
        WriteChart(directory.File("chart.bms"), "#WAV02 \n#00111:0203\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        int openCount = 0;
        var observer = new AudioSourceLoadPipelineObserver
        {
            OpenInput = _ =>
            {
                Interlocked.Increment(ref openCount);
                throw new AssertFailedException("An undefined or empty WAV reference must not open a file.");
            }
        };

        try
        {
            player.LoadResources(asParallel: false, observer);

            Assert.AreEqual(0, openCount);
            Assert.AreEqual(0, player.OmittedAudioSources.Count);
            Assert.IsNull(player.GetAudioResource(2));
            Assert.IsNull(player.GetAudioResource(3));
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LoadResources_LoadsUsedZeroFrameWaveWithoutOmissions(bool asParallel)
    {
        using var directory = new TemporaryDirectory();
        using var wave = AudioMixerSignalTests.TemporaryFloatWave.Create(
            48000,
            0,
            _ => 0f);
        File.Copy(wave.Path, directory.File("empty.wav"));
        WriteChart(
            directory.File("chart.bms"),
            "#WAV02 empty.wav\n#00111:02\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));

        try
        {
            player.LoadResources(asParallel);

            BmsAudioResource loaded = player.GetAudioResource(2)
                ?? throw new AssertFailedException("The explicitly used zero-frame WAVE did not create a resource.");
            Assert.AreEqual(TimeSpan.Zero, loaded.Duration);
            Assert.AreEqual(0, player.OmittedAudioSources.Count);
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    [TestMethod]
    public async Task LoadResources_ReaderOutOfMemoryIsFatalAndJoinsBeforeCleanup()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"), "#WAV02 used.wav\n#00111:02\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        var injectedOutOfMemory = new OutOfMemoryException("Injected read worker allocation failure.");
        var observer = new AudioSourceLoadPipelineObserver
        {
            InputReadCompleted = _ => throw injectedOutOfMemory
        };

        try
        {
            AudioSourceFatalException failure = await Task.Run(() => Assert.ThrowsException<AudioSourceFatalException>(
                () => player.LoadResources(asParallel: true, observer)));

            Assert.AreSame(injectedOutOfMemory, failure.InnerException);
            BassAudioSession activeSession = BassAudioPlayer.ActiveSession
                ?? throw new AssertFailedException("The null-device session was not active.");
            Assert.AreEqual(0, activeSession.GetPlayerStreams().Count,
                "Fatal reader failure must release all temporary native owners after workers join.");
            Assert.AreEqual(0, player.OmittedAudioSources.Count,
                "A runtime allocation failure must not be published as an omitted source.");
        }
        finally
        {
            player.DisposeLoadedAudio();
            directory.Dispose();
        }
    }

    [TestMethod]
    public async Task DisposeBeforeNextSong_StopsAndJoinsBeforeDroppingPublishedAudioResources()
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
            Assert.IsTrue(player.SawResourceBeforeStop, "Stop must see the old decoded resources.");
            Assert.IsTrue(player.SawResourceAfterStop, "Resources must remain published through Stop's task join and reset.");
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
    public void DisposeBeforeNextSongOnUiDispatcher_ReleasesOldRealtimeGraphBeforeStartingNextSong()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave(frameCount: 44100 * 8));
        WriteChart(directory.File("first.bms"), "#WAV02 used.wav\n#BPM 400\n#00011:02\n");
        WriteChart(directory.File("next.bms"), "#WAV02 used.wav\n#BPM 400\n#00011:02\n");
        BMSAutoPlayer? firstPlayer = null;
        BMSAutoPlayer? nextPlayer = null;
        var failures = new List<Exception>();

        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                Assert.IsInstanceOfType(SynchronizationContext.Current, typeof(DispatcherSynchronizationContext));

                firstPlayer = new BMSAutoPlayer(new BMSFile(directory.File("first.bms")));
                firstPlayer.LoadResources(asParallel: false);
                BassAudioSession session = firstPlayer.ResourceSession;
                int baselineOwnedStreams = session.OwnedStreamCount;
                int baselinePlayerStreams = session.GetPlayerStreams().Count;
                int baselineAdditionalStreams = session.AdditionalStreamHandles.Count;

                Task firstPlayback = firstPlayer.Start();
                BmsRealtimeAudioScheduler oldScheduler = firstPlayer.RealtimeScheduler
                    ?? throw new AssertFailedException("The first BMS playback did not create its realtime scheduler.");
                Assert.IsTrue(session.GetPlayerStreams().Count > baselinePlayerStreams,
                    "The active song must retain its scheduled voice handles.");
                Assert.AreNotEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle,
                    "The active song must own a tempo output stream even at unity speed.");

                firstPlayer.DisposeBeforeNextSong();

                Assert.IsNull(firstPlayer.RealtimeScheduler,
                    "The next-song release must finish the old scheduler before dropping its session reference.");
                Assert.ThrowsException<ObjectDisposedException>(() => _ = oldScheduler.CurrentSongFrame);
                Assert.AreEqual(baselinePlayerStreams, session.GetPlayerStreams().Count,
                    "The old scheduled voice handles must be released before the next song starts.");
                Assert.AreEqual(baselineAdditionalStreams, session.AdditionalStreamHandles.Count,
                    "The old tempo output handle must be released before the next song starts.");
                Assert.AreEqual(baselineOwnedStreams, session.OwnedStreamCount);
                Assert.AreEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle);

                AssertPlaybackCanceledOnUiDispatcher(firstPlayback, "first-song playback cancellation");

                nextPlayer = new BMSAutoPlayer(new BMSFile(directory.File("next.bms")));
                nextPlayer.LoadResources(asParallel: false);
                Task nextPlayback = nextPlayer.Start();
                Assert.IsNotNull(nextPlayer.RealtimeScheduler,
                    "The next song must be able to create a new scheduler on the reused session.");
                Assert.AreNotEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle);

                nextPlayer.DisposeBeforeNextSong();
                Assert.IsNull(nextPlayer.RealtimeScheduler);
                Assert.AreEqual(baselinePlayerStreams, session.GetPlayerStreams().Count);
                Assert.AreEqual(baselineAdditionalStreams, session.AdditionalStreamHandles.Count);
                Assert.AreEqual(baselineOwnedStreams, session.OwnedStreamCount);
                Assert.AreEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle);
                AssertPlaybackCanceledOnUiDispatcher(nextPlayback, "next-song playback cancellation");
            });
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (firstPlayer != null)
        {
            CaptureCleanup(failures, firstPlayer.Dispose);
        }
        if (nextPlayer != null)
        {
            CaptureCleanup(failures, nextPlayer.Dispose);
        }
        CaptureCleanup(failures, directory.Dispose);
        ThrowFailures(failures);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ResourceDisposal_DropsSharedPcmWithoutCreatingPrototypeVoices(bool beforeNextSong)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("first.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("second.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#WAV02 first.wav\n#WAV03 second.wav\n#00111:0203\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        player.LoadResources(asParallel: false);
        Assert.IsNotNull(player.GetAudioResource(2));
        Assert.IsNotNull(player.GetAudioResource(3));
        BassAudioSession session = BassAudioPlayer.ActiveSession
            ?? throw new AssertFailedException("The test audio session was not initialized.");
        Assert.AreEqual(0, session.GetPlayerStreams().Count,
            "Loading chart resources must not create an eagerly attached native voice per WAV index.");

        if (beforeNextSong)
        {
            player.DisposeBeforeNextSong();
        }
        else
        {
            player.Dispose();
        }

        Assert.AreEqual(0, session.GetPlayerStreams().Count);
        Assert.AreEqual(0, player.AudioResourcesByIndex.Count);
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
            ResourceAssigned = (_, _) => Interlocked.Increment(ref createdCount)
        };

        try
        {
            first.LoadResources(asParallel, observer);

            Assert.AreEqual(1, readCount);
            Assert.AreEqual(1, decodeCount);
            Assert.AreEqual(2, createdCount);
            Assert.IsNotNull(first.GetAudioResource(2));
            Assert.IsNotNull(first.GetAudioResource(3));
            Assert.AreSame(first.GetAudioResource(2), first.GetAudioResource(3));

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
        Task<AudioSourceFatalException> load = Task.Run(() => Assert.ThrowsException<AudioSourceFatalException>(
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
        AudioSourceFatalException? loadFailure = null;
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
    public async Task LoadResources_ReportsLowIndexFailureAsWarningAfterAllWorkersFinish()
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
        var goodResourceAssigned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            ResourceAssigned = (_, index) =>
            {
                if (index == 2)
                {
                    goodResourceAssigned.TrySetResult();
                }
            }
        };
        var load = Task.Run(() => player.LoadResources(asParallel: true, observer));

        var failures = new List<Exception>();
        try
        {
            await WaitForSignalOrProducerAsync(slowReadStarted.Task, load, "The slow input did not reach its read gate.");
            await WaitForSignalOrProducerAsync(fastFailureObserved.Task, load, "The fast failure was not reported.");
            await WaitForSignalOrProducerAsync(goodResourceAssigned.Task, load, "The successful player was not created.");
            Assert.IsFalse(load.IsCompleted, "Loading must wait for the delayed lower-index input before publishing results.");
            Assert.IsNull(player.GetAudioResource(2), "Partially constructed players must remain private.");
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureCleanup(failures, allowSlowRead.Set);
        await CaptureTaskCompletionAsync(load, failures);
        if (failures.Count == 0)
        {
            CaptureCleanup(failures, () => Assert.IsNotNull(player.GetAudioResource(2)));
            CaptureCleanup(failures, () => Assert.IsNull(player.GetAudioResource(3)));
            CaptureCleanup(failures, () => Assert.IsNull(player.GetAudioResource(4)));
            CaptureCleanup(failures, () => Assert.AreEqual(2, player.OmittedAudioSources.Count));
            CaptureCleanup(failures, () =>
            {
                BassAudioSession activeSession = BassAudioPlayer.ActiveSession
                    ?? throw new AssertFailedException("The null-device session was not active.");
                Assert.AreEqual(0, activeSession.GetPlayerStreams().Count,
                    "The successfully decoded resource is published after all workers complete without creating a native voice.");
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
        Task<AudioSourceFatalException> load = Task.Run(() => Assert.ThrowsException<AudioSourceFatalException>(
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
        AudioSourceFatalException? loadFailure = null;
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

    private static void AssertPlaybackCanceledOnUiDispatcher(Task playback, string operationName)
    {
        try
        {
            TestUiDispatcherHost.AwaitTaskOnDispatcher(playback, operationName);
            Assert.Fail("The stopped playback task should report cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static byte[] BuildPcmWave(int frameCount = 1)
    {
        int dataLength = checked(frameCount * sizeof(short));
        byte[] wave = new byte[checked(44 + dataLength)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), checked((uint)(wave.Length - 8)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 88200);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), checked((uint)dataLength));
        for (int frame = 0; frame < frameCount; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + frame * sizeof(short)), 16384);
        }
        return wave;
    }

    private sealed class TestBMSAutoPlayer(BMSFile bms) : BMSAutoPlayer(bms)
    {
        internal BmsAudioResource? GetAudioResource(int index) => AudioResourcesByIndex[index];

        internal void DisposeLoadedAudio() => DisposeAudioSourcesAfterUse();
    }

    private sealed class StopObservingBMSAutoPlayer(BMSFile bms, int watchedIndex)
        : BMSAutoPlayer(bms)
    {
        internal bool SawResourceBeforeStop { get; private set; }

        internal bool SawResourceAfterStop { get; private set; }

        public override void Stop()
        {
            SawResourceBeforeStop = AudioResourcesByIndex[watchedIndex] != null;
            base.Stop();
            SawResourceAfterStop = AudioResourcesByIndex[watchedIndex] != null;
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
