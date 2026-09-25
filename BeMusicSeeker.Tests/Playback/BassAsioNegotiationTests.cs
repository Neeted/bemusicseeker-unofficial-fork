using System;
using System.Collections.Generic;
using System.Linq;
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
        StringAssert.Contains(result.FallbackReason, "normalized");
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
        Assert.AreEqual((AsioSampleFormat)((int)AsioSampleFormat.Float | 0x100), native.SetFormats.Single());
        Assert.AreEqual(AsioSampleFormat.Float, native.GetChannelFormat());
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

        Assert.AreEqual("BASS_ASIO_ChannelSetFormat", exception.Stage);
        Assert.AreEqual(AsioSampleFormat.Float, native.SetFormats.Single());
        Assert.AreEqual(0, native.MixerHandleCreated);
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
        Assert.AreEqual(native.MixerHandle, session.CallbackPcmRenderer.Channel);
        Assert.AreEqual(48000, session.CallbackPcmRenderer.SampleRate);
        Assert.AreEqual(2, session.CallbackPcmRenderer.ChannelCount);
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
        SampleFormat requestedFormat) =>
        new(
            BassAudioPlayer.DeviceDriver.ASIO,
            default,
            requestedRate,
            requestedFormat,
            10f);

    private static BassAudioSession CreateSession() =>
        new(BassAudioPlayer.DeviceDriver.ASIO)
        {
            ActualBackend = BassAudioPlayer.DeviceDriver.ASIO
        };

    private sealed class RecordingAsioBoundary : IAsioNegotiationNativeBoundary
    {
        private AsioSampleFormat currentFormat = AsioSampleFormat.Unknown;

        internal HashSet<int> AcceptedRates { get; } =
            [11025, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000];

        internal HashSet<AsioSampleFormat> AcceptedFormats { get; } =
            [AsioSampleFormat.Float, AsioSampleFormat.Bit16];

        internal List<int> CheckedRates { get; } = [];

        internal List<int> SetRates { get; } = [];

        internal List<AsioSampleFormat> SetFormats { get; } = [];

        internal double CurrentRate { get; set; } = 48000;

        internal AsioSampleFormat EndpointNativeFormat { get; set; } = AsioSampleFormat.Float;

        internal bool GetEndpointNativeFormatResult { get; set; } = true;

        internal Errors EndpointNativeFormatError { get; set; } = Errors.Device;

        internal AsioSampleFormat? ReportedChannelFormat { get; set; }

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

        internal bool EnableOutputResult { get; set; } = true;

        internal bool JoinOutputResult { get; set; } = true;

        internal bool StartResult { get; set; } = true;

        internal Errors AsioError { get; set; } = Errors.SampleFormat;

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

        public double GetRate() => CurrentRate;

        public bool CheckRate(double rate)
        {
            int candidate = (int)rate;
            CheckedRates.Add(candidate);
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

            currentFormat = callbackFormat;
            return true;
        }

        public AsioSampleFormat GetChannelFormat() => ReportedChannelFormat ?? currentFormat;

        public bool TryGetEndpointNativeFormat(out AsioSampleFormat format, out Errors error)
        {
            format = EndpointNativeFormat;
            error = GetEndpointNativeFormatResult ? Errors.OK : EndpointNativeFormatError;
            return GetEndpointNativeFormatResult;
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
            StartObserver?.Invoke();
            return StartResult;
        }

        public int GetOutputLatency() => 480;

        public Errors GetCoreError() => Errors.OK;

        public Errors GetAsioError() => AsioError;
    }
}
