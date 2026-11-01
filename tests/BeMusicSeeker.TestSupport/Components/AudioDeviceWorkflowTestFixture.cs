using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数moduleで共用する既存の準備・観測境界を保持します。</summary>
internal static class AudioDeviceWorkflowTestFixture
{

    internal static AudioDeviceTestRequest CreateRequest(bool playSound = false)
    {
        return new AudioDeviceTestRequest(
            AudioDriver.WasapiShared,
            "driver",
            "Device",
            SampleRate.AUTO,
            SampleFormat.AUTO,
            10,
            false,
            50,
            playSound: playSound);
    }

    internal static AudioDeviceCapabilityRequest CreateCapabilityRequest()
        => new(
            AudioDriver.Asio,
            "asio-device",
            "ASIO Device",
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_INT_16BIT);

    internal static AudioDeviceTestResult CreateResult(
        AudioDriver driver = AudioDriver.WasapiShared)
    {
        AudioDeviceTestRequest request = CreateRequest();
        return AudioDeviceTestResultFactory.CreateSuccessful(
            request,
            actualBackend: driver,
            actualDevice: "new-driver",
            actualDeviceName: "New Device",
            actualRate: SampleRate.SAMPLE_RATE_44100Hz,
            engineFormat: SampleFormat.SAMPLE_INT_16BIT,
            latency: 12.5);
    }

    internal sealed class DelegateAudioDeviceTestPlaybackPort : IAudioDeviceTestPlaybackPort
    {
        private readonly Action stopPlayback;

        internal DelegateAudioDeviceTestPlaybackPort(Action stopPlayback)
        {
            this.stopPlayback = stopPlayback;
        }

        public Task StopPlayback()
        {
            stopPlayback();
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingAudioDeviceTestRuntime : IAudioDeviceTestRuntime
    {
        private readonly Func<AudioDeviceTestRequest, AudioDeviceTestResult> run;

        internal RecordingAudioDeviceTestRuntime(Func<AudioDeviceTestResult> run)
        {
            this.run = _ => run();
        }

        internal RecordingAudioDeviceTestRuntime(Func<AudioDeviceTestRequest, AudioDeviceTestResult> run)
        {
            this.run = run;
        }

        internal int CallCount { get; private set; }

        public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
        {
            CallCount++;
            return run(request);
        }
    }
}
