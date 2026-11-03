using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

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

    public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
    {
        environment.Commit(Values);
        Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
        Save();
        Reload();
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

/// <summary>テストが指定した音声runtimeの結果または故障を、実音声ownerへ返します。</summary>
internal sealed class DelegateAudioDeviceTestRuntime : IAudioDeviceTestRuntime
{
    private readonly Func<AudioDeviceTestRequest, AudioDeviceTestResult> run;

    /// <summary>このcaseが定める要求処理を捕捉します。</summary>
    internal DelegateAudioDeviceTestRuntime(Func<AudioDeviceTestRequest, AudioDeviceTestResult> run)
    {
        this.run = run;
    }

    /// <summary>実音声ownerの要求を変換せず、指定した処理へ渡します。</summary>
    /// <summary>表示更新で実テストを再実行していないことを観測します。</summary>
    internal int RunCount { get; private set; }

    public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
    {
        RunCount++;
        return run(request);
    }
}
