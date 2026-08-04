using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
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
        = new(AudioDriver.DirectSound, null, null);

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
            "%TITLE%");
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
            new SuccessfulAudioDeviceTestRuntime());
    }
}

internal sealed class NoOpAudioDeviceTestPlaybackPort : IAudioDeviceTestPlaybackPort
{
    public void StopPlayback()
    {
    }
}

internal sealed class SuccessfulAudioDeviceTestRuntime : IAudioDeviceTestRuntime
{
    public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
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
        double latency = 0,
        string? fallbackReason = null,
        bool streamProgressSucceeded = true)
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
            isSilentFallback: false);
        return new AudioDeviceTestResult(
            initialization,
            request.PlaySound,
            streamProgressSucceeded,
            request.PlaySound ? TimeSpan.FromSeconds(1) : TimeSpan.Zero,
            request.PlaySound ? TimeSpan.FromSeconds(1) : TimeSpan.Zero,
            request.PlaySound ? 1d : null,
            request.PlaySound ? TimeSpan.FromSeconds(8) : TimeSpan.Zero,
            streamProgressSucceeded ? null : "stream did not progress");
    }
}
