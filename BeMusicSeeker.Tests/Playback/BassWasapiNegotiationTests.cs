using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using ManagedBass;
using ManagedBass.Wasapi;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassWasapiNegotiationTests
{
    private static readonly WasapiProcedure WasapiCallback = (buffer, length, user) => length;

    /// <summary>排他のイベント・指定周期が失敗してnative既定で成功しても、試行順と両native原因を保持し指定並列数のfloat mixerへ接続する。</summary>
    [TestMethod]
    public void WasapiEventAndPeriodFailures_AreRetainedWhenNativeDefaultSucceeds()
    {
        var native = new RecordingWasapiBoundary();
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.Busy);
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.SampleFormat);
        native.InitializationResults.Enqueue(true);
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE, playerMixerThreadCount: 3),
            session,
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: true);

        Assert.AreEqual(3, native.InitializationCalls.Count);
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.Kind == WasapiInitializationKind.ExclusiveFormatted));
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.Flags.HasFlag(WasapiInitFlags.Exclusive)
            && call.Flags.HasFlag(WasapiInitFlags.AutoFormat)
            && call.Flags.HasFlag(WasapiInitFlags.Dither)));
        Assert.IsTrue(native.InitializationCalls[0].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.IsFalse(native.InitializationCalls[1].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.AreEqual(0f, native.InitializationCalls[2].BufferSeconds);
        Assert.AreEqual(0f, native.InitializationCalls[2].PeriodSeconds);
        Assert.AreEqual(3, result.Attempts.Count(attempt => attempt.Stage == "BASS_WASAPI_Init"));
        StringAssert.Contains(result.FallbackReason, "nativeErrorSource=BASSWASAPI");
        StringAssert.Contains(result.FallbackReason, "nativeErrorCode=BASS_ERROR_BUSY");
        StringAssert.Contains(result.FallbackReason, "nativeErrorCode=BASS_ERROR_FORMAT");
        StringAssert.Contains(result.FallbackReason, "native default");
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.Float));
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.MixerNonStop));
        Assert.AreEqual(3, native.MixerThreadCount);
        Assert.IsTrue(session.WasapiInitialized);
    }

    /// <summary>共有方式は選択機器のmix条件で開始し、GetInfoの実効rate・channel・形式からmixerとlatencyを構成して指定との差を報告する。</summary>
    [TestMethod]
    public void WasapiShared_UsesMixShapeAndGetInfoReadbackForResultMixerAndLatency()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "Shared Device",
                "shared-id",
                isDefault: true,
                mixRate: 48000,
                mixChannels: 6),
            WasapiInfo = new BassWasapiInfoSnapshot(96000, 4, WasapiFormat.Float, 7680)
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                SampleRate.SAMPLE_RATE_44100Hz),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        WasapiInitializationCall call = native.InitializationCalls.Single();
        Assert.AreEqual(48000, call.Rate);
        Assert.AreEqual(6, call.Channels);
        Assert.AreEqual(WasapiInitializationKind.SharedNoFormat, call.Kind);
        Assert.AreEqual(0, (int)call.Flags);
        Assert.IsFalse(call.Flags.HasFlag(WasapiInitFlags.Dither));
        Assert.AreEqual(0f, call.BufferSeconds);
        Assert.AreEqual(0f, call.PeriodSeconds);
        Assert.AreEqual(96000, native.MixerRate);
        Assert.AreEqual(4, native.MixerChannels);
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.Float));
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.MixerNonStop));
        Assert.AreEqual(SampleRate.SAMPLE_RATE_96000Hz, result.ActualRate);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.CallbackFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EndpointFormat);
        Assert.AreEqual(5d, result.LatencyMilliseconds, 0.0001d);
        StringAssert.Contains(result.FallbackReason, "endpoint mix rate 48000");
    }

    /// <summary>選択した共有機器のmix形状だけを照会し、形式・有効bit・チャンネルを返して再生初期化やgraph構築をしない。</summary>
    [TestMethod]
    public void WasapiSharedCapabilityQuery_ReportsTheSelectedMixShape()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "Shared Device",
                "shared-id",
                isDefault: true,
                mixRate: 96000,
                mixChannels: 6),
            CheckFormatResultProvider = (_, rate, channels, flags) =>
            {
                Assert.AreEqual(96000, rate);
                Assert.AreEqual(6, channels);
                Assert.AreEqual(WasapiInitFlags.Shared, flags);
                return ((int)WasapiFormat.Bit24, Errors.OK);
            }
        };
        AudioDeviceCapabilityRequest request = new(
            AudioDriver.WasapiShared,
            "shared-id",
            "Shared Device",
            SampleRate.SAMPLE_RATE_44100Hz,
            SampleFormat.SAMPLE_INT_16BIT);

        AudioDeviceCapabilityResult result = new BassWasapiNegotiator(native).QueryCapabilities(request);

        Assert.AreEqual(AudioDeviceCapabilityStatus.Available, result.Status);
        CollectionAssert.AreEqual(new[] { SampleRate.SAMPLE_RATE_96000Hz }, result.SupportedRates.ToArray());
        Assert.AreEqual(SampleFormat.SAMPLE_INT_24BIT, result.EndpointFormat);
        Assert.AreEqual(24, result.EndpointContainerBits);
        Assert.AreEqual(6, result.EndpointChannels);
        Assert.AreEqual(0, native.InitializationCalls.Count);
        Assert.AreEqual(0, native.GraphCalls.Count);
    }

    /// <summary>共有機器情報のread中に照会を取り消すと形式確認へ進まず、workflow受付を終端して解放を一回通知する。</summary>
    [TestMethod]
    public async Task WasapiSharedCapabilityQuery_CancellationDuringDeviceInfoReadSkipsFormatCheckAndReleasesWorkflow()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "Shared Device",
                "shared-id",
                isDefault: true,
                mixRate: 96000,
                mixChannels: 2)
        };
        AudioDeviceTestWorkflowOwner? owner = null;
        native.DeviceInfoObserver = () => owner!.CancelCurrentQuery();
        owner = new AudioDeviceTestWorkflowOwner(
            new NoOpAudioDeviceTestPlaybackPort(),
            new UnusedAudioDeviceTestRuntime(),
            new WasapiCapabilityRuntimeForTest(native));
        int releaseNotifications = 0;
        owner.OperationReleased += () => releaseNotifications++;

        Task<AudioDeviceCapabilityResult> queryTask = owner.TryQueryCapabilitiesAsync(
            new AudioDeviceCapabilityRequest(
                AudioDriver.WasapiShared,
                "shared-id",
                "Shared Device",
                SampleRate.SAMPLE_RATE_48000Hz,
                SampleFormat.AUTO));

        try
        {
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await queryTask);
        }
        finally
        {
            owner.CancelCurrentQuery();
            try
            {
                await queryTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.IsTrue(native.DeviceInfoObserved);
        Assert.AreEqual(0, native.CheckFormatCallCount);
        Assert.IsFalse(owner.IsRunning);
        Assert.AreEqual(1, releaseNotifications);
    }

    /// <summary>排他能力はrateと形式の完全一致だけを対応とし、mix・保存希望・高rate候補を照会して次回は最新mixを使う。</summary>
    [TestMethod]
    public void WasapiExclusiveCapabilityQuery_RecordsOnlyExactRateAndFormatReadbacks()
    {
        var checkedDevices = new List<int>();
        var checkedRates = new List<int>();
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "WASAPI Device",
                "wasapi-id",
                isDefault: true,
                mixRate: 23456),
            CheckFormatResultProvider = (deviceIndex, rate, channels, flags) =>
            {
                checkedDevices.Add(deviceIndex);
                checkedRates.Add(rate);
                Assert.AreEqual(2, channels);
                var requested = (WasapiFormat)(((int)flags >> 16) & 0xFF);
                return rate == 352800 && requested == WasapiFormat.Float
                    ? ((int)WasapiFormat.Bit16, Errors.OK)
                    : ((int)requested, Errors.OK);
            }
        };
        AudioDeviceCapabilityRequest request = new(
            AudioDriver.WasapiExclusive,
            "wasapi-id",
            "WASAPI Device",
            (SampleRate)12345,
            SampleFormat.SAMPLE_FLOAT_32BIT);

        AudioDeviceCapabilityResult result = new BassWasapiNegotiator(native).QueryCapabilities(request);

        Assert.AreEqual(AudioDeviceCapabilityStatus.Available, result.Status);
        Assert.IsTrue(result.FormatCapabilities.Any(capability =>
            capability.Rate == SampleRate.SAMPLE_RATE_352800Hz
            && capability.Format == SampleFormat.SAMPLE_FLOAT_32BIT
            && !capability.IsSupported));
        Assert.IsTrue(result.FormatCapabilities.Any(capability =>
            capability.Rate == SampleRate.SAMPLE_RATE_352800Hz
            && capability.Format == SampleFormat.SAMPLE_INT_16BIT
            && capability.IsSupported));
        Assert.IsTrue(checkedRates.Contains(23456));
        Assert.IsTrue(checkedRates.Contains(12345));
        Assert.IsTrue(checkedRates.Contains(352800));
        Assert.IsTrue(checkedRates.Contains(384000));
        Assert.IsTrue(checkedDevices.Count > 0);
        Assert.IsTrue(checkedDevices.All(index => index == 0));
        Assert.AreEqual(0, native.InitializationCalls.Count);
        Assert.AreEqual(0, native.GraphCalls.Count);

        checkedRates.Clear();
        native.DeviceInfo = CreateWasapiDevice(
            "WASAPI Device",
            "wasapi-id",
            isDefault: true,
            mixRate: 34567);
        AudioDeviceCapabilityResult nextResult = new BassWasapiNegotiator(native).QueryCapabilities(request);

        Assert.AreEqual(AudioDeviceCapabilityStatus.Available, nextResult.Status);
        Assert.IsTrue(checkedRates.Contains(34567));
        Assert.IsTrue(checkedRates.Contains(12345));
        Assert.IsFalse(checkedRates.Contains(23456));
    }

    /// <summary>照会先の欠落ではDefaultへ代替せず、native CheckFormat失敗では元段階・番号を保持して候補を成功扱いしない。</summary>
    [TestMethod]
    public void WasapiCapabilityQuery_FailsOnNativeCheckErrorAndDoesNotUseDefaultDeviceInstead()
    {
        int checkCount = 0;
        var native = new RecordingWasapiBoundary
        {
            CheckFormatResultProvider = (_, _, _, _) =>
            {
                checkCount++;
                return (-1, Errors.Device);
            }
        };
        AudioDeviceCapabilityRequest request = new(
            AudioDriver.WasapiExclusive,
            "missing-device-id",
            "Missing device",
            SampleRate.SAMPLE_RATE_352800Hz,
            SampleFormat.AUTO);

        AudioDeviceCapabilityResult missing = new BassWasapiNegotiator(native).QueryCapabilities(request);

        Assert.AreEqual(AudioDeviceCapabilityStatus.Failed, missing.Status);
        Assert.AreEqual("audio device selection", missing.FailureStage);
        Assert.AreEqual(0, checkCount);
        Assert.AreEqual(0, missing.SupportedRates.Count);

        request = new AudioDeviceCapabilityRequest(
            AudioDriver.WasapiExclusive,
            "wasapi-id",
            "WASAPI Device",
            SampleRate.SAMPLE_RATE_352800Hz,
            SampleFormat.AUTO);
        AudioDeviceCapabilityResult failed = new BassWasapiNegotiator(native).QueryCapabilities(request);

        Assert.AreEqual(AudioDeviceCapabilityStatus.Failed, failed.Status);
        Assert.AreEqual("BASS_WASAPI_CheckFormat", failed.FailureStage);
        Assert.AreEqual(Errors.Device, failed.NativeErrorCode);
        Assert.AreEqual(0, failed.SupportedRates.Count);
        Assert.AreEqual(1, checkCount);
    }

    /// <summary>排他方式自体のNotAvailableと個別形式の拒否を分け、後者だけは他の対応候補を照会して結果に残す。</summary>
    [TestMethod]
    public void WasapiExclusiveCapabilityQuery_ClassifiesModeAndCandidateRejections()
    {
        var unavailableNative = new RecordingWasapiBoundary
        {
            CheckFormatResultProvider = (_, _, _, _) => (-1, Errors.NotAvailable)
        };
        AudioDeviceCapabilityRequest request = new(
            AudioDriver.WasapiExclusive,
            "wasapi-id",
            "WASAPI Device",
            (SampleRate)12345,
            SampleFormat.SAMPLE_INT_16BIT);

        AudioDeviceCapabilityResult unavailable = new BassWasapiNegotiator(unavailableNative)
            .QueryCapabilities(request);

        Assert.AreEqual(AudioDeviceCapabilityStatus.Unsupported, unavailable.Status);
        Assert.AreEqual("BASS_WASAPI_CheckFormat", unavailable.FailureStage);
        Assert.AreEqual("BASSWASAPI/BASS_ErrorGetCode", unavailable.NativeErrorSource);
        Assert.AreEqual(Errors.NotAvailable, unavailable.NativeErrorCode);
        Assert.AreEqual(1, unavailable.Attempts.Count);
        Assert.AreEqual(Errors.NotAvailable, unavailable.Attempts[0].NativeErrorCode);

        int checkCount = 0;
        var candidateNative = new RecordingWasapiBoundary
        {
            CheckFormatResultProvider = (_, _, _, flags) =>
            {
                checkCount++;
                var requestedFormat = (WasapiFormat)(((int)flags >> 16) & 0xFF);
                return requestedFormat == WasapiFormat.Float
                    ? (-1, Errors.SampleFormat)
                    : ((int)requestedFormat, Errors.OK);
            }
        };

        AudioDeviceCapabilityResult candidateResult = new BassWasapiNegotiator(candidateNative)
            .QueryCapabilities(request);

        Assert.AreEqual(AudioDeviceCapabilityStatus.Available, candidateResult.Status);
        Assert.IsTrue(checkCount > 1);
        Assert.IsTrue(candidateResult.FormatCapabilities.Any(capability =>
            capability.Format == SampleFormat.SAMPLE_FLOAT_32BIT && !capability.IsSupported));
        Assert.IsTrue(candidateResult.FormatCapabilities.Any(capability =>
            capability.Format == SampleFormat.SAMPLE_INT_16BIT && capability.IsSupported));
    }

    /// <summary>共有開始で指定並列数1～4を設定・読戻しし、native Start時のcallback出力handleと初期化完了後の初期gain・rendererを確認する。</summary>
    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void WasapiShared_PublishesSelectedThreadCountFloatMixerAndInitialGainBeforeStart(int playerMixerThreadCount)
    {
        var native = new RecordingWasapiBoundary();
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        native.InitializationResults.Enqueue(true);
        native.StartObserver = () => Assert.AreEqual(123, session.CallbackOutputHandle);

        new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                playerMixerThreadCount: playerMixerThreadCount),
            session,
            WasapiCallback,
            initialGain: 0.35f,
            eventModeRequested: false);

        CollectionAssert.AreEqual(
            new[] { "CreateMixer", "SetMixerThreadCount", "GetMixerThreadCount", "StartWasapi" },
            native.GraphCalls);
        Assert.AreEqual(playerMixerThreadCount, native.MixerThreadCount);
        Assert.AreEqual(123, session.CallbackOutputHandle);
        AudioOutputProcessor outputProcessor = session.OutputProcessor
            ?? throw new AssertFailedException("WASAPI initialization did not create its output processor.");
        AudioPcmRenderer callbackRenderer = session.CallbackPcmRenderer
            ?? throw new AssertFailedException("WASAPI initialization did not create its callback renderer.");
        Assert.AreEqual(0.35d, outputProcessor.CurrentGain, 0.000001d);
        Assert.AreEqual(123, callbackRenderer.Channel);
        Assert.AreEqual(48000, callbackRenderer.SampleRate);
        Assert.AreEqual(2, callbackRenderer.ChannelCount);
    }

    /// <summary>並列数の設定失敗・読戻し失敗・不一致をそれぞれ初期化失敗へ伝え、未確認mixerを成功にしない。</summary>
    [DataTestMethod]
    [DataRow("set")]
    [DataRow("get")]
    [DataRow("mismatch")]
    public void MixerThreadConfigurationFailuresReachTheWasapiInitializationResult(string failure)
    {
        var native = new RecordingWasapiBoundary();
        if (failure == "set")
        {
            native.SetMixerThreadCountResult = false;
            native.MixerThreadError = Errors.Busy;
        }
        else if (failure == "get")
        {
            native.GetMixerThreadCountResult = false;
            native.MixerThreadError = Errors.Init;
        }
        else
        {
            native.ReportedMixerThreadCount = BassMixerThreadConfigurator.RealtimeDefaultThreadCount + 1;
        }

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                WasapiCallback,
                initialGain: 1f,
                eventModeRequested: false));

        Assert.AreEqual(
            failure == "set"
                ? "BASS_ChannelSetAttribute(BASS_ATTRIB_MIXER_THREADS)"
                : "BASS_ChannelGetAttribute(BASS_ATTRIB_MIXER_THREADS)",
            exception.Stage);
        Assert.AreEqual(
            failure == "set" ? Errors.Busy : failure == "get" ? Errors.Init : null,
            exception.NativeErrorCode);
        Assert.IsTrue(native.GraphCalls.Contains("SetMixerThreadCount"));
        Assert.AreEqual(failure == "set" ? 0 : 1, native.GraphCalls.Count(call => call == "GetMixerThreadCount"));
    }

    /// <summary>利用者の音量scaleを変えず、開始時のmuteだけを実効gainゼロへ反映する。</summary>
    [TestMethod]
    public void WasapiInitialGain_UsesMuteStateWithoutChangingVolumeScale()
    {
        Assert.AreEqual(
            0.35f,
            BassAudioPlayer.GetEffectiveDeviceVolumeForInitialization(0.35f, isMuted: false));
        Assert.AreEqual(
            0f,
            BassAudioPlayer.GetEffectiveDeviceVolumeForInitialization(0.35f, isMuted: true));
    }

    /// <summary>排他開始へ指定並列数と音量を渡し、整数endpointのdither flagを維持する。</summary>
    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void WasapiExclusive_UsesSelectedMixerThreadCountAndDoesNotApplySharedMixerGain(int playerMixerThreadCount)
    {
        var native = new RecordingWasapiBoundary();
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE);
        native.InitializationResults.Enqueue(true);

        new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
                playerMixerThreadCount: playerMixerThreadCount),
            session,
            WasapiCallback,
            initialGain: 0.2f,
            eventModeRequested: false);

        Assert.AreEqual(playerMixerThreadCount, native.MixerThreadCount);
        AudioOutputProcessor outputProcessor = session.OutputProcessor
            ?? throw new AssertFailedException("WASAPI initialization did not create its output processor.");
        Assert.AreEqual(0.2d, outputProcessor.CurrentGain, 0.000001d);
        Assert.IsTrue(native.InitializationCalls.Single().Flags.HasFlag(WasapiInitFlags.Dither));
    }

    /// <summary>WASAPI初期化の全失敗では未取得WASAPI所有を立てず、最後の境界失敗を元番号付きで返す。</summary>
    [TestMethod]
    public void WasapiFailedInit_DoesNotClaimWasapiOwnershipAndCapturesWasapiError()
    {
        var native = new RecordingWasapiBoundary();
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.Busy);
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.NotAvailable);
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                session,
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: true));

        Assert.IsTrue(session.CoreInitialized);
        Assert.IsFalse(session.WasapiInitialized);
        Assert.AreEqual("BASSWASAPI", exception.NativeErrorSource);
        Assert.AreEqual(Errors.NotAvailable, exception.NativeErrorCode);
        Assert.AreEqual("BASS_WASAPI_Init", exception.Stage);
    }

    /// <summary>WASAPI初期化成功直後に所有を捕捉し、後続GetInfoが失敗しても解放対象を失わない。</summary>
    [TestMethod]
    public void WasapiSuccessfulInit_ClaimsOwnershipBeforeGetInfoReadback()
    {
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        var native = new RecordingWasapiBoundary
        {
            GetInfoObserver = () => Assert.IsTrue(session.WasapiInitialized)
        };
        native.InitializationResults.Enqueue(true);

        new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            session,
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.IsTrue(native.GetInfoObserved);
    }

    /// <summary>GetInfo失敗では取得済みcore・WASAPI所有を保持し、読戻し段階と元native番号を返す。</summary>
    [TestMethod]
    public void WasapiInfoFailure_ReportsBoundaryErrorAfterClaimingOwnership()
    {
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);
        var native = new RecordingWasapiBoundary
        {
            GetWasapiInfoResult = false,
            WasapiInfoError = Errors.Init
        };
        native.InitializationResults.Enqueue(true);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                session,
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: false));

        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.WasapiInitialized);
        Assert.AreEqual("BASS_WASAPI_GetInfo", exception.Stage);
        Assert.AreEqual("BASSWASAPI", exception.NativeErrorSource);
        Assert.AreEqual(Errors.Init, exception.NativeErrorCode);
    }

    /// <summary>共有イベント失敗は通常再生だけ非イベントへ代替し、明示テストと非イベント要求では失敗として要求条件を守る。</summary>
    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public void WasapiShared_EventFailurePreservesThePurposeAndExplicitMode(bool deviceTest, bool eventMode)
    {
        AudioOutputPurpose purpose = deviceTest ? AudioOutputPurpose.DeviceTest : AudioOutputPurpose.Playback;
        var native = new RecordingWasapiBoundary();
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.Busy);
        native.InitializationResults.Enqueue(true);
        BassAudioSession session = CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

        BassAudioBackendResult Initialize() => new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, eventModeRequested: eventMode, purpose: purpose),
            session,
            WasapiCallback,
            initialGain: 0.35f,
            eventModeRequested: eventMode);

        if (purpose == AudioOutputPurpose.DeviceTest || !eventMode)
        {
            AudioInitializationException failure = Assert.ThrowsException<AudioInitializationException>(() => Initialize());
            Assert.AreEqual("BASS_WASAPI_Init", failure.Stage);
            Assert.AreEqual(eventMode, native.InitializationCalls.Single().Flags.HasFlag(WasapiInitFlags.EventDriven));
            Assert.AreEqual(0, native.MixerRate);
            return;
        }
        BassAudioBackendResult result = Initialize();
        Assert.AreEqual(2, native.InitializationCalls.Count);
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.Kind == WasapiInitializationKind.SharedNoFormat));
        Assert.IsTrue(native.InitializationCalls[0].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.IsFalse(native.InitializationCalls[1].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.IsTrue(native.InitializationCalls.All(call =>
            !call.Flags.HasFlag(WasapiInitFlags.Exclusive)
            && !call.Flags.HasFlag(WasapiInitFlags.AutoFormat)));
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.BufferSeconds == 0f && call.PeriodSeconds == 0f));
        AudioOutputProcessor outputProcessor = session.OutputProcessor
            ?? throw new AssertFailedException("WASAPI fallback did not create its output processor.");
        Assert.AreEqual(0.35d, outputProcessor.CurrentGain, 0.000001d);
        StringAssert.Contains(result.FallbackReason, "nativeErrorCode=BASS_ERROR_BUSY");
        StringAssert.Contains(result.FallbackReason, "shared non-event native default");
    }

    /// <summary>明示した機器をDefault flagの有無に依存せず選び、利用者の選択先を維持する。</summary>
    [TestMethod]
    public void WasapiExactSelection_DoesNotRequireDefaultFlag()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "Explicit WASAPI",
                "explicit-id")
        };
        native.InitializationResults.Enqueue(true);
        BassAudioNegotiationRequest request = CreateWasapiRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            device: new BassAudioPlayer.DeviceDescriptor("Explicit WASAPI", "explicit-id"));

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            request,
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("Explicit WASAPI", result.ActualDevice.Name);
    }

    /// <summary>安定IDが一致する共有機器では表示名の変更を追従し、別機器へのfallbackと誤診断しない。</summary>
    [TestMethod]
    public void WasapiStableIdentitySelection_IgnoresChangedDisplayName()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice(
                    "Renamed WASAPI",
                    "stable-id"),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    isDefault: true)
            ]
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                device: new BassAudioPlayer.DeviceDescriptor("Old WASAPI Name", "stable-id")),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("Renamed WASAPI", result.ActualDevice.Name);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.FallbackReason));
    }

    /// <summary>消えた共有機器IDは同名の別endpointへ置換せず、Defaultへの代替と欠落理由を結果に残す。</summary>
    [TestMethod]
    public void WasapiStaleStableIdentity_DoesNotSelectSameNameOnAnotherEndpoint()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice(
                    "Speakers",
                    "different-id"),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    isDefault: true)
            ]
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                device: new BassAudioPlayer.DeviceDescriptor("Speakers", "stale-id")),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(1, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("default-id", result.ActualDevice.Driver);
        StringAssert.Contains(result.FallbackReason, "unavailable");
    }

    /// <summary>IDを持たない旧共有設定だけは名前の互換解決を許し、選んだ実IDと互換理由を返す。</summary>
    [TestMethod]
    public void WasapiLegacyNameOnlySelection_UsesNameFallback()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice(
                    "Legacy Speakers",
                    "legacy-id"),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    isDefault: true)
            ]
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                device: new BassAudioPlayer.DeviceDescriptor("Legacy Speakers", null)),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("legacy-id", result.ActualDevice.Driver);
        StringAssert.Contains(result.FallbackReason, "no stable identity");
    }

    /// <summary>排他方式の旧選択はID欠落時にも名前一致の互換解決を維持し、置換先IDと理由を返す。</summary>
    [TestMethod]
    public void WasapiExclusive_StaleIdentityRetainsCompatibleNameFallback()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice(
                    "Exclusive Speakers",
                    "replacement-id"),
                CreateWasapiDevice(
                    "Default WASAPI",
                    "default-id",
                    isDefault: true)
            ]
        };
        native.InitializationResults.Enqueue(true);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            CreateWasapiRequest(
                BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
                device: new BassAudioPlayer.DeviceDescriptor("Exclusive Speakers", "stale-id")),
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: false);

        Assert.AreEqual(0, native.InitializationCalls.Single().DeviceIndex);
        Assert.AreEqual("replacement-id", result.ActualDevice.Driver);
        StringAssert.Contains(result.FallbackReason, "compatible name match");
    }

    /// <summary>Default要求にDefault機器がなければ方式・段階・境界付きで失敗し、任意機器を選ばない。</summary>
    [TestMethod]
    public void WasapiDefaultWithoutDefaultFlag_ThrowsContextualInitializationFailure()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice(
                "Non-default WASAPI",
                "non-default-id")
        };
        BassAudioNegotiationRequest request = CreateWasapiRequest(BassAudioPlayer.DeviceDriver.WASAPI_SHARED);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                request,
                CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_SHARED),
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: false));

        Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, exception.RequestedBackend);
        Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, exception.ActualBackend);
        Assert.AreEqual("BASS_WASAPI_GetDeviceInfos", exception.Stage);
        Assert.AreEqual("BASSWASAPI", exception.NativeErrorSource);
    }

    /// <summary>明示デバイステストの選択機器が消えた場合は同名・Defaultへ代替せずnative開始前に失敗する。</summary>
    [TestMethod]
    public void DeviceTest_UnavailableDeviceDoesNotSelectCompatibleNameOrDefault()
    {
        var native = new RecordingWasapiBoundary
        {
            DeviceInfos =
            [
                CreateWasapiDevice("Selected WASAPI", "replacement-id"),
                CreateWasapiDevice("Default WASAPI", "default-id", isDefault: true)
            ]
        };
        BassAudioNegotiationRequest request = CreateWasapiRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
            device: new BassAudioPlayer.DeviceDescriptor("Selected WASAPI", "stale-id"),
            purpose: AudioOutputPurpose.DeviceTest);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                request,
                CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: false));

        Assert.AreEqual("BASS_WASAPI_GetDeviceInfos", exception.Stage);
        Assert.AreEqual(0, native.InitializationCalls.Count);
    }

    /// <summary>明示デバイステストのrate読戻し不一致を失敗にし、要求rateを変えたりmixerを構築しない。</summary>
    [TestMethod]
    public void DeviceTest_ExplicitRateReadbackMismatchFailsBeforeCreatingMixer()
    {
        var native = new RecordingWasapiBoundary
        {
            WasapiInfo = new BassWasapiInfoSnapshot(48000, 2, WasapiFormat.Float, 3840)
        };
        native.InitializationResults.Enqueue(true);
        BassAudioNegotiationRequest request = CreateWasapiRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
            SampleRate.SAMPLE_RATE_44100Hz,
            purpose: AudioOutputPurpose.DeviceTest);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                request,
                CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: false));

        Assert.AreEqual("BASS_WASAPI_GetInfo", exception.Stage);
        Assert.AreEqual(44100, native.InitializationCalls.Single().Rate);
        Assert.AreEqual(0, native.MixerRate);
    }

    /// <summary>明示デバイステストの形式読戻し不一致を失敗にし、AutoFormatやmixerで不一致を隠さない。</summary>
    [TestMethod]
    public void DeviceTest_ExplicitFormatReadbackMismatchFailsBeforeCreatingMixer()
    {
        var native = new RecordingWasapiBoundary
        {
            WasapiInfo = new BassWasapiInfoSnapshot(48000, 2, WasapiFormat.Float, 3840)
        };
        native.InitializationResults.Enqueue(true);
        BassAudioNegotiationRequest request = CreateWasapiRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
            SampleRate.SAMPLE_RATE_48000Hz,
            format: SampleFormat.SAMPLE_INT_16BIT,
            purpose: AudioOutputPurpose.DeviceTest);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassWasapiNegotiator(native).Initialize(
                request,
                CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
                WasapiCallback,
                initialGain: 0.4f,
                eventModeRequested: false));

        Assert.AreEqual("BASS_WASAPI_GetInfo", exception.Stage);
        Assert.IsFalse(native.InitializationCalls.Single().Flags.HasFlag(WasapiInitFlags.AutoFormat));
        Assert.AreEqual(0, native.MixerRate);
    }

    /// <summary>排他の周期調整は用途とイベント要求を保持し、通常再生だけ許された非イベント／形式代替へ進んで原因を残す。</summary>
    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void WasapiExclusive_BufferAdjustmentPreservesPurposeAndExplicitMode(bool deviceTest, bool eventMode)
    {
        AudioOutputPurpose purpose = deviceTest ? AudioOutputPurpose.DeviceTest : AudioOutputPurpose.Playback;
        var native = new RecordingWasapiBoundary
        {
            WasapiInfo = new BassWasapiInfoSnapshot(48000, 2, WasapiFormat.Float, 3840)
        };
        native.InitializationResults.Enqueue(false);
        native.InitializationErrors.Enqueue(Errors.Busy);
        native.InitializationResults.Enqueue(true);
        BassAudioNegotiationRequest request = CreateWasapiRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
            SampleRate.SAMPLE_RATE_48000Hz,
            format: SampleFormat.SAMPLE_FLOAT_32BIT,
            eventModeRequested: eventMode,
            purpose: purpose);

        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            request,
            CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
            WasapiCallback,
            initialGain: 0.4f,
            eventModeRequested: eventMode);

        Assert.AreEqual(2, native.InitializationCalls.Count);
        Assert.AreEqual(eventMode, native.InitializationCalls[0].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.AreEqual(eventMode && purpose == AudioOutputPurpose.DeviceTest,
            native.InitializationCalls[1].Flags.HasFlag(WasapiInitFlags.EventDriven));
        Assert.IsTrue(native.InitializationCalls.All(call =>
            call.Flags.HasFlag(WasapiInitFlags.AutoFormat) == (purpose == AudioOutputPurpose.Playback)));
        Assert.AreEqual(48000, native.InitializationCalls[0].Rate);
        Assert.AreEqual(48000, native.InitializationCalls[1].Rate);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EndpointFormat);
        StringAssert.Contains(result.FallbackReason, "nativeErrorCode=BASS_ERROR_BUSY");
    }

    /// <summary>Auto rateと明示24bit形式は照会済みを前提とせず最初の完全一致rateで開始し、低精度readbackを対応として受け入れない。</summary>
    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void AutoRateWithExplicitFormat_InitializesTheFirstExactSupportedPairWithoutPriorQuery(bool deviceTest, bool lowerFormatReadback)
    {
        AudioOutputPurpose purpose = deviceTest ? AudioOutputPurpose.DeviceTest : AudioOutputPurpose.Playback;
        var checkedRates = new List<int>();
        var native = new RecordingWasapiBoundary
        {
            DeviceInfo = CreateWasapiDevice("WASAPI Device", "wasapi-id", isDefault: true, mixRate: 44100),
            WasapiInfo = new BassWasapiInfoSnapshot(48000, 2, WasapiFormat.Bit24, 2880),
            CheckFormatResultProvider = (_, rate, channels, flags) =>
            {
                checkedRates.Add(rate);
                Assert.AreEqual(2, channels);
                Assert.AreEqual(WasapiFormat.Bit24, (WasapiFormat)((int)flags >> 16));
                Assert.IsFalse(flags.HasFlag(WasapiInitFlags.AutoFormat));
                // HIWORDは下位形式を返し得る。44.1k/16bitを24bitの対応と扱わない。
                return rate == 44100 ? (lowerFormatReadback ? ((int)WasapiFormat.Bit16, Errors.OK) : (-1, Errors.SampleFormat))
                    : rate == 48000 ? ((int)WasapiFormat.Bit24, Errors.OK)
                    : (-1, Errors.SampleFormat);
            }
        };
        BassAudioNegotiationRequest request = CreateWasapiRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE, SampleRate.AUTO,
            format: SampleFormat.SAMPLE_INT_24BIT, purpose: purpose);
        BassAudioBackendResult result = new BassWasapiNegotiator(native).Initialize(
            request, CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
            WasapiCallback, initialGain: 0.4f, eventModeRequested: false);
        CollectionAssert.AreEqual(new[] { 44100, 48000 }, checkedRates);
        Assert.AreEqual(48000, native.InitializationCalls.Single().Rate);
        Assert.AreEqual(purpose == AudioOutputPurpose.Playback,
            native.InitializationCalls.Single().Flags.HasFlag(WasapiInitFlags.AutoFormat));
        Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, result.ActualRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_24BIT, result.EndpointFormat);
        Assert.AreEqual(SampleRate.AUTO, request.Rate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_24BIT, request.Format);
        Assert.IsNull(result.FallbackReason);
    }

    /// <summary>Auto rateに要求形式の対応がなければ明示テストは開始せず失敗し、通常再生だけ形式代替を理由付きで行う。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AutoRateWithUnavailableExplicitFormat_RespectsPurposeFallbackPolicy(bool deviceTest)
    {
        AudioOutputPurpose purpose = deviceTest ? AudioOutputPurpose.DeviceTest : AudioOutputPurpose.Playback;
        var native = new RecordingWasapiBoundary
        {
            WasapiInfo = new BassWasapiInfoSnapshot(48000, 2, WasapiFormat.Bit16, 1920),
            CheckFormatResultProvider = (_, _, _, _) => (-1, Errors.SampleFormat)
        };
        BassAudioNegotiationRequest request = CreateWasapiRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE, SampleRate.AUTO,
            format: SampleFormat.SAMPLE_INT_24BIT, purpose: purpose);
        BassAudioBackendResult Initialize() => new BassWasapiNegotiator(native).Initialize(
            request, CreateWasapiSession(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE),
            WasapiCallback, initialGain: 0.4f, eventModeRequested: false);
        if (purpose == AudioOutputPurpose.DeviceTest)
        {
            AudioInitializationException failure = Assert.ThrowsException<AudioInitializationException>(() => Initialize());
            Assert.AreEqual("BASS_WASAPI_CheckFormat", failure.Stage);
            Assert.AreEqual(0, native.InitializationCalls.Count);
            Assert.AreEqual(0, native.MixerRate);
        }
        else
        {
            BassAudioBackendResult result = Initialize();
            Assert.IsTrue(native.InitializationCalls.Single().Flags.HasFlag(WasapiInitFlags.AutoFormat));
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, result.EndpointFormat);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.FallbackReason));
        }
        Assert.AreEqual(SampleRate.AUTO, request.Rate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_24BIT, request.Format);
    }

    private static BassAudioNegotiationRequest CreateWasapiRequest(
        BassAudioPlayer.DeviceDriver backend,
        SampleRate rate = SampleRate.AUTO,
        BassAudioPlayer.DeviceDescriptor device = default,
        SampleFormat format = SampleFormat.AUTO,
        bool eventModeRequested = false,
        AudioOutputPurpose purpose = AudioOutputPurpose.Playback,
        int playerMixerThreadCount = BassMixerThreadConfigurator.RealtimeDefaultThreadCount) =>
        new(
            backend,
            device,
            rate,
            format,
            20f,
            eventModeRequested,
            purpose: purpose,
            playerMixerThreadCount: playerMixerThreadCount);

    private static BassAudioSession CreateWasapiSession(BassAudioPlayer.DeviceDriver backend) =>
        new(backend) { ActualBackend = backend };

    private static BassWasapiDeviceSnapshot CreateWasapiDevice(
        string name,
        string id,
        bool isDefault = false,
        int mixRate = 48000,
        int mixChannels = 2) =>
        new(
            name,
            id,
            isDefault,
            IsEnabled: true,
            IsInput: false,
            IsLoopback: false,
            IsUnplugged: false,
            MinimumUpdatePeriod: 0.005d,
            MixFrequency: mixRate,
            MixChannels: mixChannels);

    private sealed class NoOpAudioDeviceTestPlaybackPort : IAudioDeviceTestPlaybackPort
    {
        public Task StopPlayback()
        {
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedAudioDeviceTestRuntime : IAudioDeviceTestRuntime
    {
        public AudioDeviceTestResult Run(AudioDeviceTestRequest request)
            => throw new AssertFailedException("The capability query must not run a device test.");
    }

    private sealed class WasapiCapabilityRuntimeForTest : IAudioDeviceCapabilityRuntime
    {
        private readonly BassWasapiNegotiator negotiator;

        internal WasapiCapabilityRuntimeForTest(IWasapiNegotiationNativeBoundary native)
        {
            negotiator = new BassWasapiNegotiator(native);
        }

        public AudioDeviceCapabilityResult Query(
            AudioDeviceCapabilityRequest request,
            CancellationToken cancellationToken)
            => negotiator.QueryCapabilities(request, cancellationToken);
    }

    private sealed class RecordingWasapiBoundary : IWasapiNegotiationNativeBoundary
    {
        private Errors wasapiError;

        internal Queue<bool> InitializationResults { get; } = new();

        internal Queue<Errors> InitializationErrors { get; } = new();

        internal List<WasapiInitializationCall> InitializationCalls { get; } = [];

        internal List<string> GraphCalls { get; } = [];

        internal BassWasapiDeviceSnapshot DeviceInfo { get; set; } = CreateWasapiDevice(
            "WASAPI Device",
            "wasapi-id",
            isDefault: true);

        internal BassWasapiDeviceSnapshot[]? DeviceInfos { get; set; }

        internal bool GetDeviceInfosResult { get; set; } = true;

        internal Errors DeviceInfosError { get; set; } = Errors.Device;

        internal bool GetDeviceInfoResult { get; set; } = true;

        internal Errors DeviceInfoError { get; set; } = Errors.Device;

        internal Action? DeviceInfoObserver { get; set; }

        internal bool DeviceInfoObserved { get; private set; }

        internal int CheckFormatCallCount { get; private set; }

        internal BassWasapiInfoSnapshot WasapiInfo { get; set; } =
            new(48000, 2, WasapiFormat.Float, 3840);

        internal bool GetWasapiInfoResult { get; set; } = true;

        internal Errors WasapiInfoError { get; set; } = Errors.Init;

        internal Func<int, int, int, WasapiInitFlags, (int Format, Errors Error)>? CheckFormatResultProvider { get; set; }

        internal int MixerRate { get; private set; }

        internal int MixerChannels { get; private set; }

        internal BassFlags MixerFlags { get; private set; }

        internal int MixerThreadCount { get; private set; }

        internal float? ReportedMixerThreadCount { get; set; }

        internal bool SetMixerThreadCountResult { get; set; } = true;

        internal bool GetMixerThreadCountResult { get; set; } = true;

        internal Errors MixerThreadError { get; set; } = Errors.OK;

        internal Action? GetInfoObserver { get; set; }

        internal bool GetInfoObserved { get; private set; }

        internal Errors CoreError { get; set; } = Errors.OK;

        internal Action? StartObserver { get; set; }

        public bool InitializeCore() => true;

        public int GetCoreDevice() => 0;

        public bool SetMixerThreadCount(int mixerHandle, float threadCount)
        {
            GraphCalls.Add("SetMixerThreadCount");
            if (SetMixerThreadCountResult)
            {
                MixerThreadCount = (int)threadCount;
            }
            return SetMixerThreadCountResult;
        }

        public bool GetMixerThreadCount(int mixerHandle, out float threadCount)
        {
            GraphCalls.Add("GetMixerThreadCount");
            threadCount = ReportedMixerThreadCount ?? MixerThreadCount;
            return GetMixerThreadCountResult;
        }

        public Errors GetMixerThreadError() => MixerThreadError;

        public void DisableCoreUpdatePeriod()
        {
        }

        public bool TryGetDeviceInfos(
            out BassWasapiDeviceSnapshot[] deviceInfos,
            out Errors error)
        {
            deviceInfos = DeviceInfos ?? [DeviceInfo];
            error = GetDeviceInfosResult ? Errors.OK : DeviceInfosError;
            return GetDeviceInfosResult;
        }

        public bool TryGetDeviceInfo(
            int deviceIndex,
            out BassWasapiDeviceSnapshot deviceInfo,
            out Errors error)
        {
            DeviceInfoObserver?.Invoke();
            DeviceInfoObserved = true;
            deviceInfo = DeviceInfos == null ? DeviceInfo : DeviceInfos[deviceIndex];
            error = GetDeviceInfoResult ? Errors.OK : DeviceInfoError;
            return GetDeviceInfoResult;
        }

        public bool InitializeSharedWasapi(
            int deviceIndex,
            int rate,
            int channels,
            bool eventDriven,
            WasapiProcedure callback) =>
            RecordInitialization(
                WasapiInitializationKind.SharedNoFormat,
                deviceIndex,
                rate,
                channels,
                eventDriven ? WasapiInitFlags.EventDriven : WasapiInitFlags.Shared,
                0f,
                0f);

        public bool InitializeExclusiveWasapi(
            int deviceIndex,
            int rate,
            int channels,
            WasapiInitFlags flags,
            float bufferSeconds,
            float periodSeconds,
            WasapiProcedure callback) =>
            RecordInitialization(
                WasapiInitializationKind.ExclusiveFormatted,
                deviceIndex,
                rate,
                channels,
                flags,
                bufferSeconds,
                periodSeconds);

        private bool RecordInitialization(
            WasapiInitializationKind kind,
            int deviceIndex,
            int rate,
            int channels,
            WasapiInitFlags flags,
            float bufferSeconds,
            float periodSeconds)
        {
            InitializationCalls.Add(new WasapiInitializationCall(
                kind,
                deviceIndex,
                rate,
                channels,
                flags,
                bufferSeconds,
                periodSeconds));
            bool result = InitializationResults.Count == 0 || InitializationResults.Dequeue();
            wasapiError = result || InitializationErrors.Count == 0
                ? Errors.OK
                : InitializationErrors.Dequeue();
            return result;
        }

        public int GetWasapiDevice() => 0;

        public bool TryGetWasapiInfo(
            out BassWasapiInfoSnapshot info,
            out Errors error)
        {
            GetInfoObserver?.Invoke();
            GetInfoObserved = true;
            info = WasapiInfo;
            error = GetWasapiInfoResult ? Errors.OK : WasapiInfoError;
            return GetWasapiInfoResult;
        }

        public int CheckWasapiFormat(
            int deviceIndex,
            int rate,
            int channels,
            WasapiInitFlags flags,
            out Errors error)
        {
            CheckFormatCallCount++;
            (int format, Errors nativeError) = CheckFormatResultProvider?.Invoke(
                deviceIndex,
                rate,
                channels,
                flags)
                ?? ((int)WasapiFormat.Float, Errors.OK);
            error = format < 0 ? nativeError : Errors.OK;
            return format;
        }

        public int CreateMixer(int rate, int channels, BassFlags flags)
        {
            GraphCalls.Add("CreateMixer");
            MixerRate = rate;
            MixerChannels = channels;
            MixerFlags = flags;
            return 123;
        }

        public bool StartWasapi()
        {
            GraphCalls.Add("StartWasapi");
            StartObserver?.Invoke();
            return true;
        }

        public Errors GetCoreError() => CoreError;

        public Errors GetWasapiError() => wasapiError;
    }

    private enum WasapiInitializationKind
    {
        SharedNoFormat,
        ExclusiveFormatted
    }

    private sealed record WasapiInitializationCall(
        WasapiInitializationKind Kind,
        int DeviceIndex,
        int Rate,
        int Channels,
        WasapiInitFlags Flags,
        float BufferSeconds,
        float PeriodSeconds);


}
