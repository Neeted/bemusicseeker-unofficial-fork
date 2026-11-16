using System.ComponentModel;
using System.Globalization;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>実設定VMと音声ownerを局所portsへ接続し、要求・表示・編集値と終端の契約を検証します。</summary>
/// <remarks>ResourceServiceの購読と文化圏を使うため直列化します。画面入力・本番起動・実機器の接続は保証しません。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingsDialogAudioTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>音声テストへ品質・thread の draft を渡し、進捗不成立でも保存済み音声値を保って有効な AUTO 要求の失敗表示を出すことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_NoStreamProgress_DoesNotChangeEditedAudioValues()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            int originalResamplingQuality = settings.PlayerResamplingQuality;
            int originalMixerThreadCount = settings.PlayerMixerThreadCount;
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            AudioDeviceTestRequest? capturedRequest = null;
            var runtime = new DelegateAudioDeviceTestRuntime(request =>
                {
                    capturedRequest = request;
                    return AudioDeviceTestResultFactory.CreateSuccessful(
                            request,
                            actualRate: SampleRate.SAMPLE_RATE_48000Hz,
                            engineFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                            endpointFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                            actualDeviceName: "Changed name",
                            latency: 21,
                            streamProgressSucceeded: false);
                });
            var catalog = new TestAudioDeviceCatalog();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                runtime,
                requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway, audioDeviceCatalog: catalog);
            dialog.PlayerResamplingQuality = 3;
            dialog.PlayerMixerThreadCount = 4;

            await dialog.RunAudioDeviceTestAsync();
            AssertAudioResultRelocalizesWithoutWork(dialog, settingsSession, runtime, catalog);


            Assert.AreEqual(3, capturedRequest?.SampleRateConversionQuality);
            Assert.AreEqual(4, capturedRequest?.PlayerMixerThreadCount);
            Assert.AreEqual(3, capturedRequest?.AudioOutputRequest.SampleRateConversionQuality);
            Assert.AreEqual(4, capturedRequest?.AudioOutputRequest.PlayerMixerThreadCount);
            Assert.AreEqual(3, settings.PlayerResamplingQuality);
            Assert.AreEqual(4, settings.PlayerMixerThreadCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, expectedResamplingQuality: 3);
            string status = dialog.AudioDeviceTestStatusMessage;
            StringAssert.Contains(status, Resources.AudioDeviceTestStreamProgressFailureReason);
            StringAssert.Contains(status, Resources.AudioDeviceCapabilityAuto);
            StringAssert.Contains(status, SampleFormat.AUTO.ToString());
            Assert.IsFalse(status.Contains(
                string.Format(Resources.AudioSampleRateOptionFormat, 44100),
                StringComparison.Ordinal));
            Assert.IsFalse(status.Contains(Resources.AudioSampleFormat16Bit, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>fr-FRでも要求と異なるbackend・機器・形式と遅延16.5を固定表記で表示し、精度と編集設定を保持して、fallbackによる利用者意図の消失を検出します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_BackendFallback_DoesNotChangeEditedAudioValues()
    {
        CultureInfo previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.Asio,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            const string fallbackReason =
                "Requested ASIO device identity was stale; a compatible name match was used. "
                + "Requested ASIO endpoint format SAMPLE_INT_16BIT differs from native endpoint format SAMPLE_INT_24BIT.";
            var runtime = new DelegateAudioDeviceTestRuntime(request =>
                    AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        actualBackend: AudioDriver.WasapiShared,
                        actualDevice: "fallback-device",
                        actualDeviceName: "Fallback device",
                        actualRate: SampleRate.SAMPLE_RATE_48000Hz,
                        engineFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                        endpointFormat: SampleFormat.SAMPLE_INT_24BIT,
                        endpointContainerBits: 24,
                        endpointEffectiveBits: 24,
                        latency: 16.5,
                        fallbackReason: fallbackReason));
            var catalog = new TestAudioDeviceCatalog();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                runtime,
                        requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway, audioDeviceCatalog: catalog);

            await dialog.RunAudioDeviceTestAsync();
            AssertAudioResultRelocalizesWithoutWork(dialog, settingsSession, runtime, catalog);


            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.Asio);
            string resultMessage = dialog.AudioDeviceTestStatusMessage;
            StringAssert.Contains(resultMessage, AudioDriverDisplayNames.Get(AudioDriver.Asio));
            StringAssert.Contains(resultMessage, "Requested device");
            StringAssert.Contains(resultMessage, string.Format(Resources.AudioSampleRateOptionFormat, 44100));
            StringAssert.Contains(resultMessage, SampleFormat.AUTO.ToString());
            StringAssert.Contains(resultMessage, AudioDriverDisplayNames.Get(AudioDriver.WasapiShared));
            StringAssert.Contains(resultMessage, "Fallback device");
            StringAssert.Contains(resultMessage, string.Format(Resources.AudioSampleRateOptionFormat, 48000));
            StringAssert.Contains(resultMessage, Resources.AudioSampleFormatFloat32);
            StringAssert.Contains(resultMessage, Resources.AudioSampleFormat24Bit);
            StringAssert.Contains(
                resultMessage,
                string.Format(Resources.AudioDeviceCapabilityPrecisionPairFormat, 24, 24));
            StringAssert.Contains(resultMessage, Resources.AudioDeviceTestFallbackReason);
            StringAssert.Contains(
                resultMessage,
                "16.5");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage ?? string.Empty, fallbackReason);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>音声ページの再表示だけで照会し、同じ表示中は重複せず、保存済み非対応値を表示に残して rate ごとの対応形式を選び直すことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceCapabilityQuery_AudioPageVisibilityRefreshesAndFiltersFormatsByRate()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        AudioDeviceTestWorkflowOwner? workflow = null;
        SettingsDialogViewModel? dialog = null;
        PropertyChangedEventHandler? appliedObserver = null;
        var firstReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releasedCount = 0;
        int queryCount = 0;
        Action observeReleased = () =>
        {
            if (Interlocked.Increment(ref releasedCount) == 1) { firstReleased.TrySetResult(); }
            else { secondReleased.TrySetResult(); }
        };
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiExclusive,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            AudioDeviceCapabilityRequest? observedRequest = null;
            var capabilityRuntime = new DelegateAudioDeviceCapabilityRuntime(request =>
            {
                observedRequest = request;
                Interlocked.Increment(ref queryCount);
                return new AudioDeviceCapabilityResult(
                    request.Backend,
                    request.DeviceIdentity,
                    request.DeviceName,
                    AudioDeviceCapabilityStatus.Available,
                    [SampleRate.SAMPLE_RATE_352800Hz, SampleRate.SAMPLE_RATE_384000Hz],
                    [
                        new(SampleRate.SAMPLE_RATE_352800Hz, SampleFormat.SAMPLE_FLOAT_32BIT, true),
                        new(SampleRate.SAMPLE_RATE_352800Hz, SampleFormat.SAMPLE_INT_16BIT, false),
                        new(SampleRate.SAMPLE_RATE_384000Hz, SampleFormat.SAMPLE_INT_16BIT, true)
                    ]);
            });
            var testRuntime = new DelegateAudioDeviceTestRuntime(request => AudioDeviceTestResultFactory.CreateSuccessful(request));
            workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                testRuntime,
                capabilityRuntime,
                requestAdmission: new TestAudioDeviceTestRequestAdmission());
            workflow.OperationReleased += observeReleased;
            dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway);
            var firstApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            appliedObserver = (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsDialogViewModel.PlayerSampleRateNames)
                    && dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_384000Hz))
                {
                    if (Volatile.Read(ref queryCount) == 1)
                    {
                        firstApplied.TrySetResult();
                    }
                    else
                    {
                        secondApplied.TrySetResult();
                    }
                }
            };
            dialog.PropertyChanged += appliedObserver;
            dialog.SetPresentationActive(active: true);
            Assert.AreEqual(0, queryCount);
            dialog.SetAudioSettingsPageVisible(visible: true);
            dialog.SetAudioSettingsPageVisible(visible: true);
            await firstApplied.Task;
            Assert.AreEqual(1, queryCount);
            await dialog.AudioSettingsCompletionTask;
            await dialog.RunAudioDeviceTestAsync();
            Assert.IsNotNull(dialog.AudioDeviceTestStatusMessage);
            System.Globalization.CultureInfo? previousLanguage = Resources.Culture;
            try
            {
                foreach (string language in new[] { "en-US", "ja-JP", "en-US" })
                {
                    ResourceService.Current.ChangeCulture(language);
                    Assert.AreEqual(1, queryCount);
                    Assert.AreEqual(0, settingsSession.SaveCount);
                    Assert.AreEqual(1, testRuntime.RunCount);
                    Assert.IsNotNull(dialog.AudioDeviceTestStatusMessage);
                    StringAssert.Contains(dialog.PlayerSampleRateNames[SampleRate.SAMPLE_RATE_44100Hz], Resources.AudioDeviceCapabilityUnsupportedChoice);
                }
            }
            finally
            {
                if (previousLanguage == null) { Resources.Culture = null; }
                else { ResourceService.Current.ChangeCulture(previousLanguage.Name); }
            }
            string retainedResult = dialog.AudioDeviceTestStatusMessage;
            dialog.SetAudioSettingsPageVisible(visible: false);
            Assert.AreEqual(retainedResult, dialog.AudioDeviceTestStatusMessage);
            Assert.IsNull(dialog.AudioDeviceCapabilityStatusMessage);
            dialog.SetAudioSettingsPageVisible(visible: true);
            await secondApplied.Task;
            Assert.AreEqual(2, queryCount);
            Assert.AreEqual(retainedResult, dialog.AudioDeviceTestStatusMessage);
            Assert.AreEqual(1, testRuntime.RunCount);

            AudioDeviceCapabilityRequest capturedRequest = observedRequest
                ?? throw new AssertFailedException("Capability query did not reach its runtime.");
            Assert.AreEqual(AudioDriver.WasapiExclusive, capturedRequest.Backend);
            Assert.AreEqual("requested-device", capturedRequest.DeviceIdentity);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, capturedRequest.SavedRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, capturedRequest.SavedFormat);
            Assert.IsNull(dialog.AudioDeviceCapabilityStatusMessage);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
            Assert.IsTrue(dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_384000Hz));
            Assert.IsTrue(dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_352800Hz));
            StringAssert.Contains(
                dialog.PlayerSampleRateNames[SampleRate.SAMPLE_RATE_44100Hz],
                Resources.AudioDeviceCapabilityUnsupportedChoice);
            Assert.IsTrue(dialog.PlayerFormatNames.ContainsKey(SampleFormat.SAMPLE_INT_16BIT));
            StringAssert.Contains(
                dialog.PlayerFormatNames[SampleFormat.SAMPLE_INT_16BIT],
                Resources.AudioDeviceCapabilityUnsupportedChoice);
            Assert.IsFalse(dialog.EncoderSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_352800Hz));
            Assert.IsTrue(dialog.EncoderFormatNames.ContainsKey(SampleFormat.SAMPLE_INT_8BIT));

            dialog.PlayerSampleRate = SampleRate.SAMPLE_RATE_384000Hz;
            Assert.AreEqual(SampleFormat.AUTO, dialog.PlayerFormat);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
            CollectionAssert.AreEquivalent(
                new[] { SampleFormat.AUTO, SampleFormat.SAMPLE_INT_16BIT },
                dialog.PlayerFormatNames.Keys.ToArray());

            dialog.SetPresentationActive(active: false);
            Assert.IsNull(dialog.AudioDeviceCapabilityStatusMessage);
            Assert.IsNull(dialog.AudioDeviceCapabilityDiagnosticMessage);
            Assert.AreEqual(Resources.AudioDeviceCapabilityNotQueried,
                dialog.AudioDeviceCapabilityFormatDescription);
        }
        finally
        {
            // Dispose が次の照会と表示を止める。OperationReleased は runtime 資源解放であり、表示 wrapper の Task 終端ではない。
            dialog?.Dispose();
            if (queryCount > 0) { await firstReleased.Task; }
            if (queryCount > 1) { await secondReleased.Task; }
            if (dialog != null) { await dialog.AudioSettingsCompletionTask; }
            if (dialog != null && appliedObserver != null) { dialog.PropertyChanged -= appliedObserver; }
            if (workflow != null) { workflow.OperationReleased -= observeReleased; }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>照会保留中の二回のbackend変更は途中選択の照会を始めず、解放後に最終選択への照会を一回追加して最終backend選択を保持することを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceCapabilityQuery_SelectionChangesDuringQueryRunsOnlyFinalSelection()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        AudioDeviceTestWorkflowOwner? workflow = null;
        SettingsDialogViewModel? dialog = null;
        PropertyChangedEventHandler? appliedObserver = null;
        var firstReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releasedCount = 0;
        Action observeReleased = () =>
        {
            if (Interlocked.Increment(ref releasedCount) == 1) { firstReleased.TrySetResult(); }
            else { secondReleased.TrySetResult(); }
        };
        var firstQueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalQueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalQueryApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedBackends = new List<AudioDriver>();
        object requestedBackendsSync = new();
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiExclusive,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var capabilityRuntime = new DelegateAudioDeviceCapabilityRuntime(request =>
            {
                int queryOrdinal;
                lock (requestedBackendsSync)
                {
                    requestedBackends.Add(request.Backend);
                    queryOrdinal = requestedBackends.Count;
                }
                if (queryOrdinal == 1)
                {
                    firstQueryStarted.TrySetResult();
                    releaseFirstQuery.Task.GetAwaiter().GetResult();
                }
                else
                {
                    finalQueryStarted.TrySetResult();
                }
                return new AudioDeviceCapabilityResult(
                    request.Backend,
                    request.DeviceIdentity,
                    request.DeviceName,
                    AudioDeviceCapabilityStatus.Available,
                    [SampleRate.SAMPLE_RATE_384000Hz]);
            });
            workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(_ => throw new InvalidOperationException("test runtime unused")),
                capabilityRuntime,
                requestAdmission: new TestAudioDeviceTestRequestAdmission());
            workflow.OperationReleased += observeReleased;
            dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway);
            appliedObserver = (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsDialogViewModel.PlayerSampleRateNames)
                    && dialog.PlayerSampleRateNames.ContainsKey(SampleRate.SAMPLE_RATE_384000Hz))
                {
                    finalQueryApplied.TrySetResult();
                }
            };
            dialog.PropertyChanged += appliedObserver;
            dialog.SetPresentationActive(active: true);
            dialog.SetAudioSettingsPageVisible(visible: true);
            await firstQueryStarted.Task;
            dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.WasapiShared);
            dialog.PlayerDriverIndex = AudioDriverPolicy.IndexOf(AudioDriver.Asio);
            lock (requestedBackendsSync)
            {
                CollectionAssert.AreEqual(
                    new[] { AudioDriver.WasapiExclusive },
                    requestedBackends);
            }
            releaseFirstQuery.TrySetResult();
            await finalQueryStarted.Task;
            await finalQueryApplied.Task;

            lock (requestedBackendsSync)
            {
                CollectionAssert.AreEqual(
                    new[] { AudioDriver.WasapiExclusive, AudioDriver.Asio },
                    requestedBackends);
            }
            Assert.AreEqual(AudioDriverPolicy.IndexOf(AudioDriver.Asio), dialog.PlayerDriverIndex);
        }
        finally
        {
            // Dispose が次の照会と表示を止める。OperationReleased は runtime 資源解放であり、表示 wrapper の Task 終端ではない。
            dialog?.Dispose();
            releaseFirstQuery.TrySetResult();
            if (firstQueryStarted.Task.IsCompleted) { await firstReleased.Task; }
            if (finalQueryStarted.Task.IsCompleted) { await secondReleased.Task; }
            if (dialog != null) { await dialog.AudioSettingsCompletionTask; }
            if (dialog != null && appliedObserver != null) { dialog.PropertyChanged -= appliedObserver; }
            if (workflow != null) { workflow.OperationReleased -= observeReleased; }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>既定機器・AUTOの意図を成功後と言語変更後も保ち、閉鎖時の結果破棄と、旧Taskが再表示前後のどちらで完了しても結果を持ち越さないことを実Taskで確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AudioDeviceTest_DefaultAndAutoSuccess_PreservesUserIntent(bool reopenBeforeCompletion)
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        using var releaseSecond = new ManualResetEventSlim();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? pendingTest = null;
        int requests = 0;
        try
        {
            Settings settings = CreateAudioSettings(root);
            settings.PlayerDevice = null;
            settings.PlayerDeviceName = null;
            settings.PlayerSampleRate = SampleRate.AUTO;
            settings.PlayerFormat = SampleFormat.AUTO;
            settings.PlayerBufferSize = 10;
            settings.PlayerWASAPIParam = false;
            settings.uBMplayVolume = 50;
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway { PlayerDriver = AudioDriver.WasapiShared };
            var runtime = new DelegateAudioDeviceTestRuntime(request =>
            {
                if (Interlocked.Increment(ref requests) == 2)
                {
                    secondStarted.TrySetResult();
                    releaseSecond.Wait();
                }
                return AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        actualDevice: "resolved-default",
                        actualDeviceName: "Resolved default",
                        actualRate: SampleRate.SAMPLE_RATE_48000Hz,
                        engineFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                        endpointFormat: SampleFormat.SAMPLE_FLOAT_32BIT,
                        latency: 17);
            });
            var catalog = new TestAudioDeviceCatalog();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                runtime,
                        requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway, audioDeviceCatalog: catalog);

            System.Globalization.CultureInfo? previousLanguage = Resources.Culture;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                Assert.IsNull(dialog.AudioDeviceTestStatusMessage, "未実行の結果を言語変更で作りません。");
            }
            finally
            {
                if (previousLanguage == null) { Resources.Culture = null; }
                else { ResourceService.Current.ChangeCulture(previousLanguage.Name); }
            }
            await dialog.RunAudioDeviceTestAsync();
            AssertAudioResultRelocalizesWithoutWork(dialog, settingsSession, runtime, catalog);

            Assert.IsNull(settings.PlayerDevice);
            Assert.IsNull(settings.PlayerDeviceName);
            Assert.AreEqual(SampleRate.AUTO, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.AUTO, settings.PlayerFormat);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "WASAPI");
            dialog.SetAudioSettingsPageVisible(false);
            AssertAudioResultRelocalizesWithoutWork(dialog, settingsSession, runtime, catalog);
            dialog.SetPresentationActive(false);
            Assert.IsNull(dialog.AudioDeviceTestStatusMessage, "閉鎖時には結果を破棄します。");
            dialog.SetPresentationActive(true);
            Assert.IsNull(dialog.AudioDeviceTestStatusMessage);
            Assert.AreEqual(1, runtime.RunCount);
            pendingTest = dialog.RunAudioDeviceTestAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(secondStarted.Task, pendingTest, "audio test before hidden completion");
            dialog.SetPresentationActive(false);
            if (reopenBeforeCompletion) { dialog.SetPresentationActive(true); }
            Assert.IsFalse(pendingTest.IsCompleted, "閉鎖・再表示では受理済みTaskを終端させません。");
            Assert.IsTrue(dialog.IsAudioDeviceTestInProgress);
            Assert.IsFalse(dialog.IsAudioOutputSelectionEnabled);
            releaseSecond.Set();
            await pendingTest;
            Assert.IsNull(dialog.AudioDeviceTestStatusMessage, "閉鎖前に開始した結果は、完了時の表示状態によらず採用しません。");
            Assert.IsNull(dialog.AudioDeviceTestDiagnosticMessage);
            if (!reopenBeforeCompletion) { dialog.SetPresentationActive(true); }
            Assert.IsNull(dialog.AudioDeviceTestStatusMessage);
            Assert.AreEqual(2, runtime.RunCount);
        }
        finally
        {
            releaseSecond.Set();
            try { if (pendingTest != null) { await pendingTest; } }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>typed 初期化失敗を要求機器と試行機器の翻訳済み表示・native stage／error 診断へ分け、modal 通知なしで元設定を保つことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_KnownInitializationFailureShowsLocalizedDiagnosticWithoutChangingSettings()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new SettingsDialogHarness.RecordingDialogService();
            var runtime = new DelegateAudioDeviceTestRuntime(_ => throw new AudioInitializationException(
                    BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                    BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                    "BASS_WASAPI_Init",
                    new BassAudioPlayer.DeviceDescriptor("Requested device", "requested-device"),
                    new BassAudioPlayer.DeviceDescriptor("Attempted device B", "attempted-device-b"),
                    "BASSWASAPI",
                    Errors.Device,
                    "WASAPI initialization failed"));
            var catalog = new TestAudioDeviceCatalog();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                runtime,
                    requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway,
                dialogs, audioDeviceCatalog: catalog);

            await dialog.RunAudioDeviceTestAsync();
            AssertAudioResultRelocalizesWithoutWork(dialog, settingsSession, runtime, catalog);


            Assert.AreEqual(0, dialogs.MessageCount);
            string expectedMessage = string.Format(
                Resources.AudioDeviceTestInitializationErrorFormat,
                AudioDriverDisplayNames.Get(AudioDriver.WasapiShared),
                "Requested device",
                SampleRate.AUTO,
                SampleFormat.AUTO,
                AudioDriverDisplayNames.Get(AudioDriver.WasapiShared),
                "Attempted device B");
            Assert.AreEqual(expectedMessage, dialog.AudioDeviceTestStatusMessage);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_WASAPI_Init");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_DEVICE");
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.WasapiShared);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>初期化と cleanup の同時失敗を両方表示し、未取得の実値を捏造せず有効要求を保ち、例外の私有パスを表示・診断へ漏らさないことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_InitializationAndCleanupFailureKeepsEffectiveRequestWithoutActualValues()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            const string secretPath = @"C:\private\native\audio\device-state.bin";
            var primaryFailure = new AudioInitializationException(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                "BASS_WASAPI_Init",
                new BassAudioPlayer.DeviceDescriptor("Requested device", "requested-device"),
                default,
                "BASSWASAPI",
                Errors.Device,
                "WASAPI initialization failed");
            var cleanupFailure = new InvalidOperationException("Cleanup failed for " + secretPath);
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    new AudioDeviceTestResult(
                        request,
                        initialization: null,
                        streamProgressRequired: false,
                        streamProgressSucceeded: false,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        progressRatio: null,
                        failureReason: "Initialization failed before cleanup.",
                        failureKind: AudioDeviceTestFailureKind.Unexpected,
                        primaryFailure: primaryFailure,
                        cleanupFailure: cleanupFailure)),
                        requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            string expectedInitializationFailure = string.Format(
                Resources.AudioDeviceTestInitializationErrorFormat,
                AudioDriverDisplayNames.Get(AudioDriver.WasapiShared),
                "Requested device",
                SampleRate.AUTO,
                SampleFormat.AUTO,
                AudioDriverDisplayNames.Get(AudioDriver.WasapiShared),
                Resources.AudioDeviceDefault);
            Assert.AreEqual(
                Resources.AudioDeviceTestCleanupFailure + Environment.NewLine + expectedInitializationFailure,
                dialog.AudioDeviceTestStatusMessage);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_WASAPI_Init");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_DEVICE");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "cleanupFailure=InvalidOperationException");
            Assert.IsFalse(dialog.AudioDeviceTestStatusMessage.Contains(secretPath, StringComparison.Ordinal));
            Assert.IsFalse(dialog.AudioDeviceTestDiagnosticMessage.Contains(secretPath, StringComparison.Ordinal));
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.WasapiShared);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>実 native cleanup 境界の解放失敗は受付を隔離し、取得済み音声値と構造化 stage／error を保って例外の私有パスを漏らさないことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_NativeCleanupFailureKeepsNegotiatedValuesAndSafeStructuredDetails()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            const string secretPath = @"C:\private\native\audio\session-state.bin";
            var lifecycle = new BassAudioSessionLifecycle();
            var operationGate = new BassAudioOperationGate(initiallyOpen: true);
            var native = new AudioDeviceTestCleanupNativeBoundary("Failed at " + secretPath);

            bool ReleaseSession(BassAudioSession session)
            {
                if (session.IsReleased)
                {
                    return true;
                }
                if (!operationGate.TryEnterSessionCleanup(out BassAudioExclusiveLease exclusive))
                {
                    return false;
                }
                using (exclusive)
                using (lifecycle.Enter())
                {
                    bool released = BassAudioSessionCleanup.Release(session, native);
                    lifecycle.CompleteCleanup(session);
                    exclusive.Complete(released);
                    return released;
                }
            }

            var runtime = new BassAudioDeviceTestRuntime(
                ApplicationPathPolicy.Current,
                new AudioDeviceTestSoundCreationFailureBoundary(),
                (request, acquired) =>
                {
                    using BassAudioExclusiveLease exclusive = operationGate.EnterSessionInitialization();
                    exclusive.ObserveOwnership(() => !lifecycle.HasUnconfirmedOwnership);
                    using (lifecycle.Enter())
                    {
                        Assert.IsTrue(lifecycle.TryBegin(request.AudioOutputRequest, out BassAudioSession session));
                        acquired(session);
                        session.CoreInitialized = true;
                        session.CoreDeviceIndex = 0;
                        session.WasapiInitialized = true;
                        session.WasapiDeviceIndex = 0;
                        session.ActualBackend = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
                        session.ActualDevice = new BassAudioPlayer.DeviceDescriptor("Actual device", "actual-device");
                        session.MixerHandle = 201;
                        session.OutputHandle = 202;
                        session.NegotiationResult = new BassAudioBackendResult(
                            new BassAudioNegotiationRequest(
                                session.ActualBackend,
                                session.ActualDevice,
                                SampleRate.AUTO,
                                SampleFormat.AUTO,
                                10),
                            session.ActualDevice,
                            SampleRate.SAMPLE_RATE_48000Hz,
                            SampleFormat.SAMPLE_FLOAT_32BIT,
                            SampleFormat.SAMPLE_INT_24BIT,
                            16.5,
                            session.MixerHandle,
                            [],
                            null,
                            endpointContainerBits: 24,
                            endpointEffectiveBits: 24);
                        lifecycle.MarkActive(session);
                    }
                },
                ReleaseSession,
                operationGate);
            var workflow = new AudioDeviceTestWorkflowOwner(new NoOpAudioDeviceTestPlaybackPort(), runtime, requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(2, native.StreamFreeCount);
            Assert.IsTrue(lifecycle.HasCleanupPending);
            Assert.IsTrue(operationGate.IsCleanupQuarantined);
            Assert.AreEqual(Resources.AudioDeviceTestCleanupFailure,
                dialog.AudioDeviceTestStatusMessage.Split(Environment.NewLine)[0]);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "Actual device");
            StringAssert.Contains(
                dialog.AudioDeviceTestStatusMessage,
                string.Format(Resources.AudioSampleRateOptionFormat, (int)SampleRate.SAMPLE_RATE_48000Hz));
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, Resources.AudioSampleFormatFloat32);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, Resources.AudioSampleFormat24Bit);
            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, "16.5");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "cleanupFailure=InvalidOperationException");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "stage=BASS_StreamFree(");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "nativeErrorSource=BASS");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_UNKNOWN");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "exceptionType=InvalidOperationException");
            Assert.IsFalse(dialog.AudioDeviceTestStatusMessage.Contains(secretPath, StringComparison.Ordinal));
            Assert.IsFalse(dialog.AudioDeviceTestDiagnosticMessage.Contains(secretPath, StringComparison.Ordinal));
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway, AudioDriver.WasapiShared);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>MixerAttach の typed 失敗を局所表示と native stage／HANDLE 診断へ届け、modal 通知や保存音声値の変更を行わないことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_PlaybackStartFailureIsShownWithStageAndNativeError()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new SettingsDialogHarness.RecordingDialogService();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        streamProgressSucceeded: false,
                        failureKind: AudioDeviceTestFailureKind.PlaybackStartFailed,
                        playbackStage: BassAudioPlaybackStage.MixerAttach,
                        nativeErrorSource: "BASS_Mixer_StreamAddChannel",
                        nativeErrorCode: Errors.Handle)),
                        requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(Resources.AudioDeviceTestPlaybackFailureReason,
                dialog.AudioDeviceTestStatusMessage.Split(Environment.NewLine)[0]);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "MixerAttach");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_Mixer_StreamAddChannel");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_HANDLE");
            Assert.AreEqual(0, dialogs.MessageCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>SourceCreate の typed 失敗を生成失敗表示と FILEOPEN 診断へ届け、再生開始失敗と混同せず保存音声値を保つことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_PlayerCreationFailureIsShownWithStageAndNativeError()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new SettingsDialogHarness.RecordingDialogService();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(request =>
                    AudioDeviceTestResultFactory.CreateSuccessful(
                        request,
                        streamProgressSucceeded: false,
                        failureKind: AudioDeviceTestFailureKind.PlayerCreationFailed,
                        playbackStage: BassAudioPlaybackStage.SourceCreate,
                        nativeErrorSource: "BASS_StreamCreateFile",
                        nativeErrorCode: Errors.FileOpen)),
                        requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            StringAssert.Contains(dialog.AudioDeviceTestStatusMessage, Resources.AudioDeviceTestPlayerCreationFailureReason);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "SourceCreate");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_StreamCreateFile");
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "BASS_ERROR_FILEOPEN");
            Assert.AreEqual(0, dialogs.MessageCount);
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>予期しない runtime 例外を局所失敗表示と例外型の診断に変換し、modal 通知なしで音声設定を保つことを確認します。</summary>
    [TestMethod]
    public async Task AudioDeviceTest_UnexpectedRuntimeFailureShowsLocalDiagnosticWithoutDialog()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            Settings settings = CreateAudioSettings(root);
            ConfigureExplicitAudioSettings(settings);
            var settingsSession = new CountingAudioSettingsEditSession(settings);
            var audioGateway = new TestAudioSettingsGateway
            {
                OutputSelection = new AudioOutputSelection(
                    AudioDriver.WasapiShared,
                    settings.PlayerDevice,
                    settings.PlayerDeviceName)
            };
            var dialogs = new SettingsDialogHarness.RecordingDialogService();
            var workflow = new AudioDeviceTestWorkflowOwner(
                new NoOpAudioDeviceTestPlaybackPort(),
                new DelegateAudioDeviceTestRuntime(_ => throw new InvalidOperationException("unexpected test failure")),
                requestAdmission: new TestAudioDeviceTestRequestAdmission());
            using SettingsDialogViewModel dialog = CreateAudioDeviceTestDialog(
                settingsSession,
                workflow,
                audioGateway,
                dialogs);

            await dialog.RunAudioDeviceTestAsync();

            Assert.AreEqual(Resources.AudioDeviceTestUnexpectedFailureReason, dialog.AudioDeviceTestStatusMessage);
            Assert.AreEqual(0, dialogs.MessageCount);
            StringAssert.Contains(dialog.AudioDeviceTestDiagnosticMessage, "InvalidOperationException");
            AssertExplicitAudioSettingsUnchanged(settings, audioGateway);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Settings CreateAudioSettings(string root)
    {
        Settings values = TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config"));
        values.OperationModeLR2DB = false;
        return values;
    }

    private static string CreateTemporaryRoot(TestWorkspace workspace)
    {
        string root = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_SettingsDialogAudio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static SettingsDialogViewModel CreateAudioDeviceTestDialog(
        ISettingsEditSession settingsSession,
        AudioDeviceTestWorkflowOwner workflow,
        TestAudioSettingsGateway audioGateway,
        IUiDialogService? dialogs = null,
        IAudioDeviceCatalog? audioDeviceCatalog = null)
    {
        SettingsDialogViewModel dialog = SettingsDialogHarness.Create(settingsSession.Values,
            settingsEditSession: settingsSession, audioDeviceTestWorkflow: workflow,
            audioSettingsGateway: audioGateway, dialogs: dialogs, audioDeviceCatalog: audioDeviceCatalog).Dialog;
        dialog.SetPresentationActive(active: true);
        return dialog;
    }

    /// <summary>保持した成功・代替・失敗結果だけを現在語へ再投影し、実行・列挙・保存と候補を変えないことを確認します。</summary>
    private static void AssertAudioResultRelocalizesWithoutWork(SettingsDialogViewModel dialog,
        CountingAudioSettingsEditSession session, DelegateAudioDeviceTestRuntime runtime, TestAudioDeviceCatalog catalog)
    {
        System.Globalization.CultureInfo? previousLanguage = Resources.Culture;
        string diagnostic = dialog.AudioDeviceTestDiagnosticMessage;
        int saves = session.SaveCount;
        int runs = runtime.RunCount;
        int refreshes = catalog.RefreshCount;
        SampleRate rate = dialog.PlayerSampleRate;
        SampleFormat format = dialog.PlayerFormat;
        var notifications = new List<string>();
        PropertyChangedEventHandler changed = (_, args) => notifications.Add(args.PropertyName ?? string.Empty);
        dialog.PropertyChanged += changed;
        try
        {
            ResourceService.Current.ChangeCulture("en-US");
            string english = dialog.AudioDeviceTestStatusMessage;
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreNotEqual(english, dialog.AudioDeviceTestStatusMessage);
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(english, dialog.AudioDeviceTestStatusMessage);
            CollectionAssert.Contains(notifications, nameof(dialog.AudioDeviceTestStatusMessage));
            Assert.AreEqual(diagnostic, dialog.AudioDeviceTestDiagnosticMessage);
            Assert.AreEqual(rate, dialog.PlayerSampleRate);
            Assert.AreEqual(format, dialog.PlayerFormat);
            Assert.AreEqual(saves, session.SaveCount);
            Assert.AreEqual(runs, runtime.RunCount);
            Assert.AreEqual(refreshes, catalog.RefreshCount);
        }
        finally
        {
            dialog.PropertyChanged -= changed;
            if (previousLanguage == null) { Resources.Culture = null; }
            else { ResourceService.Current.ChangeCulture(previousLanguage.Name); }
        }
    }

    private static void ConfigureExplicitAudioSettings(Settings settings)
    {
        settings.PlayerDevice = "requested-device";
        settings.PlayerDeviceName = "Requested device";
        settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_44100Hz;
        settings.PlayerFormat = SampleFormat.SAMPLE_INT_16BIT;
        settings.PlayerBufferSize = 10;
        settings.PlayerResamplingQuality = 4;
        settings.PlayerWASAPIParam = false;
        settings.uBMplayVolume = 50;
    }

    private static void AssertExplicitAudioSettingsUnchanged(
        Settings settings,
        TestAudioSettingsGateway audioGateway,
        AudioDriver expectedDriver = AudioDriver.WasapiShared, int expectedResamplingQuality = 4)
    {
        Assert.AreEqual(expectedDriver, audioGateway.PlayerDriver);
        Assert.AreEqual("requested-device", audioGateway.OutputSelection.DeviceIdentity);
        Assert.AreEqual("Requested device", audioGateway.OutputSelection.DeviceName);
        Assert.AreEqual("requested-device", settings.PlayerDevice);
        Assert.AreEqual("Requested device", settings.PlayerDeviceName);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, settings.PlayerSampleRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
        Assert.AreEqual(expectedResamplingQuality, settings.PlayerResamplingQuality);
    }

}
