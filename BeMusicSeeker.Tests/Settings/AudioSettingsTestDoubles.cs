using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

internal sealed class TestAudioDeviceCatalog : IAudioDeviceCatalog
{
    internal IReadOnlyList<AudioDeviceInfo> Devices { get; set; } = [];

    internal bool EncoderAvailable { get; set; } = true;

    internal int RefreshCount { get; private set; }

    public void Refresh()
    {
        RefreshCount++;
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

internal sealed class CountingAudioSettingsEditSession : ISettingsEditSession
{
    internal CountingAudioSettingsEditSession(Settings values)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public Settings Values { get; }

    internal int SaveCount { get; private set; }

    public void Reload()
    {
    }

    public void Save()
    {
        SaveCount++;
    }

    public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
    {
        Values.OperationModeLR2DB = operationMode;
        Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
        Save();
        Reload();
    }
}

internal static class AudioDeviceTestWorkflowTestFactory
{
    internal static AudioDeviceTestWorkflowOwner Create()
    {
        return new AudioDeviceTestWorkflowOwner(
            new NoOpAudioDeviceTestPlaybackPort(),
            new SuccessfulAudioDeviceTestRuntime());
    }
}

internal sealed class NoOpAudioDeviceTestPlaybackPort : IAudioDeviceTestPlaybackPort
{
    public Task StopPlayback()
    {
        return Task.CompletedTask;
    }
}

internal sealed class AudioDeviceTestSoundCreationFailureBoundary : IAudioDeviceTestSoundBoundary
{
    public bool FileExists(string path) => true;

    public IAudioPlayer CreatePlayer(string path)
        => throw new InvalidOperationException("The test sound is not needed for cleanup diagnostics.");

    public long GetTimestamp() => throw new NotSupportedException();

    public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
        => throw new NotSupportedException();

    public void Wait(TimeSpan interval) => throw new NotSupportedException();
}

internal sealed class AudioDeviceTestCleanupNativeBoundary(string exceptionMessage)
    : IAudioSessionNativeBoundary
{
    private int streamFreeCount;

    internal int StreamFreeCount => Volatile.Read(ref streamFreeCount);

    public bool SetCoreDevice(int deviceIndex) => true;

    public bool FreeCore() => true;

    public Errors GetCoreError() => Errors.OK;

    public bool SetWasapiDevice(int deviceIndex) => true;

    public bool StopWasapi(bool reset) => true;

    public bool FreeWasapi() => true;

    public Errors GetWasapiError() => Errors.OK;

    public bool SetAsioDevice(int deviceIndex) => true;

    public bool StopAsio() => true;

    public bool FreeAsio() => true;

    public Errors GetAsioError() => Errors.OK;

    public bool FreeStream(int handle)
    {
        int call = Interlocked.Increment(ref streamFreeCount);
        if (call == 1)
        {
            return false;
        }
        if (call == 2)
        {
            throw new InvalidOperationException(exceptionMessage);
        }
        return true;
    }

    public Errors GetStreamError() => Errors.Unknown;
}

internal sealed class SuccessfulAudioDeviceTestRuntime : IAudioDeviceTestRuntime
{
    public AudioDeviceTestResult Run(
        AudioDeviceTestRequest request)
    {
        return AudioDeviceTestResultFactory.CreateSuccessful(request);
    }
}

internal sealed class GatedAudioDeviceTestRuntime : IAudioDeviceTestRuntime
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource secondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int callCount;

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int CallCount => Volatile.Read(ref callCount);

    internal void Release() => release.TrySetResult();

    internal void ReleaseSecond() => secondRelease.TrySetResult();

    public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
    {
        int call = Interlocked.Increment(ref callCount);
        if (call == 1)
        {
            Started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }
        else if (call == 2)
        {
            SecondStarted.TrySetResult();
            secondRelease.Task.GetAwaiter().GetResult();
        }
        else
        {
            throw new InvalidOperationException("The gated test runtime supports two calls.");
        }
        return AudioDeviceTestResultFactory.CreateSuccessful(request);
    }
}

internal sealed class DelegateAudioDeviceCapabilityRuntime : IAudioDeviceCapabilityRuntime
{
    private readonly Func<AudioDeviceCapabilityRequest, AudioDeviceCapabilityResult> query;

    internal DelegateAudioDeviceCapabilityRuntime(
        Func<AudioDeviceCapabilityRequest, AudioDeviceCapabilityResult> query)
    {
        this.query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public AudioDeviceCapabilityResult Query(AudioDeviceCapabilityRequest request, CancellationToken cancellationToken) => query(request);
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
