using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

internal sealed class AudioDeviceTestRequest
{
    internal AudioDeviceTestRequest(
        AudioDriver playerDriver,
        string playerDevice,
        string playerDeviceName,
        SampleRate playerSampleRate,
        SampleFormat playerFormat,
        float playerBufferSize,
        bool playerWASAPIParam,
        int playerVolume,
        bool playSound)
    {
        PlayerDriver = playerDriver;
        PlayerDevice = playerDevice;
        PlayerDeviceName = playerDeviceName;
        PlayerSampleRate = playerSampleRate;
        PlayerFormat = playerFormat;
        PlayerBufferSize = playerBufferSize;
        PlayerWASAPIParam = playerWASAPIParam;
        PlayerVolume = playerVolume;
        PlaySound = playSound;
    }

    internal AudioDriver PlayerDriver { get; }

    internal string PlayerDevice { get; }

    internal string PlayerDeviceName { get; }

    internal SampleRate PlayerSampleRate { get; }

    internal SampleFormat PlayerFormat { get; }

    internal float PlayerBufferSize { get; }

    internal bool PlayerWASAPIParam { get; }

    internal int PlayerVolume { get; }

    internal bool PlaySound { get; }
}

internal sealed class AudioDeviceTestResult
{
    internal AudioDeviceTestResult(
        AudioDriver playerDriver,
        string playerDevice,
        string playerDeviceName,
        SampleRate playerSampleRate,
        SampleFormat playerFormat,
        double playerLatency)
    {
        PlayerDriver = playerDriver;
        PlayerDevice = playerDevice;
        PlayerDeviceName = playerDeviceName;
        PlayerSampleRate = playerSampleRate;
        PlayerFormat = playerFormat;
        PlayerLatency = playerLatency;
    }

    internal AudioDriver PlayerDriver { get; }

    internal string PlayerDevice { get; }

    internal string PlayerDeviceName { get; }

    internal SampleRate PlayerSampleRate { get; }

    internal SampleFormat PlayerFormat { get; }

    internal double PlayerLatency { get; }
}

internal interface IAudioDeviceTestRuntime
{
    AudioDeviceTestResult Run(AudioDeviceTestRequest request);
}

internal interface IAudioDeviceTestPlaybackPort
{
    void StopPlayback();
}

internal sealed class AudioDeviceTestWorkflowOwner
{
    private readonly IAudioDeviceTestPlaybackPort playbackPort;

    private readonly IAudioDeviceTestRuntime runtime;

    private int isRunning;

    internal AudioDeviceTestWorkflowOwner(
        IAudioDeviceTestPlaybackPort playbackPort,
        IAudioDeviceTestRuntime runtime)
    {
        this.playbackPort = playbackPort ?? throw new ArgumentNullException(nameof(playbackPort));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    internal bool IsRunning => Volatile.Read(ref isRunning) != 0;

    internal async Task<AudioDeviceTestResult> TryRunAsync(AudioDeviceTestRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (Interlocked.Exchange(ref isRunning, 1) != 0)
        {
            return null;
        }

        try
        {
            playbackPort.StopPlayback();
            return await Task.Run(() => runtime.Run(request));
        }
        finally
        {
            Volatile.Write(ref isRunning, 0);
        }
    }
}

internal sealed class BassAudioDeviceTestRuntime : IAudioDeviceTestRuntime
{
    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly BassAudioSessionLease sessionLease = new();

    internal BassAudioDeviceTestRuntime(ApplicationPathSnapshot applicationPathSnapshot)
    {
        this.applicationPathSnapshot = applicationPathSnapshot ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
    }

    public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
    {
        if (!sessionLease.TryRelease(BassAudioPlayer.Free))
        {
            throw new InvalidOperationException(
                "A previous audio device test still owns native resources after cleanup failed.");
        }

        BassAudioPlayer.DeviceDescriptor descriptor = string.IsNullOrWhiteSpace(request.PlayerDevice)
            ? default
            : new BassAudioPlayer.DeviceDescriptor(request.PlayerDeviceName, request.PlayerDevice);
        BassAudioSession ownedSession = null;
        Exception primaryException = null;
        try
        {
            BassAudioPlayer.Frequency = request.PlayerSampleRate;
            BassAudioPlayer.Format = request.PlayerFormat;
            BassAudioPlayer.DeviceVolume = Math.Min(100, Math.Max(0, request.PlayerVolume)) / 100f;
            descriptor = BassAudioPlayer.InitializeOwned(
                BassAudioMapping.ToBassDriver(request.PlayerDriver),
                descriptor,
                request.PlayerBufferSize,
                out ownedSession,
                request.PlayerWASAPIParam);
            sessionLease.Attach(ownedSession);
            using BassAudioOperationLease operation = BassNet.EnterAudioOperation();
            AudioDriver driver = BassAudioMapping.FromBassDriver(BassAudioPlayer.DriverType);

            if (request.PlaySound)
            {
                PlayTestSoundIfAvailable();
            }

            return new AudioDeviceTestResult(
                driver,
                descriptor.Driver,
                descriptor.Name,
                BassAudioPlayer.Frequency,
                BassAudioPlayer.Format,
                BassAudioPlayer.Latency);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            ReleaseTestSession(ownedSession, primaryException);
        }
    }

    private void ReleaseTestSession(BassAudioSession ownedSession, Exception primaryException)
    {
        try
        {
            if (ownedSession != null && sessionLease.Session == null)
            {
                sessionLease.Attach(ownedSession);
            }
            if (!sessionLease.TryRelease(BassAudioPlayer.Free) && primaryException == null)
            {
                throw new InvalidOperationException(
                    "The audio device test completed without confirming native cleanup.");
            }
        }
        catch (Exception cleanupException) when (primaryException != null)
        {
            try
            {
                NLogWrapper.GetLogger(nameof(BassAudioDeviceTestRuntime)).Warn(
                    "Audio device test cleanup failed while preserving the primary error: "
                    + cleanupException.Message);
            }
            catch
            {
                // Diagnostics must not replace the device-test exception.
            }
        }
    }

    private void PlayTestSoundIfAvailable()
    {
        string testSoundPath = applicationPathSnapshot.TestSoundPath;
        if (!File.Exists(testSoundPath))
        {
            return;
        }

        BassAudioPlayer bassAudioPlayer = null;
        try
        {
            bassAudioPlayer = new BassAudioPlayer(testSoundPath);
            bassAudioPlayer.Play();
            int pollCount = 0;
            while (bassAudioPlayer.PlayState != PlayState.Stopped || bassAudioPlayer.CurrentTime == TimeSpan.Zero)
            {
                Thread.Sleep(100);
                pollCount++;
                if (pollCount == 100)
                {
                    throw new Exception("No response from sound device");
                }
            }
        }
        catch (Exception value)
        {
            NLogWrapper.TraceLogger.Warn(value);
        }
        finally
        {
            bassAudioPlayer?.Dispose();
        }
    }
}
