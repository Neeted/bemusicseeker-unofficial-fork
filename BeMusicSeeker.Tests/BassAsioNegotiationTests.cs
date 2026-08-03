using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Un4seen.Bass;
using Un4seen.BassAsio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassAsioNegotiationTests
{
    private static readonly ASIOPROC Callback =
        (input, channel, buffer, length, user) => length;

    [TestMethod]
    public void Float32Available_UsesFloatMixerAndCallbackFormat()
    {
        var native = new RecordingAsioBoundary();

        BassAudioBackendResult result = Initialize(native, SampleRate.AUTO, SampleFormat.AUTO);

        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EndpointFormat);
        Assert.AreEqual(BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT, native.SetFormats.Single());
        Assert.IsTrue(native.MixerFlags.HasFlag(BASSFlag.BASS_SAMPLE_FLOAT));
        Assert.IsTrue(native.MixerFlags.HasFlag(BASSFlag.BASS_STREAM_DECODE));
        Assert.IsTrue(native.MixerFlags.HasFlag(BASSFlag.BASS_MIXER_NONSTOP));
    }

    [TestMethod]
    public void Float32Unavailable_RetriesWithInt16MixerAndCallbackFormat()
    {
        var native = new RecordingAsioBoundary();
        native.AcceptedFormats.Remove(BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT);

        BassAudioBackendResult result = Initialize(
            native,
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT);

        CollectionAssert.AreEqual(
            new[]
            {
                BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT,
                BASSASIOFormat.BASS_ASIO_FORMAT_16BIT
            },
            native.SetFormats);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, result.EndpointFormat);
        Assert.IsFalse(native.MixerFlags.HasFlag(BASSFlag.BASS_SAMPLE_FLOAT));
        StringAssert.Contains(result.FallbackReason, "Int16");
        StringAssert.Contains(result.FallbackReason, "nativeErrorSource=BASSASIO");
        StringAssert.Contains(result.FallbackReason, "nativeErrorCode=BASS_ERROR_FORMAT");
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
            new[] { BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT },
            native.SetFormats);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EndpointFormat);
        Assert.IsTrue(native.MixerFlags.HasFlag(BASSFlag.BASS_SAMPLE_FLOAT));
        StringAssert.Contains(result.FallbackReason, "normalized");
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
            AsioError = BASSError.BASS_ERROR_FORMAT
        };
        native.AcceptedFormats.Clear();
        var session = CreateSession();
        var request = CreateRequest(SampleRate.AUTO, SampleFormat.AUTO);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(request, session, Callback));

        Assert.AreEqual("BASSASIO", exception.NativeErrorSource);
        Assert.AreEqual(BASSError.BASS_ERROR_FORMAT, exception.NativeErrorCode);
        Assert.AreEqual("BASS_ASIO_ChannelSetFormat", exception.Stage);
        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.AsioInitialized);
        Assert.AreEqual(0, session.MixerHandle);
    }

    [TestMethod]
    public void ChannelSetupFailure_RecordsMixerBeforeFailureForSessionCleanup()
    {
        var native = new RecordingAsioBoundary
        {
            EnableOutputResult = false,
            AsioError = BASSError.BASS_ERROR_UNKNOWN
        };
        var session = CreateSession();

        Assert.ThrowsException<AudioInitializationException>(
            () => new BassAsioNegotiator(native).Initialize(
                CreateRequest(SampleRate.AUTO, SampleFormat.AUTO),
                session,
                Callback));

        Assert.IsTrue(session.CoreInitialized);
        Assert.IsTrue(session.AsioInitialized);
        Assert.AreEqual(native.MixerHandle, session.MixerHandle);
        Assert.AreEqual(native.MixerHandle, session.OutputHandle);
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
        private BASSASIOFormat currentFormat = BASSASIOFormat.BASS_ASIO_FORMAT_UNKNOWN;

        internal HashSet<int> AcceptedRates { get; } =
            [11025, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000];

        internal HashSet<BASSASIOFormat> AcceptedFormats { get; } =
            [BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT, BASSASIOFormat.BASS_ASIO_FORMAT_16BIT];

        internal List<int> CheckedRates { get; } = [];

        internal List<int> SetRates { get; } = [];

        internal List<BASSASIOFormat> SetFormats { get; } = [];

        internal double CurrentRate { get; set; } = 48000;

        internal BASSFlag MixerFlags { get; private set; }

        internal int MixerHandle { get; set; } = 123;

        internal bool EnableOutputResult { get; set; } = true;

        internal BASSError AsioError { get; set; } = BASSError.BASS_ERROR_FORMAT;

        public bool InitializeCore() => true;

        public int GetCoreDevice() => 4;

        public void DisableCoreUpdatePeriod()
        {
        }

        public BASS_ASIO_DEVICEINFO[] GetDeviceInfos() =>
            [new BASS_ASIO_DEVICEINFO { name = "ASIO Device", driver = "asio.dll" }];

        public BASS_ASIO_DEVICEINFO GetDeviceInfo(int deviceIndex) => GetDeviceInfos()[deviceIndex];

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

        public bool SetChannelFormat(BASSASIOFormat format)
        {
            SetFormats.Add(format);
            if (!AcceptedFormats.Contains(format))
            {
                return false;
            }

            currentFormat = format;
            return true;
        }

        public BASSASIOFormat GetChannelFormat() => currentFormat;

        public int CreateMixer(int rate, int channels, BASSFlag flags)
        {
            MixerFlags = flags;
            return MixerHandle;
        }

        public bool EnableOutputChannel(ASIOPROC callback) => EnableOutputResult;

        public bool JoinOutputChannel(int channel) => true;

        public bool Start(int bufferLength, int threads) => true;

        public int GetOutputLatency() => 480;

        public BASSError GetCoreError() => BASSError.BASS_OK;

        public BASSError GetAsioError() => AsioError;
    }
}
