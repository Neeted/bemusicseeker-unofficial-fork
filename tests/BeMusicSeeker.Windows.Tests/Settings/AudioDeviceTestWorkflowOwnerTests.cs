using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using Ribbit.Media;
using Ribbit.Media.Audio;
using static BeMusicSeeker.TestSupport.Components.AudioDeviceWorkflowTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>音声テストの受付・解放と、再生進捗から成功／失敗を決める処理を検証します。</summary>
/// <remarks>共有BASS受付を実際に占有し、停止・初期化の未開始と解放後の再受付を確認するため非並列にします。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
public sealed class AudioDeviceTestWorkflowOwnerTests
{
    /// <summary>共有音声要求が受付中ならテスト・照会・初期化を開始せず、解放後の明示テストを受理することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task ProcessAudioRequestBusy_RejectsTestAndQueryBeforeStoppingOrInitializing()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(async () =>
        {
            Assert.IsTrue(BassAudioRuntime.TryEnterAudioRequest(out IDisposable admission));
            using (admission)
            {
                accepted.TrySetResult();
                await release.Task;
            }
        });
        int stops = 0;
        var runtime = new RecordingAudioDeviceTestRuntime(() => CreateResult());
        using var owner = new AudioDeviceTestWorkflowOwner(
            new DelegateAudioDeviceTestPlaybackPort(() => stops++), runtime);
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(accepted.Task, blocker, "audio-device.request-blocker");
            Assert.IsNull(await owner.TryRunAsync(CreateRequest()));
            Assert.AreEqual(0, stops);
            Assert.AreEqual(0, runtime.CallCount);
            AudioDeviceCapabilityResult query = BassAudioPlayer.QueryAudioDeviceCapabilities(CreateCapabilityRequest());
            Assert.AreEqual(AudioDeviceCapabilityStatus.Busy, query.Status);
            AudioDeviceTestRequest request = CreateRequest();
            AudioInitializationException busy = Assert.ThrowsExactly<AudioInitializationException>(() =>
                BassAudioPlayer.InitializeOwned(request.AudioOutputRequest, 50, out _));
            Assert.AreEqual("audio request busy", busy.Stage);
            Assert.AreEqual("BassAudioOperationGate", busy.NativeErrorSource);
            Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, busy.RequestedBackend);
            Assert.AreEqual("driver", busy.RequestedDevice.Driver);
            Assert.AreEqual("Device", busy.RequestedDevice.Name);
        }
        finally
        {
            release.TrySetResult();
            await blocker;
        }
        Assert.IsNotNull(await owner.TryRunAsync(CreateRequest()));
        Assert.AreEqual(1, stops);
        Assert.AreEqual(1, runtime.CallCount);
    }

}
