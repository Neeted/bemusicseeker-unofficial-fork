using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioDeviceTestWorkflowOwnerTests
{
    [TestMethod]
    public async Task TryRunAsync_StopsPlaybackBeforeRuntimeAndRejectsDuplicate()
    {
        var events = new List<string>();
        using var runtimeStarted = new ManualResetEventSlim();
        using var releaseRuntime = new ManualResetEventSlim();
        var runtime = new RecordingAudioDeviceTestRuntime(() =>
        {
            events.Add("runtime");
            runtimeStarted.Set();
            releaseRuntime.Wait();
            return CreateResult();
        });
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => events.Add("stop")),
            runtime);
        AudioDeviceTestRequest request = CreateRequest();

        Task<AudioDeviceTestResult> firstTask = owner.TryRunAsync(request);

        Assert.IsTrue(runtimeStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(owner.IsRunning);
        Assert.IsNull(await owner.TryRunAsync(request));

        releaseRuntime.Set();
        Assert.IsNotNull(await firstTask);
        Assert.IsFalse(owner.IsRunning);
        CollectionAssert.AreEqual(new[] { "stop", "runtime" }, events);
        Assert.AreEqual(1, runtime.CallCount);
    }

    [TestMethod]
    public async Task TryRunAsync_RuntimeFailureReleasesBusyState()
    {
        var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => { }),
            new RecordingAudioDeviceTestRuntime(() => throw new InvalidOperationException("audio test failed")));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.TryRunAsync(CreateRequest()));

        Assert.AreEqual("audio test failed", exception.Message);
        Assert.IsFalse(owner.IsRunning);
    }

    private static AudioDeviceTestRequest CreateRequest()
    {
        return new AudioDeviceTestRequest(
            AudioDriver.DirectSound,
            "driver",
            "Device",
            SampleRate.AUTO,
            SampleFormat.AUTO,
            10,
            false,
            50,
            playSound: false);
    }

    private static AudioDeviceTestResult CreateResult(
        AudioDriver driver = AudioDriver.DirectSound)
    {
        return new AudioDeviceTestResult(
            driver,
            "new-driver",
            "New Device",
            SampleRate.SAMPLE_RATE_44100Hz,
            SampleFormat.SAMPLE_INT_16BIT,
            12.5);
    }

    private sealed class DelegateAudioDeviceTestPlaybackPort : IAudioDeviceTestPlaybackPort
    {
        private readonly Action stopPlayback;

        internal DelegateAudioDeviceTestPlaybackPort(Action stopPlayback)
        {
            this.stopPlayback = stopPlayback;
        }

        public void StopPlayback()
        {
            stopPlayback();
        }
    }

    private sealed class RecordingAudioDeviceTestRuntime : IAudioDeviceTestRuntime
    {
        private readonly Func<AudioDeviceTestResult> run;

        internal RecordingAudioDeviceTestRuntime(Func<AudioDeviceTestResult> run)
        {
            this.run = run;
        }

        internal int CallCount { get; private set; }

        public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
        {
            CallCount++;
            return run();
        }
    }

}
