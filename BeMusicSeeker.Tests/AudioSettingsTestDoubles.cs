using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

internal sealed class TestAudioDeviceCatalog : IAudioDeviceCatalog
{
    internal IReadOnlyList<AudioDeviceInfo> Devices { get; set; } = [];

    internal bool EncoderAvailable { get; set; } = true;

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
    public AudioDriver PlayerDriver { get; set; } = AudioDriver.DirectSound;

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
        return new AudioDeviceTestResult(
            request.PlayerDriver,
            request.PlayerDevice,
            request.PlayerDeviceName,
            request.PlayerSampleRate,
            request.PlayerFormat,
            0);
    }
}
