using System.Diagnostics;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using ManagedBass;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NullOutputPlaybackRuntimeTests
{
    private readonly BeMusicSeeker.Properties.Settings settings = TestSettingsFactory.CreateIsolatedSettings();
    private TestWorkspace? workspace;
    private NullOutputPlaybackRuntime? runtime;
    private InternalBMSAutoPlayerSoundOnly? internalPlayer;
    private BassAudioSession? rawSession;
    private SampleRate previousRate;
    private SampleFormat previousFormat;
    private float previousGain;
    private float previousVolume;
    private bool previousMute;

    [TestInitialize]
    public void Initialize()
    {
        previousRate = BassAudioPlayer.Frequency;
        previousFormat = BassAudioPlayer.Format;
        previousGain = BassAudioPlayer.DefaultVolume;
        previousVolume = BassAudioPlayer.DeviceVolume;
        previousMute = BassAudioPlayer.IsDeviceMuted;
        workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        BassAudioPlayer.Free();
        BassAudioRuntime.Shutdown();
        BassAudioPlayer.DefaultVolume = 1f;
        BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_44100Hz;
        settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_48000Hz;
        settings.PlayerResamplingQuality = 5;
        settings.uBMplayVolume = 25;
        runtime = new NullOutputPlaybackRuntime();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        var failures = new List<Exception>();
        if (internalPlayer != null)
        {
            try { await internalPlayer.CloseAsync(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        Attempt(() => runtime?.Free());
        Attempt(() =>
        {
            if (rawSession != null && !BassAudioPlayer.Free(rawSession))
            {
                throw new InvalidOperationException("The raw fixture session was not released.");
            }
        });
        Attempt(BassAudioRuntime.Shutdown);
        Attempt(() => BassAudioPlayer.Frequency = previousRate);
        Attempt(() => BassAudioPlayer.Format = previousFormat);
        Attempt(() => BassAudioPlayer.DefaultVolume = previousGain);
        Attempt(() => BassAudioPlayer.DeviceVolume = previousVolume);
        Attempt(() => BassAudioPlayer.IsDeviceMuted = previousMute);
        Attempt(() => workspace?.Dispose());
        if (failures.Count != 0) { throw new AggregateException(failures); }
    }

    /// <summary>初回実readの未完了、正常無音の成功、同session一回取得、新sessionでの再観測を実DLLで確認します。</summary>
    [TestMethod]
    public async Task Ready_JoinsOneRealPullAndDoesNotReuseItAfterSessionReplacement()
    {
        NullOutputPlaybackRuntime output = Output;
        AudioPlaybackInitializationResult result = output.Initialize(Snapshot);
        Assert.AreEqual(settings.PlayerSampleRate, result.ActualRate);
        Assert.IsFalse(result.IsSilentFallback);
        Assert.IsFalse(result.FallbackOccurred);
        BassAudioSession first = Session;
        using var release = new ManualResetEventSlim();
        TaskCompletionSource entered = Signal();
        var native = new ObservedPcmNative(() => { entered.TrySetResult(); release.Wait(); });
        InstallRenderer(first, native);
        Task ready = output.WaitForOutputReadyAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(ready.IsCompleted);
            Assert.AreSame(ready, output.WaitForOutputReadyAsync());
        }
        finally { release.Set(); }
        await ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(4096L, native.FramesRead);
        Assert.IsTrue(native.Samples.All(value => value == 0f), "空mixerの正常無音はReadyになります。");
        await output.WaitForOutputReadyAsync();
        Assert.AreEqual(4096L, native.FramesRead, "Readyの再待機で二重取得しません。");

        settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
        output.Initialize(Snapshot);
        Assert.IsTrue(first.IsReleased);
        Assert.AreEqual(44100, Bass.ChannelGetInfo(Session.MixerHandle).Frequency);
        using var secondRelease = new ManualResetEventSlim();
        TaskCompletionSource secondEntered = Signal();
        var secondNative = new ObservedPcmNative(() => { secondEntered.TrySetResult(); secondRelease.Wait(); });
        InstallRenderer(Session, secondNative);
        Task nextReady = output.WaitForOutputReadyAsync();
        try
        {
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(nextReady.IsCompleted);
            Assert.AreNotSame(ready, nextReady);
        }
        finally { secondRelease.Set(); }
        await nextReady.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(4096L, secondNative.FramesRead);
    }

    /// <summary>既定parser・preparation・playerのBMS/bmson開始を通し、Ready後に実音源PCMへ到達し、停止回収後に再開始できます。</summary>
    /// <remarks>出力gainの独立値はGainケース、App/Panel/一時コピーの接続は別境界で検査します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InternalPlayer_DefaultGraphReadsOwnedChartAndWaveThenReleases(bool bmson)
    {
        (string chart, byte[] chartBytes, byte[] waveBytes) = WriteFixture(bmson);
        AudioDriver savedDriver = Snapshot.PlayerDriver;
        NullOutputPlaybackRuntime output = Output;
        output.Initialize(Snapshot);
        BassAudioSession session = Session;
        using var release = new ManualResetEventSlim();
        TaskCompletionSource entered = Signal();
        var native = new ObservedPcmNative(() => { entered.TrySetResult(); release.Wait(); });
        InstallRenderer(session, native);
        internalPlayer = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => settings), output);
        PlaybackStartOperation operation = internalPlayer.BeginStart(chart, null, allowPreload: true);
        Assert.AreSame(output, operation.NativeObservation);
        PlaybackStartOperation? adopted = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(operation.Ready.IsCompleted);
            release.Set();
            await operation.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            await native.NonZero.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(operation.Completion.IsCompleted, "Readyと演奏Completionは別の終端です。");
            Assert.AreEqual(48000, session.CallbackPcmRenderer?.SampleRate);
            Assert.AreEqual(5, session.SampleRateConversionQuality);
            Assert.AreEqual(savedDriver, Snapshot.PlayerDriver, "通常の保存deviceを無音deviceへ書き換えません。");
            CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(chart));
            CollectionAssert.AreEqual(waveBytes, File.ReadAllBytes(FilePath("sound.wav")));
            Exception? preparationFailure = null;
            await ((INextSongPreloadPlayer)internalPlayer).PrepareNextAsync(
                NextSongPreloadInput.Capture(chart), failure => preparationFailure = failure);
            // 既定の準備TaskへTakeAsyncで合流し、同じsessionへ採用してから次曲をStartします。
            adopted = internalPlayer.BeginStart(chart, null, allowPreload: true);
            await adopted.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNull(preparationFailure);
            Assert.IsFalse(adopted.Completion.IsCompleted);
            Assert.AreSame(session, Session);
        }
        finally
        {
            release.Set();
            try { await internalPlayer.CloseAsync(); }
            finally
            {
                try { await ObserveStopped(operation.Completion); }
                finally { if (adopted != null) { await ObserveStopped(adopted.Completion); } }
            }
        }
        Assert.IsTrue(session.IsReleased);
        Assert.AreEqual(0, session.OwnedStreamCount);
        int readsAfterClose = native.ReadCalls;
        PlaybackStartOperation next = internalPlayer.BeginStart(chart, null, allowPreload: true);
        try { await next.Ready.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            await internalPlayer.CloseAsync();
            await ObserveStopped(next.Completion);
        }
        Assert.AreEqual(readsAfterClose, native.ReadCalls, "解放済みsessionのreaderへ戻りません。");
    }

    /// <summary>PCM16の独立波形とgain/ramp式を実callback出力と比較し、音量の未適用・二重適用を区別します。</summary>
    [TestMethod]
    public async Task Gain_IsAppliedOnceAndLiveVolumeUsesTheSameProcessor()
    {
        WriteFixture(false);
        NullOutputPlaybackRuntime output = Output;
        output.SetVolume(25);
        settings.uBMplayVolume = 80;
        output.Initialize(Snapshot);
        await output.WaitForOutputReadyAsync();
        BassAudioSession session = Session;
        using var source = new BassAudioPlayer(FilePath("sound.wav")) { Volume = 1f };
        float[] pcm = new float[4096 * 2];
        using IDisposable registration = output.ObservePcm(session, out Task<PlaybackPcmSnapshot> observed);
        source.Play();
        Assert.AreEqual(4096, BassAudioPlayer.ReadNullOutputFrames(session, pcm, 4096));
        PlaybackPcmSnapshot snapshot = await observed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(48000, snapshot.SampleRate);
        Assert.AreEqual(2, snapshot.Channels);
        CollectionAssert.AreEqual(pcm.Take(snapshot.Samples.Length).ToArray(), snapshot.Samples.ToArray(),
            "受動観測は追加pullせず、成功した同一取得のPCMを返します。");
        AssertWave(pcm, frame => 0.25d);
        output.SetVolume(50);
        source.Play();
        Assert.AreEqual(4096, BassAudioPlayer.ReadNullOutputFrames(session, pcm, 4096));
        int rampFrames = (int)Math.Round((int)settings.PlayerSampleRate * 0.005d, MidpointRounding.ToEven);
        AssertWave(pcm, frame => 0.25d + 0.25d * Math.Min(frame + 1, rampFrames) / rampFrames);
    }

    /// <summary>0秒/2秒の発音位置を入力定義から算出し、既定parser・decoder・予約・共通gain後の全frameを照合します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScheduledPcm_MatchesIndependentWaveAndEventFrames(bool bmson)
    {
        (string chart, byte[] chartBytes, byte[] waveBytes) = WriteFixture(bmson);
        NullOutputPlaybackRuntime output = Output;
        output.Initialize(Snapshot);
        await output.WaitForOutputReadyAsync();
        BassAudioSession session = Session;
        using var player = new BMSAutoPlayer(PlaybackChart.Load(chart));
        player.LoadResources();
        int rate = (int)settings.PlayerSampleRate;
        Assert.AreEqual(rate, player.AudioSchedule.SampleRate);
        using var scheduler = new BmsRealtimeAudioScheduler(
            player.AudioSchedule, player.AudioResourcesByIndex, session,
            new BassMixerSourceNativeBoundary(), player.Duration, 0, 1f, 1);
        long total = rate * 2L + 4096;
        long position = 0;
        float[] pcm = new float[1024 * 2];
        while (position < total)
        {
            scheduler.TickWithoutNullOutputAdvance();
            int count = (int)Math.Min(1024, total - position);
            Assert.AreEqual(count, BassAudioPlayer.ReadNullOutputFrames(session, pcm, count));
            for (int frame = 0; frame < count; frame++)
            {
                long absolute = position + frame;
                long sourceFrame = absolute >= rate * 2L ? absolute - rate * 2L : absolute;
                float left = sourceFrame >= 4096 ? 0f : sourceFrame < 2048 ? 0.25f : -0.125f;
                float right = sourceFrame >= 4096 ? 0f : sourceFrame < 2048 ? -0.125f : 0.25f;
                Assert.AreEqual(left * 0.25f, pcm[frame * 2], 1e-7f, $"left song frame {absolute}");
                Assert.AreEqual(right * 0.25f, pcm[frame * 2 + 1], 1e-7f, $"right song frame {absolute}");
            }
            position += count;
        }
        Assert.AreEqual(total, scheduler.CurrentSongFrame);
        CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(chart));
        CollectionAssert.AreEqual(waveBytes, File.ReadAllBytes(FilePath("sound.wav")));
    }

    /// <summary>native取得が成功してもprocessorが失敗したゼロ埋めをReadyにせず、故障とcleanup失敗を両方残します。</summary>
    [TestMethod]
    public async Task Ready_RejectsProcessorFailureAndRetainsOwnershipUntilExplicitCleanup()
    {
        WriteFixture(false);
        var cleanupFailure = new IOException("injected release failure");
        bool failRelease = true;
        runtime = new NullOutputPlaybackRuntime(session => failRelease ? throw cleanupFailure : BassAudioPlayer.Free(session));
        NullOutputPlaybackRuntime output = Output;
        output.Initialize(Snapshot);
        BassAudioSession session = Session;
        using (var source = new BassAudioPlayer(FilePath("sound.wav")) { Volume = 1f })
        {
            source.Play();
            session.OutputProcessor = new AudioOutputProcessor(48000, double.MaxValue);
            AudioCallbackOutputFailureException failure = await Assert.ThrowsExactlyAsync<AudioCallbackOutputFailureException>(
                () => output.WaitForOutputReadyAsync());
            Assert.AreEqual(AudioOutputProcessFailure.NonFiniteOutput, failure.Failure.ProcessStage);
            AggregateException combined = Assert.ThrowsExactly<AggregateException>(output.Free);
            Assert.IsTrue(combined.Flatten().InnerExceptions.Contains(cleanupFailure));
            Assert.IsTrue(combined.Flatten().InnerExceptions.Contains(failure));
            Assert.IsFalse(session.IsReleased);
            Assert.ThrowsExactly<InvalidOperationException>(() => output.Initialize(Snapshot));
        }
        failRelease = false;
        await Assert.ThrowsExactlyAsync<AudioCallbackOutputFailureException>(() => Task.Run(output.Free));
        Assert.IsTrue(session.IsReleased);
        output.Initialize(Snapshot);
        await output.WaitForOutputReadyAsync();
    }

    /// <summary>初回native取得中のFreeは実Task終端を待って解放し、待機中の音量通知を妨げません。</summary>
    [TestMethod]
    public async Task Free_JoinsFirstPullOutsideNativeAndVolumeLocks()
    {
        bool released = false;
        runtime = new NullOutputPlaybackRuntime(session => { released = true; return BassAudioPlayer.Free(session); });
        NullOutputPlaybackRuntime output = Output;
        output.Initialize(Snapshot);
        BassAudioSession session = Session;
        using var release = new ManualResetEventSlim();
        TaskCompletionSource entered = Signal();
        InstallRenderer(session, new ObservedPcmNative(() => { entered.TrySetResult(); release.Wait(); }));
        Task ready = output.WaitForOutputReadyAsync();
        Task? free = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            TaskCompletionSource freeEntered = Signal();
            free = Task.Run(() => { freeEntered.TrySetResult(); output.Free(); });
            await freeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(() => output.SetVolume(40)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(released);
            Assert.IsFalse(free.IsCompleted);
        }
        finally
        {
            release.Set();
            try { await ready.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (free != null) { await free.WaitAsync(TimeSpan.FromSeconds(10)); } }
        }
        Assert.IsTrue(released);
        Assert.IsTrue(session.IsReleased);
    }

    /// <summary>Processed schedulerだけが経過frameを一回取得し、pause/seek/resumeで停止期間を取り戻しません。Rawは従来のgain・時計を維持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scheduler_UsesOneDriveAndKeepsRawTimingIndependent(bool processed)
    {
        (string chart, _, _) = WriteFixture(false);
        NullOutputPlaybackRuntime output = Output;
        if (processed)
        {
            output.Initialize(Snapshot);
            await output.WaitForOutputReadyAsync();
        }
        else
        {
            output.SetVolume(0);
            output.Initialize(Snapshot);
            await output.WaitForOutputReadyAsync();
            output.Free();
            BassAudioPlayer.Frequency = settings.PlayerSampleRate;
            BassAudioWriter.InitializeOwnedSession(out rawSession);
        }
        BassAudioSession session = Session;
        var rawCallback = new ObservedPcmNative();
        if (!processed)
        {
            // 既存手動NULL fixtureはcallback部品を持てます。その有無をProcessedの代用にしません。
            session.PublishCallbackOutputHandle(session.MixerHandle);
            session.OutputProcessor = new AudioOutputProcessor((int)settings.PlayerSampleRate, 0d);
            InstallRenderer(session, rawCallback);
            await session.WaitForOutputReadyAsync();
        }
        using var player = new BMSAutoPlayer(PlaybackChart.Load(chart));
        player.LoadResources();
        int rate = (int)settings.PlayerSampleRate;
        long clock = Stopwatch.Frequency;
        using var scheduler = new BmsRealtimeAudioScheduler(
            player.AudioSchedule, player.AudioResourcesByIndex, session,
            new BassMixerSourceNativeBoundary(), player.Duration, 0, 1f, 1, () => clock);
        long origin = MixerFrame(session);
        clock += Stopwatch.Frequency / 100;
        scheduler.Tick();
        Assert.AreEqual(origin + rate / 100, MixerFrame(session));
        scheduler.Tick();
        Assert.AreEqual(origin + rate / 100, MixerFrame(session), "同時刻のtickで二重消費しません。");
        session.SetCallbackOutputPaused(true);
        scheduler.ResetControlInterval();
        clock += Stopwatch.Frequency * 5;
        long pausedFrame = MixerFrame(session);
        if (processed)
        {
            float[] paused = new float[64];
            Assert.AreEqual(0, BassAudioPlayer.ReadNullOutputFrames(session, paused, 32));
            Assert.AreEqual(pausedFrame, MixerFrame(session));
        }
        scheduler.ResetControlInterval();
        session.SetCallbackOutputPaused(false);
        scheduler.Tick();
        Assert.AreEqual(processed ? pausedFrame : pausedFrame + rate * 5, MixerFrame(session));
        session.SetCallbackOutputPaused(true);
        scheduler.Rebase(rate * 2L);
        Assert.AreEqual(rate * 2L, scheduler.CurrentSongFrame);
        clock += Stopwatch.Frequency * 5;
        scheduler.ResetControlInterval();
        session.SetCallbackOutputPaused(false);
        clock += Stopwatch.Frequency / 100;
        long resumedOrigin = MixerFrame(session);
        scheduler.Tick();
        Assert.AreEqual(resumedOrigin + (processed ? rate / 100 : rate * 5 + rate / 100), MixerFrame(session));
        if (!processed) { Assert.AreEqual(0, rawCallback.ReadCalls); }
    }

    /// <summary>pause・出力欠落・native故障のゼロ埋めを正frame取得と混同せず、故障後も実sessionを回収します。</summary>
    [TestMethod]
    [DataRow("paused")]
    [DataRow("missing")]
    [DataRow("native")]
    public async Task Ready_DoesNotAcceptZeroFilledOrFailedPull(string failureMode)
    {
        NullOutputPlaybackRuntime output = Output;
        output.Initialize(Snapshot);
        BassAudioSession session = Session;
        if (failureMode == "paused") { session.SetCallbackOutputPaused(true); }
        else if (failureMode == "missing") { session.PublishCallbackOutputHandle(0); }
        else { InstallRenderer(session, new FailedPcmNative()); }
        Task ready = output.WaitForOutputReadyAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ready);
        Assert.AreSame(ready, output.WaitForOutputReadyAsync());
        Assert.Throws<InvalidOperationException>(output.Free);
        Assert.IsTrue(session.IsReleased, "取得故障でも解放は試みます。");
        output.Initialize(Snapshot);
        await output.WaitForOutputReadyAsync();
    }

    /// <summary>待機側の取消で実native readを打ち切らず、Freeはその実終端まで所有を保持します。</summary>
    [TestMethod]
    public async Task CancelledWait_DoesNotReleaseTheLiveFirstPull()
    {
        NullOutputPlaybackRuntime output = Output;
        output.Initialize(Snapshot);
        BassAudioSession session = Session;
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource entered = Signal();
        InstallRenderer(session, new ObservedPcmNative(() => { entered.TrySetResult(); release.Wait(); }));
        Task ready = output.WaitForOutputReadyAsync();
        Task? free = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => ready.WaitAsync(cancellation.Token));
            free = Task.Run(output.Free);
            Assert.IsFalse(ready.IsCompleted);
            Assert.IsFalse(session.IsReleased);
        }
        finally
        {
            release.Set();
            try { await ready.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (free != null) { await free.WaitAsync(TimeSpan.FromSeconds(10)); } }
        }
        Assert.IsTrue(session.IsReleased);
    }

    private sealed class FailedPcmNative : IAudioPcmNative
    {
        public Errors LastError => Errors.Device;
        public int ChannelGetData(int channel, float[] buffer, int lengthBytes) => -1;
    }

    private NullOutputPlaybackRuntime Output => runtime ?? throw new InvalidOperationException("runtime is not initialized");
    private PlayerSettingsSnapshot Snapshot => new SettingsPlayerSettingsGateway(() => settings).CaptureSnapshot();
    private static BassAudioSession Session => BassAudioPlayer.ActiveSession ?? throw new AssertFailedException("実sessionが必要です。");
    private string FilePath(string name) => Path.Combine((workspace ?? throw new InvalidOperationException()).RootDirectory, name);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void InstallRenderer(BassAudioSession session, IAudioPcmNative native)
    {
        ChannelInfo info = Bass.ChannelGetInfo(session.MixerHandle);
        session.CallbackPcmRenderer = new AudioPcmRenderer(session.CallbackOutputHandle, info.Frequency, info.Channels, native);
    }

    private static long MixerFrame(BassAudioSession session)
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        Bass.CurrentDevice = session.CoreDeviceIndex;
        long bytes = Bass.ChannelGetPosition(session.MixerHandle, PositionFlags.Bytes);
        Assert.IsTrue(bytes >= 0);
        return bytes / (2 * sizeof(float));
    }

    private static async Task ObserveStopped(Task completion)
    {
        try { await completion.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }

    private (string Chart, byte[] ChartBytes, byte[] WaveBytes) WriteFixture(bool bmson)
    {
        string chart = FilePath(bmson ? "chart.bmson" : "chart.bms");
        string text = bmson
            ? "{\"version\":\"1.0.0\",\"info\":{\"init_bpm\":120,\"resolution\":240},\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0,\"c\":false},{\"x\":1,\"y\":960,\"l\":0,\"c\":false}]}]}"
            : "#PLAYER 1\n#TITLE null output fixture\n#BPM 120\n#WAV01 sound.wav\n#00011:01\n#00111:01\n";
        byte[] chartBytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(chart, chartBytes);
        byte[] wave = new byte[44 + 4096 * 2 * sizeof(short)];
        using (var writer = new BinaryWriter(new MemoryStream(wave), Encoding.ASCII))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(wave.Length - 8);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            writer.Write((int)settings.PlayerSampleRate);
            writer.Write((int)settings.PlayerSampleRate * 4);
            writer.Write((ushort)4);
            writer.Write((ushort)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(4096 * 4);
            for (int frame = 0; frame < 4096; frame++)
            {
                writer.Write((short)(frame < 2048 ? 8192 : -4096));
                writer.Write((short)(frame < 2048 ? -4096 : 8192));
            }
        }
        File.WriteAllBytes(FilePath("sound.wav"), wave);
        return (chart, chartBytes, wave);
    }

    private static void AssertWave(float[] actual, Func<int, double> gain)
    {
        for (int frame = 0; frame < 4096; frame++)
        {
            double left = frame < 2048 ? 8192d / 32768 : -4096d / 32768;
            double right = frame < 2048 ? -4096d / 32768 : 8192d / 32768;
            Assert.AreEqual((float)(left * gain(frame)), actual[frame * 2], 1e-7f, $"left frame {frame}");
            Assert.AreEqual((float)(right * gain(frame)), actual[frame * 2 + 1], 1e-7f, $"right frame {frame}");
        }
    }

    /// <summary>実native readへ委譲し、初回取得だけを保留して実PCMを観測します。</summary>
    private sealed class ObservedPcmNative(Action? beforeFirstRead = null) : IAudioPcmNative
    {
        private int readCalls;
        private long framesRead;
        private readonly List<float> samples = [];
        internal TaskCompletionSource NonZero { get; } = Signal();
        internal int ReadCalls => Volatile.Read(ref readCalls);
        internal long FramesRead => Interlocked.Read(ref framesRead);
        internal float[] Samples { get { lock (samples) { return samples.ToArray(); } } }
        public Errors LastError => Bass.LastError;
        public int ChannelGetData(int channel, float[] buffer, int lengthBytes)
        {
            if (Interlocked.Increment(ref readCalls) == 1) { beforeFirstRead?.Invoke(); }
            int bytes = Bass.ChannelGetData(channel, buffer, lengthBytes);
            if (bytes > 0)
            {
                Interlocked.Add(ref framesRead, bytes / (2 * sizeof(float)));
                lock (samples) { samples.AddRange(buffer.Take(bytes / sizeof(float))); }
                if (buffer.Take(bytes / sizeof(float)).Any(value => Math.Abs(value) > 0.01f)) { NonZero.TrySetResult(); }
            }
            return bytes;
        }
    }
}
