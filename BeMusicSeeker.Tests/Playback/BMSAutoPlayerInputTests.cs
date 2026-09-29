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

    [TestMethod]
    public void LoadResources_WarnsOncePerMissingOrCorruptPathWhileContinuing()
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
        _ = NLogWrapper.GetLogger(nameof(BMSAutoPlayer));
        LoggingConfiguration? originalConfiguration = LogManager.Configuration;
        var warningTarget = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
        var testConfiguration = new LoggingConfiguration();
        testConfiguration.AddRule(LogLevel.Warn, LogLevel.Warn, warningTarget, nameof(BMSAutoPlayer));
        LogManager.Configuration = testConfiguration;

        try
        {
            player.LoadResources();

            Assert.IsNull(player.GetAudioResource(1), "An unused malformed definition must not be decoded.");
            Assert.IsNotNull(player.GetAudioResource(2), "The used audio file must be loaded.");
            Assert.IsNull(player.GetAudioResource(3), "A missing explicit source is omitted after its read failure.");
            Assert.IsNull(player.GetAudioResource(4));
            Assert.IsNull(player.GetAudioResource(5));
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

    [TestMethod]
    public void LoadResources_AbortsWhenEveryExplicitMissingAudioFails()
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
                player.LoadResources);

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
        try
        {
            player.LoadResources();

            Assert.AreEqual(0, player.OmittedAudioSources.Count);
            Assert.IsNull(player.GetAudioResource(2));
            Assert.IsNull(player.GetAudioResource(3));
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    [TestMethod]
    public void LoadResources_LoadsUsedZeroFrameWaveWithoutOmissions()
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
            player.LoadResources();

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
    public async Task DisposeBeforeNextSong_StopsAndJoinsBeforeDroppingPublishedAudioResources()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#BPM 400\n#WAV02 used.wav\n#10011:02\n");
        var player = new StopObservingBMSAutoPlayer(
            new BMSFile(directory.File("chart.bms")),
            watchedIndex: 2);
        player.LoadResources();

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
                firstPlayer.LoadResources();
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
                nextPlayer.LoadResources();
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
        player.LoadResources();
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

    private static void WriteChart(string path, string body)
    {
        File.WriteAllText(
            path,
            "#PLAYER 1\n#TITLE audio input\n#ARTIST test\n" + body,
            Encoding.ASCII);
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
