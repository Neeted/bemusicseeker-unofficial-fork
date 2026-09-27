using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioSourceLoaderTests
{
    [TestInitialize]
    public void InitializeAudioRuntime()
    {
        BassAudioPlayer.Free();
        BassAudioRuntime.Shutdown();
        BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _);
    }

    [TestCleanup]
    public void ShutdownAudioRuntime()
    {
        BassAudioPlayer.Free();
        BassAudioRuntime.Shutdown();
    }

    [TestMethod]
    public void LoadFloatWave_PreservesOverRangeAndLowLevelSamples()
    {
        float tiny = MathF.ScaleB(1f, -20);
        float[] expected = [1.25f, -1.25f, tiny, 0f];
        using var wave = new TemporaryWave(BuildWave(
            formatTag: 3,
            channels: 1,
            sampleRate: 48000,
            containerBits: 32,
            data: FloatBytes(expected)));

        DecodedAudio actual = AudioSourceLoader.Load(wave.Path);

        Assert.AreEqual(48000, actual.SampleRate);
        Assert.AreEqual(1, actual.ChannelCount);
        Assert.AreEqual(4L, actual.FrameCount);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[index]), BitConverter.SingleToInt32Bits(actual.GetSample(index)));
        }
    }

    [TestMethod]
    public void LoadWaveWithOggExtension_UsesContainerSignature()
    {
        float[] expected = [1.125f, -0.375f];
        using var wave = new TemporaryWave(
            BuildWave(3, 1, 48000, 32, FloatBytes(expected)),
            extension: ".ogg");

        DecodedAudio actual = AudioSourceLoader.Load(wave.Path);

        Assert.AreEqual(48000, actual.SampleRate);
        Assert.AreEqual(1, actual.ChannelCount);
        Assert.AreEqual(2L, actual.FrameCount);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(
                BitConverter.SingleToInt32Bits(expected[index]),
                BitConverter.SingleToInt32Bits(actual.GetSample(index)),
                $"sample {index}");
        }
    }

    [TestMethod]
    public void LoadPcmWave_PreservesIntegerBoundaryScaling()
    {
        (ushort bits, byte[] data, float[] expected)[] cases =
        [
            (8, [0, 128, 255], [-1f, 0f, 127f / 128f]),
            (16, [0x00, 0x80, 0x00, 0x00, 0xFF, 0x7F], [-1f, 0f, (float)(32767d / 32768d)]),
            (24, Pcm24Bytes(-8388608, 0, 8388607), [-1f, 0f, (float)(8388607d / 8388608d)]),
            (32, Pcm32Bytes(int.MinValue, 0, int.MaxValue), [-1f, 0f, (float)(2147483647d / 2147483648d)])
        ];

        foreach ((ushort bits, byte[] data, float[] expected) in cases)
        {
            using var wave = new TemporaryWave(BuildWave(1, 1, 44100, bits, data));
            DecodedAudio actual = AudioSourceLoader.Load(wave.Path);
            Assert.AreEqual(expected.Length, actual.FrameCount, $"{bits}-bit frame count");
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(expected[index]),
                    BitConverter.SingleToInt32Bits(actual.GetSample(index)),
                    $"{bits}-bit sample {index}");
            }
        }
    }

    [TestMethod]
    public void LoadPcmWave_ConvertsFloat64AndValidBitsIn32Once()
    {
        double[] doubleSamples = [1.25d, -1.25d, System.Math.Pow(2, -20)];
        byte[] doubleData = new byte[doubleSamples.Length * sizeof(double)];
        for (int index = 0; index < doubleSamples.Length; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                doubleData.AsSpan(index * sizeof(double)),
                BitConverter.DoubleToInt64Bits(doubleSamples[index]));
        }

        using var float64Wave = new TemporaryWave(BuildWave(3, 1, 48000, 64, doubleData));
        DecodedAudio float64Audio = AudioSourceLoader.Load(float64Wave.Path);
        for (int index = 0; index < doubleSamples.Length; index++)
        {
            Assert.AreEqual(
                BitConverter.SingleToInt32Bits((float)doubleSamples[index]),
                BitConverter.SingleToInt32Bits(float64Audio.GetSample(index)));
        }

        byte[] packed24 = Pcm24Bytes(-8388608, 0, 8388607);
        byte[] valid24In32 = Pcm32Bytes(unchecked((int)0x80000000), 0, 0x7FFFFF00);
        using var packedWave = new TemporaryWave(BuildWave(1, 1, 44100, 24, packed24));
        using var extensibleWave = new TemporaryWave(BuildWave(
            formatTag: 1,
            channels: 1,
            sampleRate: 44100,
            containerBits: 32,
            data: valid24In32,
            extensible: true,
            channelMask: 0x00000004,
            validBits: 24));
        DecodedAudio packedAudio = AudioSourceLoader.Load(packedWave.Path);
        DecodedAudio extensibleAudio = AudioSourceLoader.Load(extensibleWave.Path);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 3).Select(index => BitConverter.SingleToInt32Bits(packedAudio.GetSample(index))).ToArray(),
            Enumerable.Range(0, 3).Select(index => BitConverter.SingleToInt32Bits(extensibleAudio.GetSample(index))).ToArray());
    }

    [TestMethod]
    public void LoadWave_HandlesChunkOrderAndOddChunkPadding()
    {
        float[] expected = [0.125f, -0.375f];
        using var wave = new TemporaryWave(BuildWave(
            formatTag: 3,
            channels: 1,
            sampleRate: 32000,
            containerBits: 32,
            data: FloatBytes(expected),
            dataBeforeFormat: true,
            addOddJunkChunk: true));

        DecodedAudio actual = AudioSourceLoader.Load(wave.Path);

        Assert.AreEqual(32000, actual.SampleRate);
        Assert.AreEqual(2L, actual.FrameCount);
        Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[0]), BitConverter.SingleToInt32Bits(actual.GetSample(0)));
        Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[1]), BitConverter.SingleToInt32Bits(actual.GetSample(1)));
    }

    [TestMethod]
    public void LoadWave_RejectsNonFiniteUnalignedAndUnknownLayoutInputs()
    {
        using var nonFiniteWave = new TemporaryWave(BuildWave(3, 1, 44100, 32, FloatBytes([float.NaN])));
        using var unalignedWave = new TemporaryWave(BuildWave(1, 1, 44100, 16, [0x01]));
        using var unknownLayoutWave = new TemporaryWave(BuildWave(
            formatTag: 1,
            channels: 3,
            sampleRate: 44100,
            containerBits: 16,
            data: Pcm16Bytes(0, 0, 0),
            extensible: true,
            channelMask: 0,
            validBits: 16));
        using var nineChannelWave = new TemporaryWave(BuildWave(
            formatTag: 1,
            channels: 9,
            sampleRate: 44100,
            containerBits: 16,
            data: Pcm16Bytes(0, 0, 0, 0, 0, 0, 0, 0, 0),
            extensible: true,
            channelMask: 0x0000073F,
            validBits: 16));

        AudioSourceLoadException nonFinite = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioSourceLoader.Load(nonFiniteWave.Path));
        AudioSourceLoadException unaligned = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioSourceLoader.Load(unalignedWave.Path));
        AudioSourceLoadException unknownLayout = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioSourceLoader.Load(unknownLayoutWave.Path));
        AudioSourceLoadException nineChannels = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioSourceLoader.Load(nineChannelWave.Path));

        Assert.AreEqual(AudioSourceLoadStage.DecodeWithBass, nonFinite.Stage);
        Assert.AreEqual(AudioSourceLoadStage.ParseWaveFormat, unaligned.Stage);
        Assert.AreEqual(AudioSourceLoadStage.ParseWaveFormat, unknownLayout.Stage);
        Assert.AreEqual(AudioSourceLoadStage.ParseWaveFormat, nineChannels.Stage);
    }

    [TestMethod]
    public void LoadWave_RejectsContainerSizeBeyondTheFile()
    {
        byte[] bytes = BuildWave(3, 1, 44100, 32, FloatBytes([0.5f]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), uint.MaxValue);
        using var wave = new TemporaryWave(bytes);

        AudioSourceLoadException failure = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioSourceLoader.Load(wave.Path));

        Assert.AreEqual(AudioSourceLoadStage.ParseWaveFormat, failure.Stage);
    }

    [TestMethod]
    public void AudioInputFile_ReadOnlyViewsShareOneNativeBufferAndUseIndependentPositions()
    {
        using var wave = new TemporaryWave(BuildWave(3, 1, 44100, 32, FloatBytes([0.25f, -0.75f])));
        using var input = AudioInputFile.Read(wave.Path);
        using Stream first = input.OpenReadView();
        using Stream second = input.OpenReadView();
        byte[] signature = new byte[12];

        Assert.AreEqual(new FileInfo(wave.Path).Length, input.Length);
        Assert.AreEqual(12, first.Read(signature));
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(signature, 0, 4));
        Assert.AreEqual(0L, second.Position);
        Assert.AreEqual((int)'R', second.ReadByte());
        Assert.AreEqual(12L, first.Position);
        Assert.AreEqual(1L, second.Position);
    }

    [TestMethod]
    public void AudioInputFile_ReadOnlyViewRetainsNativeBufferAfterOwnerDispose()
    {
        using var wave = new TemporaryWave(BuildWave(3, 1, 44100, 32, FloatBytes([0.5f])));
        var input = AudioInputFile.Read(wave.Path);
        Stream view = input.OpenReadView();
        input.Dispose();

        try
        {
            Assert.ThrowsException<ObjectDisposedException>(() => input.OpenReadView());
            Assert.AreEqual((int)'R', view.ReadByte(), "An existing SafeBuffer view must retain its allocation.");
        }
        finally
        {
            view.Dispose();
        }
    }

    [TestMethod]
    public void AudioInputFile_ReportsReadFailureAtInspectStageAndChecksNativeLengthWithoutAllocation()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");

        AudioSourceLoadException failure = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioSourceLoader.Load(missing));

        Assert.AreEqual(AudioSourceLoadStage.InspectContainer, failure.Stage);
        Assert.IsInstanceOfType<IOException>(failure.InnerException);
        Assert.ThrowsException<IOException>(() => AudioInputFile.GetNativeAllocationSize(-1));
        if (IntPtr.Size == sizeof(int))
        {
            Assert.ThrowsException<IOException>(
                () => AudioInputFile.GetNativeAllocationSize((long)int.MaxValue + 1));
        }
        else
        {
            Assert.AreEqual(long.MaxValue, AudioInputFile.GetNativeAllocationSize(long.MaxValue).ToInt64());
        }
    }

    [TestMethod]
    public void AudioInputFile_RejectsOversizedOggBeforeNativeAllocationAfterSplitSignatureRead()
    {
        string path = Path.Combine(Path.GetTempPath(), "oversized.ogg");
        long length = (long)Array.MaxLength + 1;
        var stream = new LengthReportingSignatureStream(length, "OggS"u8.ToArray(), maxReadSize: 2);
        int openCount = 0;
        int allocationCount = 0;

        AudioSourceLoadException failure = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioInputFile.Read(
                path,
                _ =>
                {
                    openCount++;
                    return stream;
                },
                requestedLength =>
                {
                    allocationCount++;
                    if (requestedLength > 1024 * 1024)
                    {
                        throw new InvalidOperationException("The test prevents a large native allocation.");
                    }
                }));

        Assert.AreEqual(AudioSourceLoadStage.DecodeVorbis, failure.Stage);
        Assert.AreEqual(Path.GetFullPath(path), failure.Path);
        Assert.AreEqual(1, openCount);
        Assert.AreEqual(2, stream.ReadCount, "The split OggS signature should require only two short reads.");
        Assert.AreEqual(4L, stream.BytesRead);
        Assert.IsTrue(stream.WasDisposed);
        Assert.AreEqual(0, allocationCount, "An oversized Ogg input must be rejected before native allocation.");
    }

    [TestMethod]
    public void AudioInputFile_DoesNotApplyOggInputLimitToWaveSignature()
    {
        string path = Path.Combine(Path.GetTempPath(), "oversized.wav");
        long length = (long)Array.MaxLength + 1;
        var stream = new LengthReportingSignatureStream(length, "RIFF"u8.ToArray(), maxReadSize: 2);
        var allocationPrevented = new InvalidOperationException("The test prevents a large native allocation.");
        int openCount = 0;
        int allocationCount = 0;

        InvalidOperationException actual = Assert.ThrowsException<InvalidOperationException>(
            () => AudioInputFile.Read(
                path,
                _ =>
                {
                    openCount++;
                    return stream;
                },
                requestedLength =>
                {
                    allocationCount++;
                    Assert.AreEqual(length, requestedLength);
                    throw allocationPrevented;
                }));

        Assert.AreSame(allocationPrevented, actual);
        Assert.AreEqual(1, openCount);
        Assert.AreEqual(2, stream.ReadCount);
        Assert.AreEqual(4L, stream.BytesRead);
        Assert.IsTrue(stream.WasDisposed);
        Assert.AreEqual(1, allocationCount, "A non-Ogg input should reach native allocation without the Vorbis array limit.");
    }

    [TestMethod]
    public void AudioInputFile_MidReadIoFailureKeepsInspectStageAndOriginalException()
    {
        using var wave = new TemporaryWave(BuildWave(3, 1, 44100, 32, FloatBytes([0.5f, -0.25f])));
        var originalFailure = new IOException("Injected failure during the second read.");

        AudioSourceLoadException failure = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioSourceLoader.LoadWithSession(wave.Path, _ => new FailingReadStream(
                File.ReadAllBytes(wave.Path), originalFailure)));

        Assert.AreEqual(AudioSourceLoadStage.InspectContainer, failure.Stage);
        Assert.AreSame(originalFailure, failure.InnerException);
    }

    [TestMethod]
    public void LoadWithSession_UsesOneOpenedInputAndRetainsItUntilTemporaryDecoderRelease()
    {
        using var wave = new TemporaryWave(BuildWave(3, 1, 44100, 32, FloatBytes([0.5f, -0.25f])));
        BassAudioSession session = BassAudioPlayer.ActiveSession
            ?? throw new AssertFailedException("The test audio session was not initialized.");
        int openCount = 0;
        var injectedReleaseFailure = new InvalidOperationException("Injected native release failure.");

        AudioSourceLoadException failure = Assert.ThrowsException<AudioSourceLoadException>(
            () => AudioSourceLoader.LoadWithSession(
                wave.Path,
                _ =>
                {
                    openCount++;
                    return File.OpenRead(wave.Path);
                },
                _ => throw injectedReleaseFailure));

        Assert.AreEqual(AudioSourceLoadStage.DecodeWithBass, failure.Stage);
        Assert.AreSame(injectedReleaseFailure, failure.InnerException);
        Assert.AreEqual(1, openCount, "The signature, metadata, and decoder must share one file read.");
        BassAudioOwnedStream owned = session.GetPlayerStreams().Single();
        Assert.IsTrue(owned.TryGetOwner(out AudioInputFile input));
        using (Stream view = input.OpenReadView())
        {
            Assert.AreEqual((int)'R', view.ReadByte(), "A failed native release must retain the input buffer.");
        }

        Bass.CurrentDevice = session.CoreDeviceIndex;
        Assert.IsTrue(Bass.StreamFree(owned.Handle), "Explicit native cleanup should release the retained decoder.");
        session.ConfirmPlayerStreamReleased(owned.Handle);
        Assert.AreEqual(0, session.GetPlayerStreams().Count);
        Assert.ThrowsException<ObjectDisposedException>(() => input.OpenReadView());
    }

    [TestMethod]
    public async Task LoadWithSession_SessionReplacementDuringReadCannotDecodeOnReplacementSession()
    {
        var wave = new TemporaryWave(BuildWave(3, 1, 44100, 32, FloatBytes([0.5f, -0.25f])));
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRead = new ManualResetEventSlim();
        Task<DecodedAudioSessionSource> load = Task.Run(() => AudioSourceLoader.LoadWithSession(
            wave.Path,
            _ => new BlockingReadStream(File.ReadAllBytes(wave.Path), readStarted, allowRead)));
        BassAudioSession? replacement = null;
        var failures = new List<Exception>();

        try
        {
            await WaitForSignalOrProducerAsync(
                readStarted.Task,
                load,
                "The input stream did not reach its read gate.");
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out replacement);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureCleanup(failures, allowRead.Set);
        InvalidOperationException? loadFailure = null;
        try
        {
            _ = await load;
            failures.Add(new AssertFailedException("The load should reject the replaced audio session."));
        }
        catch (InvalidOperationException exception)
        {
            loadFailure = exception;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 0)
        {
            CaptureCleanup(failures, () => StringAssert.Contains(
                (loadFailure ?? throw new AssertFailedException("The loader accepted the replaced audio session.")).Message,
                "audio session changed"));
            CaptureCleanup(failures, () => Assert.AreEqual(0, replacement?.GetPlayerStreams().Count));
        }
        CaptureCleanup(failures, BassAudioPlayer.Free);
        CaptureCleanup(failures, BassAudioRuntime.Shutdown);
        CaptureCleanup(failures, allowRead.Dispose);
        CaptureCleanup(failures, wave.Dispose);
        ThrowFailures(failures);
    }

    private static void CaptureCleanup(List<Exception> failures, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task WaitForSignalOrProducerAsync(Task signal, Task producer, string missingSignalMessage)
    {
        if (!signal.IsCompleted)
        {
            await Task.WhenAny(signal, producer).ConfigureAwait(false);
        }
        if (signal.IsCompleted)
        {
            await signal.ConfigureAwait(false);
            return;
        }

        await producer.ConfigureAwait(false);
        throw new AssertFailedException(missingSignalMessage);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Count > 1)
        {
            throw new AggregateException("The test failure and cleanup failures are retained.", failures);
        }
    }

    [TestMethod]
    public void FloatWaveSource_PresentsTheSamePcmWithIndependentByteCursors()
    {
        float[] samples = [1.25f, -1.25f, MathF.ScaleB(1f, -20), 0.5f];
        var audio = new DecodedAudio(48000, AudioChannelLayout.CreateStandard(2), samples);
        var first = new FloatWaveSource(audio, "fixture.wav");
        var second = new FloatWaveSource(audio, "fixture.wav");

        byte[] header = ReadVirtualBytes(first, 0, 80);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        Assert.AreEqual("WAVE", Encoding.ASCII.GetString(header, 8, 4));
        Assert.AreEqual(80L + samples.Length * sizeof(float), first.TotalLength);
        Assert.AreEqual((ushort)0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));
        Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
        Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(68)));
        Assert.AreEqual((uint)(samples.Length * sizeof(float)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76)));

        byte[] expectedPcm = FloatBytes(samples);
        byte[] firstSlice = ReadVirtualBytes(first, 81, 7);
        byte[] secondHeader = ReadVirtualBytes(second, 0, 12);
        CollectionAssert.AreEqual(expectedPcm.AsSpan(1, 7).ToArray(), firstSlice);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(secondHeader, 0, 4));
        Assert.AreEqual(BitConverter.SingleToInt32Bits(samples[0]), BitConverter.SingleToInt32Bits(audio.GetSample(0)));
        Assert.IsTrue(first.Seek(first.TotalLength));
        IntPtr endBuffer = Marshal.AllocHGlobal(1);
        try
        {
            Assert.AreEqual(-1, first.Read(endBuffer, 1));
        }
        finally
        {
            Marshal.FreeHGlobal(endBuffer);
        }
    }

    [TestMethod]
    public void FloatWaveSource_ReordersVorbisSamplesToWaveAndBassSpeakerOrder()
    {
        AudioSpeakerPosition[] vorbisOrder =
        [
            AudioSpeakerPosition.FrontLeft,
            AudioSpeakerPosition.FrontCenter,
            AudioSpeakerPosition.FrontRight,
            AudioSpeakerPosition.BackLeft,
            AudioSpeakerPosition.BackRight,
            AudioSpeakerPosition.LowFrequency
        ];
        float[] samples = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, -0.1f, -0.2f, -0.3f, -0.4f, -0.5f, -0.6f];
        var audio = new DecodedAudio(48000, new AudioChannelLayout(vorbisOrder), samples);
        var source = new FloatWaveSource(audio, "fixture.ogg");
        float[] waveOrder = [0.1f, 0.3f, 0.2f, 0.6f, 0.4f, 0.5f, -0.1f, -0.3f, -0.2f, -0.6f, -0.4f, -0.5f];

        CollectionAssert.AreEqual(
            new[]
            {
                AudioSpeakerPosition.FrontLeft,
                AudioSpeakerPosition.FrontRight,
                AudioSpeakerPosition.FrontCenter,
                AudioSpeakerPosition.LowFrequency,
                AudioSpeakerPosition.BackLeft,
                AudioSpeakerPosition.BackRight
            },
            Enumerable.Range(0, source.ChannelLayout.ChannelCount).Select(index => source.ChannelLayout[index]).ToArray());
        byte[] header = ReadVirtualBytes(source, 0, 80);
        Assert.AreEqual(0x0000003Fu, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));

        byte[] expected = FloatBytes(waveOrder);
        CollectionAssert.AreEqual(expected, ReadVirtualBytes(source, 80, expected.Length));
        CollectionAssert.AreEqual(expected.AsSpan(1, 23).ToArray(), ReadVirtualBytes(source, 81, 23));
    }

    private static byte[] BuildWave(
        ushort formatTag,
        int channels,
        int sampleRate,
        ushort containerBits,
        byte[] data,
        bool extensible = false,
        uint channelMask = 0,
        ushort validBits = 0,
        bool dataBeforeFormat = false,
        bool addOddJunkChunk = false)
    {
        int blockAlign = checked(channels * containerBits / 8);
        byte[] format = new byte[extensible ? 40 : 16];
        BinaryPrimitives.WriteUInt16LittleEndian(format, extensible ? (ushort)0xFFFE : formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8), checked((uint)(sampleRate * blockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), checked((ushort)blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), containerBits);
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(16), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(18), validBits == 0 ? containerBits : validBits);
            BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(20), channelMask);
            byte[] subFormat = formatTag == 3
                ? [0x03, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71]
                : [0x01, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71];
            subFormat.CopyTo(format, 24);
        }

        using var chunks = new MemoryStream();
        using (var writer = new BinaryWriter(chunks, Encoding.ASCII, leaveOpen: true))
        {
            if (addOddJunkChunk)
            {
                WriteChunk(writer, "JUNK", [1, 2, 3]);
            }
            if (dataBeforeFormat)
            {
                WriteChunk(writer, "data", data);
            }
            WriteChunk(writer, "fmt ", format);
            if (!dataBeforeFormat)
            {
                WriteChunk(writer, "data", data);
            }
        }

        byte[] body = chunks.ToArray();
        using var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(checked((uint)(4 + body.Length)));
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(body);
        }
        return wave.ToArray();
    }

    private static void WriteChunk(BinaryWriter writer, string id, byte[] data)
    {
        writer.Write(Encoding.ASCII.GetBytes(id));
        writer.Write(checked((uint)data.Length));
        writer.Write(data);
        if ((data.Length & 1) != 0)
        {
            writer.Write((byte)0);
        }
    }

    private static byte[] FloatBytes(float[] samples)
    {
        byte[] bytes = new byte[checked(samples.Length * sizeof(float))];
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float)),
                BitConverter.SingleToInt32Bits(samples[index]));
        }
        return bytes;
    }

    private static byte[] Pcm16Bytes(params short[] samples)
    {
        byte[] bytes = new byte[checked(samples.Length * sizeof(short))];
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short)), samples[index]);
        }
        return bytes;
    }

    private static byte[] Pcm24Bytes(params int[] samples)
    {
        byte[] bytes = new byte[checked(samples.Length * 3)];
        for (int index = 0; index < samples.Length; index++)
        {
            int value = samples[index];
            bytes[index * 3] = (byte)value;
            bytes[index * 3 + 1] = (byte)(value >> 8);
            bytes[index * 3 + 2] = (byte)(value >> 16);
        }
        return bytes;
    }

    private static byte[] Pcm32Bytes(params int[] samples)
    {
        byte[] bytes = new byte[checked(samples.Length * sizeof(int))];
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * sizeof(int)), samples[index]);
        }
        return bytes;
    }

    private static byte[] ReadVirtualBytes(FloatWaveSource source, long offset, int length)
    {
        Assert.IsTrue(source.Seek(offset));
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            int read = source.Read(buffer, length);
            Assert.AreEqual(length, read);
            byte[] bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed class TemporaryWave : IDisposable
    {
        internal TemporaryWave(byte[] bytes, string extension = ".wav")
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BeMusicSeeker-" + Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(Path, bytes);
        }

        internal string Path { get; }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }

    private sealed class FailingReadStream(byte[] content, IOException failure) : MemoryStream(content)
    {
        private int readCount;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (readCount++ != 0)
            {
                throw failure;
            }
            return base.Read(buffer, offset, Math.Min(count, 7));
        }
    }

    private sealed class BlockingReadStream(
        byte[] content,
        TaskCompletionSource readStarted,
        ManualResetEventSlim allowRead) : MemoryStream(content)
    {
        private int readCount;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref readCount) == 1)
            {
                readStarted.TrySetResult();
                allowRead.Wait();
            }
            return base.Read(buffer, offset, count);
        }
    }

    private sealed class LengthReportingSignatureStream(long length, byte[] signature, int maxReadSize) : Stream
    {
        private long position;

        internal int ReadCount { get; private set; }

        internal long BytesRead => position;

        internal bool WasDisposed { get; private set; }

        public override bool CanRead => !WasDisposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(WasDisposed, this);
            ReadCount++;
            int available = (int)System.Math.Max(0, signature.Length - position);
            int read = System.Math.Min(System.Math.Min(count, maxReadSize), available);
            signature.AsSpan(checked((int)position), read).CopyTo(buffer.AsSpan(offset, read));
            position = checked(position + read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
