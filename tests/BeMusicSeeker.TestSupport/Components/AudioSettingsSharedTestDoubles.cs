using System.Diagnostics.CodeAnalysis;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using ManagedBass;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.TestSupport.Components;


internal sealed class TestAudioDeviceCatalog : IAudioDeviceCatalog
{
    internal IReadOnlyList<AudioDeviceInfo> Devices { get; set; } = [];

    internal bool EncoderAvailable { get; set; } = true;

    private int refreshCount;

    internal int RefreshCount => Volatile.Read(ref refreshCount);

    internal Action? RefreshAction { get; set; }

    public void Refresh()
    {
        Interlocked.Increment(ref refreshCount);
        RefreshAction?.Invoke();
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDriver driver)
    {
        return Devices;
    }

    public bool IsEncoderAvailable(EncoderType encoder, string encoderDirectory)
    {
        return EncoderAvailable;
    }
}

internal sealed class TestAudioSettingsGateway : IAudioSettingsGateway
{
    internal AudioOutputSelection OutputSelection { get; set; }
        = new(AudioDriver.WasapiShared, null, null);

    internal AudioDriver PlayerDriver
    {
        get => OutputSelection.Backend;
        set => OutputSelection = new AudioOutputSelection(
            value,
            OutputSelection.DeviceIdentity,
            OutputSelection.DeviceName);
    }

    public AudioOutputSelection CaptureOutputSelection() => OutputSelection;

    public void ApplyOutputSelection(AudioOutputSelection selection)
    {
        OutputSelection = selection;
    }

    public AudioNormalization EncoderNormalization { get; set; } = AudioNormalization.None;

    internal int SampleRateConversionQuality { get; set; } = AudioResamplingQuality.Default;

    internal EncoderType Encoder { get; private set; } = EncoderType.WAVE;

    public AudioEncodingSettingsSnapshot CaptureEncodingSettings()
    {
        return new AudioEncodingSettingsSnapshot(
            Encoder,
            SampleRate.AUTO,
            SampleFormat.AUTO,
            EncoderNormalization,
            0.8f,
            string.Empty,
            1f,
            "%TITLE%",
            SampleRateConversionQuality);
    }

    public void ApplyEncoderFallback(EncoderType encoder)
    {
        Encoder = encoder;
    }
}

internal static class AudioDeviceTestWorkflowTestFactory
{
    internal static AudioDeviceTestWorkflowOwner Create()
    {
        return new AudioDeviceTestWorkflowOwner(
            new NoOpAudioDeviceTestPlaybackPort(),
            new SuccessfulAudioDeviceTestRuntime(),
            requestAdmission: new TestAudioDeviceTestRequestAdmission());
    }
}

/// <summary>条件検証ごとの専有gateを使い、共有BASS状態やnative解放診断を参照しない受付です。</summary>
internal sealed class TestAudioDeviceTestRequestAdmission : IAudioDeviceTestRequestAdmission
{
    private readonly BassAudioOperationGate gate = new(initiallyOpen: true);

    internal bool RejectRequests { get; set; }

    internal AudioInitializationException? CleanupFailure { get; set; }

    internal int ActiveLeaseCount { get; private set; }

    /// <inheritdoc />
    public bool TryEnter(AudioDeviceTestRequest request, [NotNullWhen(true)] out IDisposable? lease,
        out AudioInitializationException? cleanupFailure)
    {
        cleanupFailure = CleanupFailure;
        if (RejectRequests || cleanupFailure != null || !gate.TryEnterRequest(out IDisposable admitted))
        {
            lease = null;
            return false;
        }
        ActiveLeaseCount++;
        lease = new TestLease(this, admitted);
        return true;
    }

    private sealed class TestLease(TestAudioDeviceTestRequestAdmission owner, IDisposable admitted) : IDisposable
    {
        private IDisposable? lease = admitted;

        public void Dispose()
        {
            IDisposable? accepted = Interlocked.Exchange(ref lease, null);
            if (accepted == null) { return; }
            accepted.Dispose();
            owner.ActiveLeaseCount--;
        }
    }
}

internal sealed class NoOpAudioDeviceTestPlaybackPort : IAudioDeviceTestPlaybackPort
{
    public Task StopPlayback()
    {
        return Task.CompletedTask;
    }
}

internal sealed class SuccessfulAudioDeviceTestRuntime : IAudioDeviceTestRuntime
{
    public AudioDeviceTestResult Run(
        AudioDeviceTestRequest request)
    {
        return AudioDeviceTestResultFactory.CreateSuccessful(request);
    }
}

internal static class AudioDeviceTestResultFactory
{
    internal static AudioDeviceTestResult CreateSuccessful(
        AudioDeviceTestRequest request,
        AudioDriver? actualBackend = null,
        string? actualDevice = null,
        string? actualDeviceName = null,
        SampleRate? actualRate = null,
        SampleFormat? engineFormat = null,
        SampleFormat? endpointFormat = null,
        int endpointContainerBits = 0,
        int endpointEffectiveBits = 0,
        double latency = 0,
        string? fallbackReason = null,
        bool streamProgressSucceeded = true,
        AudioDeviceTestFailureKind failureKind = AudioDeviceTestFailureKind.None,
        BassAudioPlaybackStage? playbackStage = null,
        string? nativeErrorSource = null,
        Errors? nativeErrorCode = null)
    {
        var initialization = new AudioPlaybackInitializationResult(
            request.PlayerDriver,
            request.PlayerDevice,
            request.PlayerDeviceName,
            request.PlayerSampleRate,
            request.PlayerFormat,
            request.PlayerBufferSize,
            request.PlayerWASAPIParam,
            request.PlayerVolume,
            actualBackend ?? request.PlayerDriver,
            actualDevice ?? request.PlayerDevice,
            actualDeviceName ?? request.PlayerDeviceName,
            actualRate ?? request.PlayerSampleRate,
            engineFormat ?? request.PlayerFormat,
            endpointFormat ?? engineFormat ?? request.PlayerFormat,
            2,
            latency,
            fallbackReason,
            isSilentFallback: false,
            endpointContainerBits: endpointContainerBits,
            endpointEffectiveBits: endpointEffectiveBits);
        AudioDeviceTestFailureKind effectiveFailureKind = failureKind != AudioDeviceTestFailureKind.None
            ? failureKind
            : request.PlaySound && !streamProgressSucceeded
                ? AudioDeviceTestFailureKind.PlaybackDidNotAdvance
                : AudioDeviceTestFailureKind.None;
        return new AudioDeviceTestResult(
            request,
            initialization,
            request.PlaySound,
            streamProgressSucceeded,
            request.PlaySound ? TimeSpan.FromSeconds(1) : TimeSpan.Zero,
            request.PlaySound ? TimeSpan.FromSeconds(1) : TimeSpan.Zero,
            request.PlaySound ? 1d : null,
            streamProgressSucceeded ? null : "stream did not progress",
            effectiveFailureKind,
            playbackStage,
            nativeErrorSource,
            nativeErrorCode);
    }
}
