using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using ManagedBass;
using ManagedBass.Asio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassAsioNegotiationTests
{
    private static readonly AsioProcedure Callback =
        (input, channel, buffer, length, user) => length;

    [TestMethod]
    public void Float32Available_UsesFloatMixerAndCallbackFormat()
    {
        var native = new RecordingAsioBoundary();

        BassAudioBackendResult result = Initialize(native, SampleRate.AUTO, SampleFormat.AUTO);

        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.CallbackFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EndpointFormat);
        Assert.AreEqual(10d, result.LatencyMilliseconds,
            "48 kHzで480 sampleは出力latency 10 msです。");
        Assert.AreEqual(AsioSampleFormat.Float, native.SetFormats.Single());
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.Float));
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.Decode));
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.MixerNonStop));
        Assert.AreEqual(Math.Min(4, Environment.ProcessorCount), native.MixerThreadCount);
        CollectionAssert.AreEqual(
            new[] { "set", "get" },
            native.MixerThreadCalls);
    }

    [DataTestMethod]
    [DataRow("set")]
    [DataRow("get")]
    [DataRow("mismatch")]
    public void MixerThreadConfigurationFailuresReachTheAsioInitializationResult(string failure)
    {
        var native = new RecordingAsioBoundary();
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
            native.ReportedMixerThreadCount = Math.Min(4, Environment.ProcessorCount) + 1;
        }

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => Initialize(native, SampleRate.AUTO, SampleFormat.AUTO));

        Assert.AreEqual(
            failure == "set"
                ? "BASS_ChannelSetAttribute(BASS_ATTRIB_MIXER_THREADS)"
                : "BASS_ChannelGetAttribute(BASS_ATTRIB_MIXER_THREADS)",
            exception.Stage);
        Assert.AreEqual(
            failure == "set" ? Errors.Busy : failure == "get" ? Errors.Init : null,
            exception.NativeErrorCode);
        Assert.IsTrue(native.MixerThreadCalls.Contains("set"));
        Assert.AreEqual(failure == "set" ? 0 : 1, native.MixerThreadCalls.Count(call => call == "get"));
    }

    [TestMethod]
    public void Float32Unavailable_FailsInsteadOfFallingBackToInteger()
    {
        var native = new RecordingAsioBoundary();
        native.AcceptedFormats.Remove(AsioSampleFormat.Float);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => Initialize(
                native,
                SampleRate.SAMPLE_RATE_48000Hz,
                SampleFormat.SAMPLE_FLOAT_32BIT));

        CollectionAssert.AreEqual(new[] { AsioSampleFormat.Float }, native.SetFormats);
        Assert.AreEqual("BASS_ASIO_ChannelSetFormat", exception.Stage);
        Assert.AreEqual(Errors.SampleFormat, exception.NativeErrorCode);
        Assert.AreEqual(0, native.MixerHandleCreated);
    }

    [DataTestMethod]
    [DataRow(SampleFormat.SAMPLE_INT_8BIT)]
    [DataRow(SampleFormat.SAMPLE_INT_24BIT)]
    [DataRow(SampleFormat.SAMPLE_INT_32BIT)]
    public void WideIntegerRequest_IsNormalizedWithoutCallbackByteWidthMismatch(
        SampleFormat requestedFormat)
    {
        var native = new RecordingAsioBoundary();

        BassAudioBackendResult result = Initialize(
            native,
            SampleRate.SAMPLE_RATE_44100Hz,
            requestedFormat);

        CollectionAssert.AreEqual(
                new[] { AsioSampleFormat.Float },
            native.SetFormats);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.CallbackFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EndpointFormat);
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.Float));
        StringAssert.Contains(result.FallbackReason, "Requested ASIO endpoint format");
    }

    [DataTestMethod]
    [DataRow(16, SampleFormat.SAMPLE_INT_16BIT, 16, 16)]
    [DataRow(17, SampleFormat.SAMPLE_INT_24BIT, 24, 24)]
    [DataRow(18, SampleFormat.SAMPLE_INT_32BIT, 32, 32)]
    [DataRow(24, SampleFormat.SAMPLE_INT_32BIT, 32, 16)]
    [DataRow(25, SampleFormat.SAMPLE_INT_32BIT, 32, 18)]
    [DataRow(26, SampleFormat.SAMPLE_INT_32BIT, 32, 20)]
    [DataRow(27, SampleFormat.SAMPLE_INT_32BIT, 32, 24)]
    public void IntegerNativeEndpoint_UsesFloatCallbackAndRecordsNativePrecision(
        int nativeFormatValue,
        SampleFormat expectedEndpointFormat,
        int expectedContainerBits,
        int expectedEffectiveBits)
    {
        var native = new RecordingAsioBoundary
        {
            EndpointNativeFormat = (AsioSampleFormat)nativeFormatValue
        };
        BassAudioSession session = CreateSession();

        BassAudioBackendResult result = new BassAsioNegotiator(native).Initialize(
            CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
            session,
            Callback);

        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.CallbackFormat);
        Assert.AreEqual(expectedEndpointFormat, result.EndpointFormat);
        Assert.AreEqual(2, result.ActualChannels);
        Assert.AreEqual(2, native.MixerChannels);
        var expectedRaw = (AsioSampleFormat)((int)AsioSampleFormat.Float | 0x100);
        Assert.AreEqual(expectedRaw, native.SetFormats.Single());
        Assert.AreEqual(expectedRaw, native.GetChannelFormat());
        StringAssert.Contains(
            result.Attempts.Single(attempt => attempt.Stage == "BASS_ASIO_ChannelGetFormat").Outcome,
            "requestedRaw=0x113 actualRaw=0x113");
        Assert.IsTrue(native.MixerFlags.HasFlag(BassFlags.Float));
        Assert.AreEqual(8, sizeof(float) * result.ActualChannels);
        StringAssert.Contains(
            result.Attempts.Single(attempt => attempt.Stage == "BASS_ASIO_ChannelGetInfo").Outcome,
            "nativeFormat=" + nativeFormatValue + " containerBits=" + expectedContainerBits
            + " effectiveBits=" + expectedEffectiveBits);
        Assert.IsTrue(string.IsNullOrEmpty(result.FallbackReason));
    }

    [DataTestMethod]
    [DataRow(32)]
    [DataRow(33)]
    [DataRow(-1)]
    [DataRow(123)]
    public void UnsupportedNativeEndpointFormat_FailsBeforeCreatingFloatMixer(int nativeFormatValue)
    {
        var native = new RecordingAsioBoundary
        {
            EndpointNativeFormat = (AsioSampleFormat)nativeFormatValue
        };
        BassAudioSession session = CreateSession();

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(
                CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
                session,
                Callback));

        Assert.AreEqual("BASS_ASIO_ChannelGetInfo", exception.Stage);
        Assert.AreEqual(0, native.MixerHandleCreated);
        Assert.AreEqual(0, native.SetFormats.Count);
        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.AsioInitialized);
    }

    [TestMethod]
    public void EndpointNativeFormatReadFailure_PreservesAcquiredAsioOwnership()
    {
        var native = new RecordingAsioBoundary
        {
            GetEndpointNativeFormatResult = false,
            EndpointNativeFormatError = Errors.Device
        };
        BassAudioSession session = CreateSession();

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(
                CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
                session,
                Callback));

        Assert.AreEqual("BASS_ASIO_ChannelGetInfo", exception.Stage);
        Assert.AreEqual("BASSASIO", exception.NativeErrorSource);
        Assert.AreEqual(Errors.Device, exception.NativeErrorCode);
        Assert.AreEqual(0, native.MixerHandleCreated);
        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.AsioInitialized);
    }

    [TestMethod]
    public void FloatCallbackFormatReadbackMismatch_FailsBeforeCreatingMixer()
    {
        var native = new RecordingAsioBoundary
        {
            ReportedChannelFormat = AsioSampleFormat.Bit16
        };

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => Initialize(native, SampleRate.AUTO, SampleFormat.AUTO));

        Assert.AreEqual("BASS_ASIO_ChannelGetFormat", exception.Stage);
        Assert.IsNull(exception.NativeErrorCode);
        Assert.AreEqual(AsioSampleFormat.Float, native.SetFormats.Single());
        Assert.AreEqual(0, native.MixerHandleCreated);
        StringAssert.Contains(
            exception.Attempts.Single(attempt => attempt.Stage == "BASS_ASIO_ChannelGetFormat").Outcome,
            "requestedRaw=0x13 actualRaw=0x10");
    }

    [DataTestMethod]
    [DataRow(19, 0x13)]
    [DataRow(16, 0x113)]
    public void FloatCallbackReadback_AcceptsOnlyTheSameRawValue(int endpointNativeFormat, int expectedRaw)
    {
        var native = new RecordingAsioBoundary
        {
            EndpointNativeFormat = (AsioSampleFormat)endpointNativeFormat
        };

        BassAudioBackendResult result = Initialize(native, SampleRate.AUTO, SampleFormat.AUTO);

        Assert.AreEqual((AsioSampleFormat)expectedRaw, native.SetFormats.Single());
        Assert.AreEqual((AsioSampleFormat)expectedRaw, native.GetChannelFormat());
        StringAssert.Contains(
            result.Attempts.Single(attempt => attempt.Stage == "BASS_ASIO_ChannelGetFormat").Outcome,
            "requestedRaw=0x" + expectedRaw.ToString("X")
            + " actualRaw=0x" + expectedRaw.ToString("X"));
    }

    [DataTestMethod]
    [DataRow(0x13, 16, 0x113)]
    [DataRow(0x113, 19, 0x13)]
    [DataRow(0x213, 16, 0x113)]
    [DataRow(0x110, 16, 0x113)]
    public void FloatCallbackReadback_RejectsMissingOrAdditionalRawFlags(
        int reportedRaw,
        int endpointNativeFormat,
        int requestedRaw)
    {
        var native = new RecordingAsioBoundary
        {
            EndpointNativeFormat = (AsioSampleFormat)endpointNativeFormat,
            ReportedChannelFormat = (AsioSampleFormat)reportedRaw
        };

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => Initialize(native, SampleRate.AUTO, SampleFormat.AUTO));

        Assert.AreEqual("BASS_ASIO_ChannelGetFormat", exception.Stage);
        Assert.IsNull(exception.NativeErrorCode);
        Assert.AreEqual(0, native.MixerHandleCreated);
        StringAssert.Contains(
            exception.Attempts.Single(attempt => attempt.Stage == "BASS_ASIO_ChannelGetFormat").Outcome,
            "requestedRaw=0x" + requestedRaw.ToString("X")
            + " actualRaw=0x" + reportedRaw.ToString("X"));
    }

    [TestMethod]
    public void FloatCallbackReadbackUnknown_ReportsGetFormatFailureAndNativeError()
    {
        var native = new RecordingAsioBoundary
        {
            ReportedChannelFormat = AsioSampleFormat.Unknown,
            ChannelFormatError = Errors.Init
        };

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => Initialize(native, SampleRate.AUTO, SampleFormat.AUTO));

        Assert.AreEqual("BASS_ASIO_ChannelGetFormat", exception.Stage);
        Assert.AreEqual(Errors.Init, exception.NativeErrorCode);
        StringAssert.Contains(
            exception.Attempts.Single(attempt => attempt.Stage == "BASS_ASIO_ChannelGetFormat").Outcome,
            "requestedRaw=0x13 actualRaw=0xFFFFFFFF");
    }

    [TestMethod]
    public void AutoRate_StartsWithDriverCurrentRateAndRemovesDuplicates()
    {
        var native = new RecordingAsioBoundary
        {
            CurrentRate = 44100
        };

        BassAudioBackendResult result = Initialize(native, SampleRate.AUTO, SampleFormat.AUTO);

        CollectionAssert.AreEqual(new[] { 44100 }, native.CheckedRates);
        CollectionAssert.AreEqual(new[] { 44100 }, native.SetRates);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, result.ActualRate);
    }

    [TestMethod]
    public void ExplicitRate_TriesRequestedThenCurrentThenStandardRatesInOrder()
    {
        var native = new RecordingAsioBoundary
        {
            CurrentRate = 44100
        };
        native.AcceptedRates.Clear();
        native.AcceptedRates.Add(48000);

        BassAudioBackendResult result = Initialize(
            native,
            SampleRate.SAMPLE_RATE_96000Hz,
            SampleFormat.AUTO);

        CollectionAssert.AreEqual(new[] { 96000, 44100, 48000 }, native.CheckedRates);
        CollectionAssert.AreEqual(new[] { 48000 }, native.SetRates);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, result.ActualRate);
        StringAssert.Contains(result.FallbackReason, "96000");
        StringAssert.Contains(result.FallbackReason, "48000");
        StringAssert.Contains(result.FallbackReason, "BASSASIO/BASS_ERROR_FORMAT");
    }

    [TestMethod]
    public void Playback_ExplicitRateReadbackMismatchRetainsNormalizationFallback()
    {
        var native = new RecordingAsioBoundary
        {
            CurrentRate = 48000,
            RateReadbackAfterSet = 44100
        };

        BassAudioBackendResult result = Initialize(
            native,
            SampleRate.SAMPLE_RATE_96000Hz,
            SampleFormat.AUTO);

        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, result.ActualRate);
        Assert.IsTrue(native.MixerHandleCreated != 0);
        Assert.AreEqual(1, native.StartCalls);
        StringAssert.Contains(result.FallbackReason, "normalized to 44100");
        Assert.AreEqual(
            "mismatched requestedRate=96000 actualRate=44100",
            result.Attempts.Single(attempt => attempt.Stage == "BASS_ASIO_GetRate").Outcome);
    }

    [TestMethod]
    public void DeviceTest_ExplicitRateReadbackMismatchFailsBeforeMixerAndReleasesPartialSession()
    {
        var native = new RecordingAsioBoundary
        {
            CurrentRate = 48000,
            RateReadbackAfterSet = 44100
        };
        BassAudioSession? acquiredSession = null;
        var request = new AudioDeviceTestRequest(
            AudioDriver.Asio,
            "asio.dll",
            "ASIO Device",
            SampleRate.SAMPLE_RATE_96000Hz,
            SampleFormat.SAMPLE_INT_16BIT,
            10f,
            playerWASAPIParam: false,
            playerVolume: 50,
            playSound: false);
        var runtime = new BassAudioDeviceTestRuntime(
            ApplicationPathPolicy.Current,
            new UnusedTestSoundBoundary(),
            (testRequest, acquired) =>
            {
                AudioOutputRequest output = testRequest.AudioOutputRequest;
                var negotiationRequest = new BassAudioNegotiationRequest(
                    BassAudioMapping.ToBassDriver(output.Backend),
                    new BassAudioPlayer.DeviceDescriptor(output.DeviceName, output.DeviceIdentity),
                    output.Rate,
                    output.Format,
                    output.BufferSize,
                    output.EventMode,
                    output.SampleRateConversionQuality,
                    output.Purpose);
                var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO)
                {
                    ActualBackend = BassAudioPlayer.DeviceDriver.ASIO
                };
                acquiredSession = session;
                acquired(session);
                session.NegotiationResult = new BassAsioNegotiator(native).Initialize(
                    negotiationRequest,
                    session,
                    Callback);
            },
            session => BassAudioSessionCleanup.Release(session, native),
            new BassAudioOperationGate(initiallyOpen: true));

        AudioDeviceTestResult result = runtime.Run(request);

        Assert.IsInstanceOfType<AudioInitializationException>(result.PrimaryFailure);
        var failure = (AudioInitializationException)result.PrimaryFailure!;
        Assert.AreEqual("BASS_ASIO_GetRate", failure.Stage);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_96000Hz, result.RequestedRate);
        CollectionAssert.AreEqual(new[] { 96000 }, native.CheckedRates);
        CollectionAssert.AreEqual(new[] { 96000 }, native.SetRates);
        StringAssert.Contains(
            failure.Attempts.Single(attempt => attempt.Stage == "BASS_ASIO_GetRate").Outcome,
            "mismatched requestedRate=96000 actualRate=44100");
        Assert.IsNull(result.Initialization);
        Assert.IsNull(result.ActualRate);
        Assert.IsNull(result.CleanupFailure);
        Assert.AreEqual(0, native.MixerHandleCreated);
        Assert.AreEqual(0, native.StartCalls);
        Assert.IsNotNull(acquiredSession);
        Assert.IsTrue(acquiredSession!.IsReleased);
        Assert.IsFalse(acquiredSession.AsioInitialized);
        Assert.IsFalse(acquiredSession.CoreInitialized);
        CollectionAssert.AreEqual(
            new[] { "SetAsioDevice", "FreeAsio", "SetCoreDevice", "FreeCore" },
            native.CleanupCalls);
    }

    [TestMethod]
    public void DeviceTest_ExplicitRateDoesNotTryCurrentOrStandardFallback()
    {
        var native = new RecordingAsioBoundary
        {
            CurrentRate = 44100
        };
        native.AcceptedRates.Clear();
        native.AcceptedRates.Add(48000);
        BassAudioNegotiationRequest request = CreateRequest(
            SampleRate.SAMPLE_RATE_96000Hz,
            SampleFormat.AUTO,
            AudioOutputPurpose.DeviceTest);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(request, CreateSession(), Callback));

        Assert.AreEqual("BASS_ASIO_SetRate", exception.Stage);
        CollectionAssert.AreEqual(new[] { 96000 }, native.CheckedRates);
        Assert.AreEqual(0, native.SetRates.Count);
        Assert.AreEqual(0, native.MixerHandleCreated);
    }

    [TestMethod]
    public void DeviceTest_ExplicitFormatMismatchFailsBeforeCreatingMixer()
    {
        var native = new RecordingAsioBoundary
        {
            EndpointNativeFormat = AsioSampleFormat.Float
        };
        BassAudioNegotiationRequest request = CreateRequest(
            SampleRate.AUTO,
            SampleFormat.SAMPLE_INT_16BIT,
            AudioOutputPurpose.DeviceTest);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(request, CreateSession(), Callback));

        Assert.AreEqual("BASS_ASIO_ChannelGetInfo", exception.Stage);
        Assert.AreEqual(0, native.MixerHandleCreated);
    }

    [TestMethod]
    public void DeviceTest_UnavailableDeviceDoesNotSelectNameMatchOrDefault()
    {
        var native = new RecordingAsioBoundary();
        var request = new BassAudioNegotiationRequest(
            BassAudioPlayer.DeviceDriver.ASIO,
            new BassAudioPlayer.DeviceDescriptor("ASIO Device", "missing-driver.dll"),
            SampleRate.AUTO,
            SampleFormat.AUTO,
            10f,
            purpose: AudioOutputPurpose.DeviceTest);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(request, CreateSession(), Callback));

        Assert.AreEqual("BASS_ASIO_GetDeviceInfos", exception.Stage);
        Assert.AreEqual(0, native.CheckedRates.Count);
        Assert.AreEqual(0, native.MixerHandleCreated);
    }

    [TestMethod]
    public void StaleDeviceIdentity_DefaultsWithinAsioAndRecordsIdentityLoss()
    {
        var native = new RecordingAsioBoundary();
        var request = new BassAudioNegotiationRequest(
            BassAudioPlayer.DeviceDriver.ASIO,
            new BassAudioPlayer.DeviceDescriptor("Disconnected ASIO", "missing.dll"),
            SampleRate.AUTO,
            SampleFormat.AUTO,
            10f);

        BassAudioBackendResult result = new BassAsioNegotiator(native).Initialize(
            request,
            CreateSession(),
            Callback);

        Assert.AreEqual("ASIO Device", result.ActualDevice.Name);
        StringAssert.Contains(result.FallbackReason, "unavailable");
        StringAssert.Contains(result.FallbackReason, "default");
    }

    [TestMethod]
    public void AsioFormatFailure_CapturesAsioErrorSourceAndRetainsAcquiredOwnership()
    {
        var native = new RecordingAsioBoundary
        {
            AsioError = Errors.SampleFormat
        };
        native.AcceptedFormats.Clear();
        BassAudioSession session = CreateSession();
        BassAudioNegotiationRequest request = CreateRequest(SampleRate.AUTO, SampleFormat.AUTO);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(request, session, Callback));

        Assert.AreEqual("BASSASIO", exception.NativeErrorSource);
        Assert.AreEqual(Errors.SampleFormat, exception.NativeErrorCode);
        Assert.AreEqual("BASS_ASIO_ChannelSetFormat", exception.Stage);
        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.AsioInitialized);
        Assert.AreEqual(0, session.MixerHandle);
    }

    [TestMethod]
    public void AsioDeviceInfoFailure_ReportsBoundaryErrorBeforeAsioOwnership()
    {
        var native = new RecordingAsioBoundary
        {
            GetDeviceInfosResult = false,
            DeviceInfosError = Errors.Device
        };
        BassAudioSession session = CreateSession();

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(
                CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
                session,
                Callback));

        Assert.AreEqual("BASS_ASIO_GetDeviceInfos", exception.Stage);
        Assert.AreEqual("BASSASIO", exception.NativeErrorSource);
        Assert.AreEqual(Errors.Device, exception.NativeErrorCode);
        Assert.IsTrue(session.CoreInitialized);
        Assert.IsFalse(session.AsioInitialized);
    }

    [TestMethod]
    public void ChannelSetupFailure_RecordsMixerBeforeFailureForSessionCleanup()
    {
        var native = new RecordingAsioBoundary
        {
            EnableOutputResult = false,
            AsioError = Errors.Unknown
        };
        BassAudioSession session = CreateSession();

        Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(
                CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
                session,
                Callback));

        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.AsioInitialized);
        Assert.AreEqual(native.MixerHandle, session.MixerHandle);
        Assert.AreEqual(native.MixerHandle, session.OutputHandle);
        Assert.AreEqual(native.MixerHandle, session.CallbackOutputHandle);
        Assert.IsFalse(session.IsStarted);
    }

    [TestMethod]
    public void AsioMixer_IsPublishedBeforeEnableJoinAndStart()
    {
        var native = new RecordingAsioBoundary();
        BassAudioSession session = CreateSession();
        native.EnableObserver = () => Assert.AreEqual(native.MixerHandle, session.CallbackOutputHandle);
        native.JoinObserver = () => Assert.AreEqual(native.MixerHandle, session.CallbackOutputHandle);
        native.StartObserver = () => Assert.AreEqual(native.MixerHandle, session.CallbackOutputHandle);

        new BassAsioNegotiator(native).Initialize(
            CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
            session,
            Callback);

        Assert.AreEqual(native.MixerHandle, session.CallbackOutputHandle);
        AudioPcmRenderer callbackRenderer = session.CallbackPcmRenderer
            ?? throw new AssertFailedException("ASIO initialization did not create its callback renderer.");
        Assert.AreEqual(native.MixerHandle, callbackRenderer.Channel);
        Assert.AreEqual(48000, callbackRenderer.SampleRate);
        Assert.AreEqual(2, callbackRenderer.ChannelCount);
        Assert.IsTrue(session.IsStarted);
    }

    [TestMethod]
    public void ChannelJoinFailure_RetainsPublishedMixerOwnership()
    {
        var native = new RecordingAsioBoundary
        {
            JoinOutputResult = false,
            AsioError = Errors.Unknown
        };
        BassAudioSession session = CreateSession();

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(
                CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
                session,
                Callback));

        Assert.AreEqual("BASS_ASIO_ChannelJoin", exception.Stage);
        Assert.AreEqual(native.MixerHandle, session.MixerHandle);
        Assert.AreEqual(native.MixerHandle, session.OutputHandle);
        Assert.AreEqual(native.MixerHandle, session.CallbackOutputHandle);
        Assert.IsFalse(session.IsStarted);
    }

    [TestMethod]
    public void StartFailure_RetainsPublishedMixerOwnership()
    {
        var native = new RecordingAsioBoundary
        {
            StartResult = false,
            AsioError = Errors.Unknown
        };
        BassAudioSession session = CreateSession();

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(
                CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
                session,
                Callback));

        Assert.AreEqual("BASS_ASIO_Start", exception.Stage);
        Assert.AreEqual(native.MixerHandle, session.MixerHandle);
        Assert.AreEqual(native.MixerHandle, session.OutputHandle);
        Assert.AreEqual(native.MixerHandle, session.CallbackOutputHandle);
        Assert.IsFalse(session.IsStarted);
    }

    [TestMethod]
    public void CapabilityQuery_CancellationStopsBeforeTheNextNativeRateCheck()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new RecordingAsioBoundary { OnCheckRate = cancellation.Cancel };
        BassAudioSession session = CreateSession();
        Assert.ThrowsException<OperationCanceledException>(() => new BassAsioNegotiator(native)
            .QueryCapabilities(CreateCapabilityRequest(), session, cancellation.Token));
        Assert.AreEqual(1, native.CheckedRates.Count);
        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.AsioInitialized);
        Assert.AreEqual(0, native.SetRates.Count);
        Assert.AreEqual(0, native.SetFormats.Count);
    }

    [TestMethod]
    public void CapabilityQuery_ReportsOnlyExplicitRatesAndNativeChannelFormats()
    {
        var native = new RecordingAsioBoundary
        {
            AsioError = Errors.NotAvailable,
            CurrentRate = 23456
        };
        native.AcceptedRates.Clear();
        native.AcceptedRates.UnionWith([12345, 23456, 352800, 384000]);
        var request = new AudioDeviceCapabilityRequest(
            AudioDriver.Asio,
            "asio.dll",
            "ASIO Device",
            (SampleRate)12345,
            SampleFormat.SAMPLE_INT_16BIT);

        BassAudioSession session = CreateSession();
        AudioDeviceCapabilityResult result = new BassAsioNegotiator(native)
            .QueryCapabilities(request, session);

        Assert.AreEqual(AudioDeviceCapabilityStatus.Available, result.Status);
        CollectionAssert.AreEquivalent(
            new[] { (SampleRate)12345, (SampleRate)23456, SampleRate.SAMPLE_RATE_352800Hz, SampleRate.SAMPLE_RATE_384000Hz },
            result.SupportedRates.ToArray());
        Assert.IsTrue(native.CheckedRates.Contains(12345));
        Assert.IsTrue(native.CheckedRates.Contains(23456));
        Assert.IsTrue(native.CheckedRates.Contains(352800));
        Assert.IsTrue(native.CheckedRates.Contains(384000));
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EndpointFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.RightEndpointFormat);
        Assert.AreEqual(2, result.EndpointChannels);
        Assert.AreEqual(0, native.SetRates.Count);
        Assert.AreEqual(0, native.SetFormats.Count);
        Assert.AreEqual(0, native.MixerHandleCreated);
        Assert.IsFalse(session.IsStarted);

        native = new RecordingAsioBoundary
        {
            AsioError = Errors.NotAvailable,
            CurrentRate = 34567
        };
        native.AcceptedRates.Clear();
        native.AcceptedRates.UnionWith([12345, 34567, 352800, 384000]);
        AudioDeviceCapabilityResult nextResult = new BassAsioNegotiator(native)
            .QueryCapabilities(request, CreateSession());

        Assert.AreEqual(AudioDeviceCapabilityStatus.Available, nextResult.Status);
        Assert.IsTrue(native.CheckedRates.Contains(34567));
        Assert.IsTrue(native.CheckedRates.Contains(12345));
        Assert.IsFalse(native.CheckedRates.Contains(23456));
    }

    [TestMethod]
    public void CapabilityQuery_DistinguishesUnsupportedRatesFromCheckRateFailure()
    {
        var native = new RecordingAsioBoundary { AsioError = Errors.NotAvailable };
        native.AcceptedRates.Clear();
        AudioDeviceCapabilityRequest request = CreateCapabilityRequest();

        AudioDeviceCapabilityResult unsupported = new BassAsioNegotiator(native)
            .QueryCapabilities(request, CreateSession());

        Assert.AreEqual(AudioDeviceCapabilityStatus.Unsupported, unsupported.Status);
        Assert.AreEqual(0, unsupported.SupportedRates.Count);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, unsupported.EndpointFormat);

        native = new RecordingAsioBoundary { AsioError = Errors.Device };
        native.AcceptedRates.Clear();
        AudioDeviceCapabilityResult failed = new BassAsioNegotiator(native)
            .QueryCapabilities(request, CreateSession());

        Assert.AreEqual(AudioDeviceCapabilityStatus.Failed, failed.Status);
        Assert.AreEqual("BASS_ASIO_CheckRate", failed.FailureStage);
        Assert.AreEqual(Errors.Device, failed.NativeErrorCode);
        Assert.AreEqual(0, failed.SupportedRates.Count);
        Assert.AreEqual(SampleFormat.UNKNOWN, failed.EndpointFormat);
    }

    [TestMethod]
    public void CapabilityQuery_DoesNotSubstituteAnUnavailableSelectedDevice()
    {
        var native = new RecordingAsioBoundary();
        AudioDeviceCapabilityRequest request = new(
            AudioDriver.Asio,
            "missing.dll",
            "Missing ASIO device",
            SampleRate.AUTO,
            SampleFormat.AUTO);

        AudioDeviceCapabilityResult result = new BassAsioNegotiator(native)
            .QueryCapabilities(request, CreateSession());

        Assert.AreEqual(AudioDeviceCapabilityStatus.Failed, result.Status);
        Assert.AreEqual("audio device selection", result.FailureStage);
        Assert.AreEqual(0, native.CheckedRates.Count);
        Assert.AreEqual(0, native.SetRates.Count);
        Assert.AreEqual(0, native.SetFormats.Count);
    }

    private static BassAudioBackendResult Initialize(
        RecordingAsioBoundary native,
        SampleRate requestedRate,
        SampleFormat requestedFormat)
    {
        return new BassAsioNegotiator(native).Initialize(
            CreateRequest(requestedRate, requestedFormat),
            CreateSession(),
            Callback);
    }

    private static BassAudioNegotiationRequest CreateRequest(
        SampleRate requestedRate,
        SampleFormat requestedFormat,
        AudioOutputPurpose purpose = AudioOutputPurpose.Playback) =>
        new(
            BassAudioPlayer.DeviceDriver.ASIO,
            default,
            requestedRate,
            requestedFormat,
            10f,
            purpose: purpose);

    private static BassAudioSession CreateSession() =>
        new(BassAudioPlayer.DeviceDriver.ASIO)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.ASIO
        };

    private static AudioDeviceCapabilityRequest CreateCapabilityRequest() =>
        new(
            AudioDriver.Asio,
            "asio.dll",
            "ASIO Device",
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_INT_16BIT);

    private sealed class RecordingAsioBoundary : IAsioNegotiationNativeBoundary, IAudioSessionNativeBoundary
    {
        private AsioSampleFormat currentFormat = AsioSampleFormat.Unknown;

        internal HashSet<int> AcceptedRates { get; } =
            [11025, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000];

        internal HashSet<AsioSampleFormat> AcceptedFormats { get; } =
            [AsioSampleFormat.Float, AsioSampleFormat.Bit16];

        internal List<int> CheckedRates { get; } = [];

        internal List<int> SetRates { get; } = [];

        internal List<AsioSampleFormat> SetFormats { get; } = [];

        internal double CurrentRate { get; set; } = 48000;

        internal double? RateReadbackAfterSet { get; set; }

        internal AsioSampleFormat EndpointNativeFormat { get; set; } = AsioSampleFormat.Float;

        internal bool GetEndpointNativeFormatResult { get; set; } = true;

        internal Errors EndpointNativeFormatError { get; set; } = Errors.Device;

        internal AsioSampleFormat? ReportedChannelFormat { get; set; }

        internal AsioSampleFormat RightEndpointNativeFormat { get; set; } = AsioSampleFormat.Float;

        internal Errors ChannelInfoError { get; set; } = Errors.Parameter;

        internal BassFlags MixerFlags { get; private set; }

        internal int MixerChannels { get; private set; }

        internal int MixerThreadCount { get; private set; }

        internal float? ReportedMixerThreadCount { get; set; }

        internal bool SetMixerThreadCountResult { get; set; } = true;

        internal bool GetMixerThreadCountResult { get; set; } = true;

        internal Errors MixerThreadError { get; set; } = Errors.OK;

        internal List<string> MixerThreadCalls { get; } = [];

        internal int MixerHandle { get; set; } = 123;

        internal int MixerHandleCreated { get; private set; }

        internal int StartCalls { get; private set; }

        internal List<string> CleanupCalls { get; } = [];

        internal bool EnableOutputResult { get; set; } = true;

        internal bool JoinOutputResult { get; set; } = true;

        internal bool StartResult { get; set; } = true;

        internal Errors AsioError { get; set; } = Errors.SampleFormat;

        internal Errors ChannelFormatError { get; set; } = Errors.Init;

        internal bool GetDeviceInfosResult { get; set; } = true;

        internal Errors DeviceInfosError { get; set; } = Errors.Device;

        internal Action? EnableObserver { get; set; }

        internal Action? JoinObserver { get; set; }

        internal Action? StartObserver { get; set; }

        public bool InitializeCore() => true;

        public int GetCoreDevice() => 4;

        public bool SetMixerThreadCount(int mixerHandle, float threadCount)
        {
            MixerThreadCalls.Add("set");
            if (SetMixerThreadCountResult)
            {
                MixerThreadCount = (int)threadCount;
            }
            return SetMixerThreadCountResult;
        }

        public bool GetMixerThreadCount(int mixerHandle, out float threadCount)
        {
            MixerThreadCalls.Add("get");
            threadCount = ReportedMixerThreadCount ?? MixerThreadCount;
            return GetMixerThreadCountResult;
        }

        public Errors GetMixerThreadError() => MixerThreadError;

        public void DisableCoreUpdatePeriod()
        {
        }

        public bool TryGetDeviceInfos(
            out BassAsioDeviceSnapshot[] deviceInfos,
            out Errors error)
        {
            deviceInfos = [new BassAsioDeviceSnapshot("ASIO Device", "asio.dll")];
            error = GetDeviceInfosResult ? Errors.OK : DeviceInfosError;
            return GetDeviceInfosResult;
        }

        public bool TryGetDeviceInfo(
            int deviceIndex,
            out BassAsioDeviceSnapshot deviceInfo,
            out Errors error)
        {
            deviceInfo = new BassAsioDeviceSnapshot("ASIO Device", "asio.dll");
            error = Errors.OK;
            return true;
        }

        public bool InitializeAsio(int deviceIndex) => true;

        public int GetAsioDevice() => 7;

        public double GetRate() => SetRates.Count == 0
            ? CurrentRate
            : RateReadbackAfterSet ?? CurrentRate;

        public bool TryGetRate(out double rate, out Errors error)
        {
            rate = CurrentRate;
            error = rate > 0d ? Errors.OK : Errors.SampleRate;
            return rate > 0d;
        }

        internal Action? OnCheckRate { get; set; }

        public bool CheckRate(double rate)
        {
            int candidate = (int)rate;
            CheckedRates.Add(candidate);
            OnCheckRate?.Invoke();
            return AcceptedRates.Contains(candidate);
        }

        public bool SetRate(double rate)
        {
            int accepted = (int)rate;
            SetRates.Add(accepted);
            CurrentRate = accepted;
            return true;
        }

        public bool SetChannelRate(double rate) => true;

        public bool SetChannelFormat(AsioSampleFormat format)
        {
            SetFormats.Add(format);
            var callbackFormat = (AsioSampleFormat)((int)format & ~0x100);
            if (!AcceptedFormats.Contains(callbackFormat))
            {
                return false;
            }

            currentFormat = format;
            return true;
        }

        public AsioSampleFormat GetChannelFormat() => ReportedChannelFormat ?? currentFormat;

        public bool TryGetChannelFormat(out AsioSampleFormat format, out Errors error)
        {
            format = GetChannelFormat();
            error = format == AsioSampleFormat.Unknown ? ChannelFormatError : Errors.OK;
            return format != AsioSampleFormat.Unknown;
        }

        public bool TryGetEndpointNativeFormat(out AsioSampleFormat format, out Errors error)
        {
            format = EndpointNativeFormat;
            error = GetEndpointNativeFormatResult ? Errors.OK : EndpointNativeFormatError;
            return GetEndpointNativeFormatResult;
        }

        public bool TryGetChannelNativeFormat(
            bool input,
            int channel,
            out AsioSampleFormat format,
            out Errors error)
        {
            format = channel == 0 ? EndpointNativeFormat : RightEndpointNativeFormat;
            error = channel <= 1 ? Errors.OK : ChannelInfoError;
            return channel is 0 or 1;
        }

        public int CreateMixer(int rate, int channels, BassFlags flags)
        {
            MixerFlags = flags;
            MixerChannels = channels;
            MixerHandleCreated = MixerHandle;
            return MixerHandle;
        }

        public bool EnableOutputChannel(AsioProcedure callback)
        {
            EnableObserver?.Invoke();
            return EnableOutputResult;
        }

        public bool JoinOutputChannel(int channel)
        {
            JoinObserver?.Invoke();
            return JoinOutputResult;
        }

        public bool Start(int bufferLength, int threads)
        {
            StartCalls++;
            StartObserver?.Invoke();
            return StartResult;
        }

        public int GetOutputLatency() => 480;

        public Errors GetCoreError() => Errors.OK;

        public Errors GetAsioError() => AsioError;

        public bool SetCoreDevice(int deviceIndex)
        {
            CleanupCalls.Add("SetCoreDevice");
            return true;
        }

        public bool FreeCore()
        {
            CleanupCalls.Add("FreeCore");
            return true;
        }

        public bool SetWasapiDevice(int deviceIndex)
        {
            CleanupCalls.Add("SetWasapiDevice");
            return true;
        }

        public bool StopWasapi(bool reset)
        {
            CleanupCalls.Add("StopWasapi");
            return true;
        }

        public bool FreeWasapi()
        {
            CleanupCalls.Add("FreeWasapi");
            return true;
        }

        public Errors GetWasapiError() => Errors.OK;

        public bool SetAsioDevice(int deviceIndex)
        {
            CleanupCalls.Add("SetAsioDevice");
            return true;
        }

        public bool StopAsio()
        {
            CleanupCalls.Add("StopAsio");
            return true;
        }

        public bool FreeAsio()
        {
            CleanupCalls.Add("FreeAsio");
            return true;
        }

        public bool FreeStream(int handle)
        {
            CleanupCalls.Add("FreeStream");
            return true;
        }

        public Errors GetStreamError() => Errors.OK;
    }

    private sealed class UnusedTestSoundBoundary : IAudioDeviceTestSoundBoundary
    {
        public bool FileExists(string path) => false;

        public IAudioPlayer CreatePlayer(string path)
            => throw new InvalidOperationException("This device test does not play its test sound.");

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp) => TimeSpan.Zero;

        public void Wait(TimeSpan interval)
            => throw new AssertFailedException("The test sound was not requested.");
    }
}
