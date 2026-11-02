using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;
using ManagedBass;
using ManagedBass.Enc;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 明示的に有効化された場合に、本番writerと実外部encoderの出力を検査します。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExternalAudioEncoderSmokeTests
{
    private const string EnabledEnvironmentVariable = "BMS_TEST_AUDIO_ENCODERS";
    private const string EncoderDirectoryEnvironmentVariable = "BMS_TEST_AUDIO_ENCODER_DIR";
    private const string EncoderTypesEnvironmentVariable = "BMS_TEST_AUDIO_ENCODER_TYPES";

    private static readonly EncoderType[] SupportedEncoderTypes =
    [
        EncoderType.MP3_LAME,
        EncoderType.AAC_NERO,
        EncoderType.OPUS,
        EncoderType.FLAC,
        EncoderType.OGG_VORBIS
    ];

    /// <summary>明示有効化した実encoderで音声を保存・復号し、形式署名・非無音PCM・衝突先保持と資源解放を検査する。</summary>
    /// <remarks>外部配布encoderを準備するEnvironmentIntegration区分。環境指定なし・指定したencoder未配置は失敗とする。共有nativeとencoder状態を直列所有する。</remarks>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    [TestCategory("Suite.audio-encoders")]
    public async Task AvailableExternalEncodersProduceRecognizableOutput()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)?.Trim(),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Fail(
                EnabledEnvironmentVariable + "=1 is required for the external encoder smoke test.");
            return;
        }

        string? configuredDirectory = GetOptionalEnvironmentValue(EncoderDirectoryEnvironmentVariable);
        IReadOnlyList<EncoderType> requestedTypes = ReadRequestedEncoderTypes();
        EncoderType[] candidateTypes = requestedTypes.Count == 0
            ? SupportedEncoderTypes
            : requestedTypes.ToArray();
        (EncoderType EncoderType, string Directory)[] discovered = candidateTypes
            .Select(encoderType =>
            {
                string directory = encoderType.SearchEncoderBinary(configuredDirectory);
                return (EncoderType: encoderType, Directory: directory);
            })
            .ToArray();
        string[] missing = discovered
            .Where(item => item.Directory == null)
            .Select(item => item.EncoderType.ToString())
            .ToArray();

        if (requestedTypes.Count > 0 && missing.Length > 0)
        {
            Assert.Fail(
                "The requested external encoder executable(s) were not found: "
                + string.Join(", ", missing)
                + ". Search order: "
                + string.Join("; ", GetSearchDirectories(configuredDirectory)));
        }

        EncoderType[] available = discovered
            .Where(item => item.Directory != null)
            .Select(item => item.EncoderType)
            .ToArray();
        if (available.Length == 0)
        {
            Assert.Fail(
                "Opt-in external encoder smoke found no supported executable. Search order: "
                + string.Join("; ", GetSearchDirectories(configuredDirectory)));
        }

        string previousEncoderDirectory = BassAudioWriter.EncoderDirectory;
        try
        {
            BassAudioWriter.EncoderDirectory = configuredDirectory ?? AppContext.BaseDirectory;
            var suiteDeadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromSeconds(60));
            await RunSmokeAsync(available, suiteDeadline);
        }
        finally
        {
            BassAudioWriter.EncoderDirectory = previousEncoderDirectory;
        }
    }

    /// <summary>実BASSenc子processへPCMを送り同期停止した後に、ゼロ／非ゼロの終了値を所有handleから正確に回収する。</summary>
    /// <remarks>native encoderとWSH子processの実接続を検査するProcessIntegration区分。共有native状態を直列所有し、停止・handle回収・script削除まで行う。</remarks>
    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    [TestCategory("ProcessIntegration")]
    [TestCategory("Suite.audio-encoder-child")]
    public void BassEncodeStartWriteStopPreservesOwnedChildExitCode(int expectedExitCode)
    {
        var suiteDeadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromSeconds(60));
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var session = new ManagedBassWriterSession(workspace, suiteDeadline);
        bool childTerminated = true;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            session.Initialize();
            AssertNativeEncoderChildExit(expectedExitCode, workspace, suiteDeadline, ref childTerminated);
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            if (childTerminated) { session.Dispose(ref primaryFailure); }
            PreserveUnreleasedSession(ref primaryFailure, session, workspace);
            CaptureCleanup(ref primaryFailure, workspace.Dispose);
        }

        primaryFailure?.Throw();
    }

    private static void AssertNativeEncoderChildExit(int expectedExitCode, TestWorkspace workspace,
        ProcessDeadline suiteDeadline, ref bool childTerminated)
    {
        BassAudioSession audioSession = BassAudioPlayer.ActiveSession
            ?? throw new InvalidOperationException("The writer has no active audio session.");
        // stdinをEOFまで消費する一つのプロセスで、BASSencの同期停止と終了コードを確認します。
        // PowerShellはコンソールから切り離されるとスクリプト実行前に終了するため、WSHを使います。
        string scriptPath = workspace.GetPath("encoder-eof.js");
        string commandLine = AudioEncoderCommandFactory.QuoteWindowsArgument(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe"))
            + " //B //Nologo " + AudioEncoderCommandFactory.QuoteWindowsArgument(scriptPath);
        int encoderHandle = 0;
        bool encoderAcquired = false;
        bool exitConfirmed = false;
        uint? lastExitCode = null;
        int lastExitError = 0;
        IAudioEncoderProcessHandle? processHandle = null;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            File.WriteAllText(scriptPath,
                "var input = WScript.StdIn.ReadAll(); if (input.length != 16) WScript.Quit(99); WScript.Quit("
                + expectedExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ");", Encoding.ASCII);
            encoderHandle = BassEnc.EncodeStart(
                audioSession.MixerHandle, commandLine,
                EncodeFlags.Unicode | EncodeFlags.NoHeader | EncodeFlags.Pause, null, IntPtr.Zero);
            encoderAcquired = encoderHandle != 0;
            Assert.AreNotEqual(0, encoderHandle, "BASS_Encode_Start failed with " + Bass.LastError + ".");
            Assert.IsTrue(AudioEncoderProcessHandle.TryDuplicate(
                AudioEncoderProcessHandle.FromBassEncoderHandle(encoderHandle),
                out processHandle, out int duplicateError),
                "DuplicateHandle failed with Win32 error " + duplicateError + ".");
            Assert.IsNotNull(processHandle);
            float[] samples = [0.125f, -0.125f, 0.5f, -0.5f];
            Assert.IsTrue(BassEnc.EncodeWrite(encoderHandle, samples, samples.Length * sizeof(float)));
            bool stopped = BassEnc.EncodeStop(encoderHandle);
            if (stopped)
            {
                encoderHandle = 0;
            }
            Assert.IsTrue(stopped, "BASS_Encode_Stop failed with " + Bass.LastError + ".");
            bool exitRead = AudioEncoderProcessHandle.TryGetExitCode(processHandle, out uint actualExitCode, out lastExitError);
            lastExitCode = exitRead ? actualExitCode : null;
            exitConfirmed = exitRead && actualExitCode != 259;
            Assert.IsTrue(exitConfirmed, "GetExitCodeProcess did not confirm exit; Win32 error " + lastExitError + ".");
            Assert.AreEqual((uint)expectedExitCode, actualExitCode);
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            if (encoderHandle != 0)
            {
                CaptureCleanup(ref primaryFailure, () =>
                {
                    if (!BassEnc.EncodeStop(encoderHandle))
                    {
                        throw new InvalidOperationException("The native encoder helper did not stop: " + Bass.LastError);
                    }
                    encoderHandle = 0;
                });
            }
            if (encoderHandle == 0 && processHandle is { } ownedProcessHandle)
            {
                CaptureCleanup(ref primaryFailure, () =>
                {
                    if (!exitConfirmed)
                    {
                        bool exitRead = AudioEncoderProcessHandle.TryGetExitCode(ownedProcessHandle, out uint exitCode, out lastExitError);
                        lastExitCode = exitRead ? exitCode : null;
                        exitConfirmed = exitRead && exitCode != 259;
                    }
                    if (!exitConfirmed)
                    { throw new InvalidOperationException("The native encoder child exit is not confirmed: " + lastExitError); }
                    ownedProcessHandle.Dispose();
                    processHandle = null;
                });
            }
            childTerminated = !encoderAcquired || (encoderHandle == 0 && exitConfirmed);
            if (!childTerminated || processHandle != null)
            {
                workspace.PreserveArtifacts();
                primaryFailure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException("The native encoder child remains owned."));
                primaryFailure.SourceException.Data["AudioSmoke.NativeEncoderOwner"] = new object?[] { encoderHandle, processHandle };
            }
            AudioEncoderCleanupObservation observation = !encoderAcquired
                ? AudioEncoderCleanupObservation.NoUnconfirmedOwner
                : encoderHandle != 0 ? AudioEncoderCleanupObservation.NativeStopUnconfirmed
                : !exitConfirmed ? AudioEncoderCleanupObservation.ChildExitUnconfirmed
                : AudioEncoderCleanupObservation.NoUnconfirmedOwner;
            AudioEncoderTestTermination.Complete(observation, workspace, suiteDeadline, primaryFailure?.SourceException,
                $"encoderAcquired={encoderAcquired}; stopConfirmed={encoderAcquired && encoderHandle == 0}; exitConfirmed={exitConfirmed}; exitCode={lastExitCode}; exitError={lastExitError}");
        }
        primaryFailure?.Throw();
    }

    private static IReadOnlyList<EncoderType> ReadRequestedEncoderTypes()
    {
        string? value = GetOptionalEnvironmentValue(EncoderTypesEnvironmentVariable);
        if (value == null)
        {
            return Array.Empty<EncoderType>();
        }

        string[] tokens = value.Split(',', StringSplitOptions.None)
            .Select(token => token.Trim())
            .ToArray();
        if (tokens.Any(token => token.Length == 0))
        {
            Assert.Fail(EncoderTypesEnvironmentVariable + " must contain comma-separated encoder names.");
        }

        var unknown = new List<string>();
        var parsed = new List<EncoderType>(tokens.Length);
        foreach (string token in tokens)
        {
            bool found = false;
            foreach (EncoderType supportedEncoderType in SupportedEncoderTypes)
            {
                if (!string.Equals(
                        token,
                        supportedEncoderType.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;
                if (parsed.Contains(supportedEncoderType))
                {
                    unknown.Add(token);
                }
                else
                {
                    parsed.Add(supportedEncoderType);
                }

                break;
            }

            if (!found)
            {
                unknown.Add(token);
            }
        }

        if (unknown.Count > 0)
        {
            Assert.Fail(
                EncoderTypesEnvironmentVariable
                + " contains an unknown or duplicate encoder: "
                + string.Join(", ", unknown)
                + ". Allowed values: "
                + string.Join(", ", SupportedEncoderTypes));
        }

        return parsed;
    }

    private static string? GetOptionalEnvironmentValue(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IEnumerable<string> GetSearchDirectories(string? configuredDirectory)
    {
        string?[] directories = new[]
        {
            configuredDirectory,
            ApplicationPathPolicy.Current.AssetsDirectory,
            Path.Combine(ApplicationPathPolicy.Current.AssetsDirectory, "libs", "x64"),
            Path.Combine(ApplicationPathPolicy.Current.AssetsDirectory, "x64")
        };

        return directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => directory!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task RunSmokeAsync(IReadOnlyList<EncoderType> encoderTypes, ProcessDeadline suiteDeadline)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = workspace.RootDirectory;
        var session = new ManagedBassWriterSession(workspace, suiteDeadline);
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            session.Initialize();
            BassAudioSession ownedSession = BassAudioPlayer.ActiveSession
                ?? throw new InvalidOperationException("The writer has no active audio session.");
            ChannelInfo mixerInfo = Bass.ChannelGetInfo(ownedSession.MixerHandle);
            Assert.IsTrue(mixerInfo.Frequency > 0, "The output mixer reported an invalid sample rate.");
            Assert.AreEqual(2, mixerInfo.Channels, "The external smoke fixture expects stereo output.");
            float[] pcm = CreateDeterministicPcm(
                mixerInfo.Frequency,
                frameCount: mixerInfo.Frequency / 10,
                mixerInfo.Channels);

            foreach (EncoderType encoderType in encoderTypes)
            {
                string outputWithoutExtension = Path.Combine(
                    temporaryDirectory,
                    "sample 音声 (space) " + encoderType);
                string extension = encoderType.GetEncoderOutputExtension();
                string collisionPath = outputWithoutExtension + extension;
                File.WriteAllBytes(collisionPath, [0xC0, 0xFF, 0xEE]);

                CreateEncoder(encoderType, outputWithoutExtension);
                BassAudioWriter.SetTagInfo(new AudioTagInfo(
                    "Smoke Artist",
                    "Smoke Title",
                    "Smoke Genre",
                    2d,
                    "120",
                    "synthetic.wav",
                    "ManagedBass external encoder smoke"));
                BassAudioWriter.StartRecording();
                BassAudioWriter.WritePcm(pcm);
                BassAudioWriter.StopRecording();

                string outputPath = outputWithoutExtension + " (2)" + extension;
                Assert.IsTrue(File.Exists(outputPath), "Encoder did not create " + outputPath);
                Assert.IsTrue(new FileInfo(outputPath).Length > 0, "Encoder created an empty output.");
                CollectionAssert.AreEqual(
                    new byte[] { 0xC0, 0xFF, 0xEE },
                    File.ReadAllBytes(collisionPath),
                    "The existing output was overwritten instead of receiving a collision suffix.");
                AssertOutputSignature(encoderType, outputPath, mixerInfo.Frequency, mixerInfo.Channels);
                await AssertDecodedOutputAsync(encoderType, outputPath, workspace, suiteDeadline, mixerInfo.Frequency, mixerInfo.Channels);
                Assert.IsTrue(
                    BassAudioWriter.TryReleaseEncoder(),
                    "The encoder owner did not release cleanly after " + encoderType + ".");
            }
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            session.Dispose(ref primaryFailure);
            PreserveUnreleasedSession(ref primaryFailure, session, workspace);
            CaptureCleanup(ref primaryFailure, workspace.Dispose);
        }

        primaryFailure?.Throw();
    }

    private static void CreateEncoder(EncoderType encoderType, string outputWithoutExtension)
    {
        switch (encoderType)
        {
            case EncoderType.MP3_LAME:
                BassAudioWriter.CreateEncoderLAME(outputWithoutExtension);
                break;
            case EncoderType.AAC_NERO:
                BassAudioWriter.CreateEncoderNeroAAC(outputWithoutExtension);
                break;
            case EncoderType.OPUS:
                BassAudioWriter.CreateEncoderOPUS(outputWithoutExtension);
                break;
            case EncoderType.FLAC:
                BassAudioWriter.CreateEncoderFLAC(outputWithoutExtension);
                break;
            case EncoderType.OGG_VORBIS:
                BassAudioWriter.CreateEncoderOGG(outputWithoutExtension);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoderType), encoderType, "Unsupported encoder.");
        }
    }

    private static void AssertOutputSignature(
        EncoderType encoderType,
        string outputPath,
        int expectedSampleRate,
        int expectedChannels)
    {
        byte[] output = File.ReadAllBytes(outputPath);
        switch (encoderType)
        {
            case EncoderType.MP3_LAME:
                Assert.IsTrue(
                    HasAsciiAt(output, 0, "ID3") || HasMpegFrameSync(output),
                    "The MP3 output has neither an ID3 header nor an MPEG frame sync.");
                break;
            case EncoderType.AAC_NERO:
                Assert.IsTrue(HasAsciiAt(output, 4, "ftyp"), "The M4A output has no ISO BMFF ftyp box.");
                break;
            case EncoderType.OPUS:
                Assert.IsTrue(HasAsciiAt(output, 0, "OggS"), "The Opus output has no OggS page.");
                Assert.IsTrue(ContainsAscii(output, "OpusHead"), "The Opus output has no OpusHead packet.");
                int opusHeadOffset = FindAscii(output, "OpusHead");
                Assert.IsTrue(opusHeadOffset >= 0);
                Assert.IsTrue(opusHeadOffset + 16 <= output.Length, "The OpusHead packet is truncated.");
                Assert.AreEqual(expectedChannels, output[opusHeadOffset + 9]);
                Assert.AreEqual(
                    (uint)expectedSampleRate,
                    BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(opusHeadOffset + 12, 4)));
                break;
            case EncoderType.FLAC:
                Assert.IsTrue(HasAsciiAt(output, 0, "fLaC"), "The FLAC output has no fLaC marker.");
                break;
            case EncoderType.OGG_VORBIS:
                Assert.IsTrue(HasAsciiAt(output, 0, "OggS"), "The Ogg output has no OggS page.");
                Assert.IsTrue(
                    HasPacketMarker(output, 1, "vorbis"),
                    "The Ogg output has no Vorbis identification packet.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoderType), encoderType, "Unsupported encoder.");
        }
    }

    private static async Task AssertDecodedOutputAsync(
        EncoderType encoderType,
        string outputPath,
        TestWorkspace workspace,
        ProcessDeadline suiteDeadline,
        int expectedSampleRate,
        int expectedChannels)
    {
        if (encoderType == EncoderType.OPUS)
        {
            string encoderDirectory = encoderType.SearchEncoderBinary(BassAudioWriter.EncoderDirectory);
            string decoderPath = Path.Combine(encoderDirectory, "opusdec.exe");
            Assert.IsTrue(File.Exists(decoderPath), "opusdec.exe is required to validate the Opus decode.");

            string decodedWavePath = workspace.GetPath("opus-decoded.wav");
            await RunDecoderProcessAsync(
                decoderPath, workspace, suiteDeadline,
                "--float",
                "--rate",
                "48000",
                outputPath,
                decodedWavePath);
            AudioTestWaveFile decodedWave = AudioTestWaveFileReader.Read(File.ReadAllBytes(decodedWavePath));
            AssertDecodedWave(decodedWave, 48000, expectedChannels, expectedFormat: 3, expectedBits: 32);
            return;
        }

        if (encoderType == EncoderType.FLAC)
        {
            string encoderDirectory = encoderType.SearchEncoderBinary(BassAudioWriter.EncoderDirectory);
            string decoderPath = Path.Combine(encoderDirectory, "flac.exe");
            Assert.IsTrue(File.Exists(decoderPath), "flac.exe is required to validate the FLAC decode.");

            string decodedWavePath = workspace.GetPath("flac-decoded.wav");
            await RunDecoderProcessAsync(
                decoderPath, workspace, suiteDeadline,
                "--decode",
                "--force",
                "--output-name=" + decodedWavePath,
                outputPath);
            AudioTestWaveFile decodedWave = AudioTestWaveFileReader.Read(File.ReadAllBytes(decodedWavePath));
            AssertDecodedWave(decodedWave, expectedSampleRate, expectedChannels, expectedFormat: 1, expectedBits: 24);
            return;
        }

        DecodedAudio decoded = AudioSourceLoader.Load(outputPath);
        Assert.AreEqual(expectedSampleRate, decoded.SampleRate);
        Assert.AreEqual(expectedChannels, decoded.ChannelCount);
        Assert.IsTrue(decoded.FrameCount > 0, "The production decoder returned no PCM frames.");

        double peak = 0d;
        int sampleCount = checked((int)(decoded.FrameCount * decoded.ChannelCount));
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            float sample = decoded.GetSample(sampleIndex);
            Assert.IsTrue(float.IsFinite(sample), "The production decoder returned a non-finite sample.");
            peak = Math.Max(peak, Math.Abs((double)sample));
        }

        Assert.IsTrue(peak > 0.1d, "The production decoder returned a silent stream.");
    }

    private static void AssertDecodedWave(
        AudioTestWaveFile wave,
        int expectedSampleRate,
        int expectedChannels,
        ushort expectedFormat,
        ushort expectedBits)
    {
        Assert.AreEqual(expectedFormat, wave.Format);
        Assert.AreEqual(expectedBits, wave.BitsPerSample);
        Assert.AreEqual(expectedChannels, wave.Channels);
        Assert.AreEqual(expectedSampleRate, wave.SampleRate);

        int bytesPerSample = expectedBits / 8;
        Assert.IsTrue(wave.DataLength > 0, "The decoded WAV contains no PCM frames.");
        Assert.AreEqual(0, wave.DataLength % (expectedChannels * bytesPerSample));

        double peak = 0d;
        for (int offset = wave.DataOffset; offset < wave.DataOffset + wave.DataLength; offset += bytesPerSample)
        {
            double sample = expectedFormat == 3
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                    wave.Bytes.AsSpan(offset, sizeof(float))))
                : ReadIntegerPcmSample(wave.Bytes.AsSpan(offset, bytesPerSample), expectedBits);
            Assert.IsTrue(double.IsFinite(sample), "The decoder returned a non-finite sample.");
            peak = Math.Max(peak, Math.Abs(sample));
        }

        Assert.IsTrue(peak > 0.1d, "The decoded stream is silent.");
    }

    private static double ReadIntegerPcmSample(ReadOnlySpan<byte> sample, ushort bitsPerSample)
    {
        return bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
            24 => ReadSigned24(sample) / 8388608d,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d,
            _ => throw new AssertFailedException("The decoded WAV uses an unsupported integer PCM depth.")
        };
    }

    private static int ReadSigned24(ReadOnlySpan<byte> sample)
    {
        int value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xFF00_0000);
    }

    private static async Task RunDecoderProcessAsync(string executable, TestWorkspace workspace, ProcessDeadline suiteDeadline, params string[] arguments)
    {
        ProcessDeadline deadline = suiteDeadline.LimitRemaining(TimeSpan.FromSeconds(30));
        ProcessDeadline? cleanupDeadline = null;
        ProcessDeadline BeginCleanup() => cleanupDeadline ??= suiteDeadline.AddGrace(TimeSpan.FromSeconds(10)).LimitRemaining(TimeSpan.FromSeconds(10));
        OwnedProcess process;
        try
        {
            process = OwnedProcess.Start(new ProcessStartRequest(executable, arguments, workspace.RootDirectory), BeginCleanup);
        }
        catch (OwnedProcessStartException failure) when (failure.CleanupFailure is not null)
        {
            workspace.PreserveArtifacts();
            throw;
        }
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            ProcessCompletion completion = await process.WaitForExitAsync(deadline);
            Assert.AreEqual(0, completion.ExitCode, Path.GetFileName(executable) + " rejected the encoded audio output."
                + Environment.NewLine + completion.StandardOutput + Environment.NewLine + completion.StandardError);
        }
        catch (Exception failure)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(failure);
            BeginCleanup();
        }
        finally
        {
            try
            {
                ProcessStopResult stopped = await process.StopAsync(null, primaryFailure is null ? deadline : BeginCleanup());
                if (stopped.CleanupFailure is not null) { CaptureFailure(ref primaryFailure, stopped.CleanupFailure); }
            }
            catch (Exception failure) { CaptureFailure(ref primaryFailure, failure); }
            if (!process.Completion.IsCompletedSuccessfully) { workspace.PreserveArtifacts(); }
            try { await process.DisposeAsync(); }
            catch (Exception failure)
            {
                workspace.PreserveArtifacts();
                CaptureFailure(ref primaryFailure, failure);
            }
        }
        primaryFailure?.Throw();
    }

    private static bool HasMpegFrameSync(byte[] data)
    {
        for (int index = 0; index < data.Length - 1; index++)
        {
            if (data[index] == 0xFF && (data[index + 1] & 0xE0) == 0xE0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPacketMarker(byte[] data, byte packetType, string marker)
    {
        byte[] markerBytes = Encoding.ASCII.GetBytes(marker);
        for (int index = 0; index <= data.Length - markerBytes.Length - 1; index++)
        {
            if (data[index] == packetType && HasAsciiAt(data, index + 1, marker))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAscii(byte[] data, string value)
    {
        byte[] marker = Encoding.ASCII.GetBytes(value);
        for (int index = 0; index <= data.Length - marker.Length; index++)
        {
            if (HasAsciiAt(data, index, value))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindAscii(byte[] data, string value)
    {
        for (int index = 0; index <= data.Length - value.Length; index++)
        {
            if (HasAsciiAt(data, index, value))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasAsciiAt(byte[] data, int offset, string value)
    {
        byte[] marker = Encoding.ASCII.GetBytes(value);
        if (offset < 0 || offset + marker.Length > data.Length)
        {
            return false;
        }

        for (int index = 0; index < marker.Length; index++)
        {
            if (data[offset + index] != marker[index])
            {
                return false;
            }
        }

        return true;
    }

    private static float[] CreateDeterministicPcm(int sampleRate, int frameCount, int channelCount)
    {
        float[] pcm = new float[checked(frameCount * channelCount)];

        for (int frame = 0; frame < frameCount; frame++)
        {
            float sample = (float)(Math.Sin(2d * Math.PI * 440d * frame / sampleRate) * 0.35d);
            for (int channel = 0; channel < channelCount; channel++)
            {
                pcm[frame * channelCount + channel] = sample;
            }
        }

        return pcm;
    }

    private static void CaptureCleanup(ref ExceptionDispatchInfo? primaryFailure, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception failure) { CaptureFailure(ref primaryFailure, failure); }
    }

    private static void CaptureFailure(ref ExceptionDispatchInfo? primaryFailure, Exception failure)
    {
        if (primaryFailure is null) { primaryFailure = ExceptionDispatchInfo.Capture(failure); return; }
        const string key = "AudioSmoke.CleanupFailures";
        if (primaryFailure.SourceException.Data[key] is not List<Exception> failures)
        {
            failures = [];
            primaryFailure.SourceException.Data[key] = failures;
        }
        failures.Add(failure);
    }

    private static void PreserveUnreleasedSession(ref ExceptionDispatchInfo? failure, ManagedBassWriterSession session, TestWorkspace workspace)
    {
        if (session.IsReleased) { return; }
        workspace.PreserveArtifacts();
        failure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException("The audio session remains owned."));
        failure.SourceException.Data["AudioSmoke.SessionOwner"] = session;
    }

    private sealed class ManagedBassWriterSession(TestWorkspace workspace, ProcessDeadline suiteDeadline)
    {
        /// <summary>encoder停止後のplayerとruntimeの解放を確認した場合だけ真です。</summary>
        internal bool IsReleased { get; private set; }

        /// <summary>呼出元が先に所有したhelperで初期化し、途中失敗も同じfinallyで回収します。</summary>
        internal void Initialize()
        {
            BassAudioRuntime.Shutdown();
            BassAudioRuntime.Initialize();
            BassAudioWriter.Initialize();
        }

        /// <summary>本体の主原因を維持して解放失敗を追加し、実owner未解放だけを専有process終了へ接続します。</summary>
        internal void Dispose(ref ExceptionDispatchInfo? failure)
        {
            if (IsReleased) { return; }
            CaptureCleanup(ref failure, () =>
            {
                if (BassAudioWriter.RecordState == PlayState.Playing) { BassAudioWriter.StopRecording(); }
            });
            bool? encoderReleased = null;
            CaptureCleanup(ref failure, () =>
            {
                encoderReleased = BassAudioWriter.TryReleaseEncoder();
                if (encoderReleased == false) { throw new InvalidOperationException("The external encoder owner did not release."); }
            });
            // 停止失敗は解放済みではありません。元source/sessionを保持して失敗とともに返します。
            if (encoderReleased == false)
            {
                AudioEncoderTestTermination.Complete(AudioEncoderCleanupObservation.WriterOwnerUnreleased,
                    workspace, suiteDeadline, failure?.SourceException, "TryReleaseEncoder=false; writer owner release unconfirmed");
            }
            if (encoderReleased != true) { return; }
            bool playerFreed = false;
            bool runtimeStopped = false;
            CaptureCleanup(ref failure, () => { BassAudioPlayer.Free(); playerFreed = true; });
            CaptureCleanup(ref failure, () => { BassAudioRuntime.Shutdown(); runtimeStopped = true; });
            IsReleased = playerFreed && runtimeStopped;
        }
    }
}
