using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Properties;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Models;

/// <summary>
/// 明示的な無音再生で実decoder・mixer・共通出力処理を使います。
/// 初回実pullだけを所有し、演奏中の駆動と再生Taskの停止合流は既存playerへ委ねます。
/// </summary>
internal sealed class NullOutputPlaybackRuntime : IAudioPlaybackRuntime, IPlaybackNativeObservation
{
    private readonly BassAudioSessionLease sessionLease = new();
    private readonly object volumeGate = new();
    private readonly Func<BassAudioSession, bool> releaseSession;
    private Task firstPullTask;
    private AudioPlaybackInitializationResult activeInitialization;
    private int? currentVolume;
    private bool initializing;

    private PcmRegistration pcmRegistration;

    /// <summary>現在所有する実sessionだけを公開します。</summary>
    public BassAudioSession Session => sessionLease.Session;

    /// <summary>登録後の実取得にだけ接続します。旧sessionへの登録と二重登録は拒否します。</summary>
    public IDisposable ObservePcm(BassAudioSession expectedSession, out Task<PlaybackPcmSnapshot> completion)
    {
        lock (volumeGate)
        {
            if (activeInitialization == null || !ReferenceEquals(expectedSession, sessionLease.Session)
                || expectedSession.State != BassAudioSessionState.Active)
            { throw new InvalidOperationException("The PCM observation session expired."); }
            if (pcmRegistration != null) { throw new InvalidOperationException("A PCM observation is already registered."); }
            var registration = new PcmRegistration(this, expectedSession);
            pcmRegistration = registration;
            registration.Callback = registration.Capture;
            Volatile.Write(ref expectedSession.PcmObservation, registration.Callback);
            completion = registration.Completion.Task;
            return registration;
        }
    }

    private sealed class PcmRegistration(NullOutputPlaybackRuntime owner, BassAudioSession session) : IDisposable
    {
        internal readonly TaskCompletionSource<PlaybackPcmSnapshot> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private bool disposed;
        internal PlaybackPcmCallback Callback;

        internal void Capture(BassAudioSession actual, int rate, int channels, ReadOnlySpan<float> samples)
        {
            lock (gate)
            {
                if (disposed || Completion.Task.IsCompleted) { return; }
                try
                {
                    if (!ReferenceEquals(actual, session)) { throw new InvalidOperationException("A different session supplied PCM."); }
                    int first = -1;
                    for (int index = 0; index < samples.Length; index++)
                    {
                        if (!float.IsFinite(samples[index])) { throw new InvalidOperationException("Nonfinite observed PCM."); }
                        if (samples[index] != 0 && first < 0) { first = index / channels * channels; }
                    }
                    if (first < 0) { return; }
                    int count = Math.Min(64 * channels, samples.Length - first);
                    Completion.TrySetResult(new(rate, channels, ImmutableArray.Create(samples.Slice(first, count).ToArray())));
                }
                catch (Exception failure) { Completion.TrySetException(failure); }
            }
        }

        public void Dispose()
        {
            lock (owner.volumeGate)
            {
                lock (gate)
                {
                    if (disposed) { return; }
                    disposed = true;
                    Interlocked.CompareExchange(ref session.PcmObservation, null, Callback);
                    Completion.TrySetCanceled();
                }
                if (ReferenceEquals(owner.pcmRegistration, this)) { owner.pcmRegistration = null; }
            }
        }
    }

    /// <summary>実native sessionを所有する明示無音runtimeを作成します。</summary>
    internal NullOutputPlaybackRuntime() : this(BassAudioPlayer.Free) { }

    /// <summary>解放失敗時の所有保持を検査できる解放境界を受け取ります。</summary>
    internal NullOutputPlaybackRuntime(Func<BassAudioSession, bool> releaseSession)
    {
        this.releaseSession = releaseSession ?? throw new ArgumentNullException(nameof(releaseSession));
    }

    /// <inheritdoc />
    public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!BassAudioRuntime.TryEnterAudioRequest(out IDisposable admission))
        {
            throw BassAudioPlayer.GetCleanupPendingFailure(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default)
                ?? new AudioInitializationException(
                    BassAudioPlayer.DeviceDriver.NULL_DEVICE, BassAudioPlayer.DeviceDriver.INVALID,
                    "audio request busy", default, default, "BassAudioOperationGate", null,
                    "Another audio request is still preparing or releasing its native resources.");
        }
        using IDisposable requestAdmission = admission;
        lock (volumeGate) { initializing = true; }
        try
        {
            BassAudioSession retained = sessionLease.Session;
            if (retained != null)
            {
                if (retained.State != BassAudioSessionState.Active || activeInitialization == null)
                {
                    throw new InvalidOperationException("The null output runtime still owns an unreleased session.");
                }
                retained.ThrowIfCallbackOutputFailed();
                if (activeInitialization.RequestedRate == settings.PlayerSampleRate
                    && retained.SampleRateConversionQuality == settings.SampleRateConversionQuality)
                {
                    return activeInitialization;
                }
                Free();
            }

            BassAudioSession acquired = null;
            try
            {
                BassAudioWriter.InitializeOwnedSession(
                    out acquired, settings.PlayerSampleRate, settings.SampleRateConversionQuality,
                    NullOutputProcessing.Processed, Math.Clamp(settings.PlayerVolume, 0, 100) / 100f);
                sessionLease.Attach(acquired);
                BassAudioBackendResult result = acquired.NegotiationResult
                    ?? throw new InvalidOperationException("The null output graph has no negotiated format.");
                // 明示的に選んだ無音出力であり、保存された可聴device要求のfallbackとは扱わない。
                activeInitialization = new AudioPlaybackInitializationResult(
                    AudioDriver.NullDevice, null, null, settings.PlayerSampleRate,
                    SampleFormat.SAMPLE_FLOAT_32BIT, 0f, false, settings.PlayerVolume,
                    AudioDriver.NullDevice, null, null, result.ActualRate,
                    result.EngineFormat, result.EndpointFormat, result.ActualChannels,
                    result.LatencyMilliseconds, null, isSilentFallback: false);
                return activeInitialization;
            }
            catch (Exception primary)
            {
                if (acquired != null)
                {
                    sessionLease.Attach(acquired);
                    try { Free(); }
                    catch (Exception cleanup)
                    {
                        throw new AggregateException("Null output initialization and cleanup failed.", primary, cleanup);
                    }
                }
                throw;
            }
        }
        finally
        {
            lock (volumeGate)
            {
                initializing = false;
                if (currentVolume is int volume)
                {
                    sessionLease.Session?.OutputProcessor?.SetTargetGain(volume / 100d);
                }
            }
        }
    }

    /// <summary>同sessionの初回実取得Taskへ合流し、空mixerの正常なゼロPCMも成功として待ちます。</summary>
    public Task WaitForOutputReadyAsync()
    {
        lock (volumeGate)
        {
            BassAudioSession session = sessionLease.Session
                ?? throw new InvalidOperationException("The null output runtime has no initialized session.");
            return firstPullTask ??= Task.Run(() =>
            {
                AudioPcmRenderer renderer = session.CallbackPcmRenderer
                    ?? throw new InvalidOperationException("The null output renderer is unavailable.");
                float[] buffer = new float[checked(BassAudioSession.NullOutputBlockFrames * renderer.ChannelCount)];
                int frames = BassAudioPlayer.ReadNullOutputFrames(session, buffer, BassAudioSession.NullOutputBlockFrames);
                if (frames <= 0)
                {
                    throw new InvalidOperationException("The first null output pull did not process any frames.");
                }
            });
        }
    }

    /// <inheritdoc />
    public int CurrentVoices => BassAudioPlayer.CurrentVoices;

    /// <inheritdoc />
    public int MaxVoices => BassAudioPlayer.MaxVoices;

    /// <inheritdoc />
    public void ClearMaxVoices() => BassAudioPlayer.ClearMaxVoices();

    /// <summary>NULL用のdevice音量特例を通さず、共通processorへ一度だけ希望gainを渡します。</summary>
    public void SetVolume(int volume)
    {
        lock (volumeGate)
        {
            currentVolume = Math.Clamp(volume, 0, 100);
            if (!initializing)
            {
                sessionLease.Session?.OutputProcessor?.SetTargetGain(currentVolume.Value / 100d);
            }
        }
    }

    /// <summary>呼出元の再生Task合流後、初回pullをlock外でjoinしてから実session解放を試みます。</summary>
    public void Free()
    {
        Task pull;
        lock (volumeGate)
        {
            pull = firstPullTask;
            // 解放を試したsessionは、未確認のまま次のInitializeで再利用しない。
            activeInitialization = null;
        }
        var failures = new List<Exception>();
        try { pull?.GetAwaiter().GetResult(); }
        catch (Exception failure) { failures.Add(failure); }
        try
        {
            lock (volumeGate) { pcmRegistration?.Dispose(); }
            if (!sessionLease.TryRelease(releaseSession))
            {
                throw new InvalidOperationException(Resources.AudioDeviceTestCleanupFailure);
            }
            lock (volumeGate)
            {
                firstPullTask = null;
                activeInitialization = null;
            }
        }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count == 1) { ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1)
        {
            throw new AggregateException("Null output pull and session cleanup failed.", failures);
        }
    }
}
