using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using BeMusicSeeker.Models;
using ManagedBass.Enc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Exercises the production audio writer with real external encoder processes when explicitly
/// enabled by the test environment.
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

    /// <summary>
    /// Converts deterministic stereo PCM through every available requested production encoder
    /// and validates each container or frame signature.  The test is opt-in because encoder
    /// binaries are external user-provided tools, not repository or package assets.
    /// </summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    public void AvailableExternalEncodersProduceRecognizableOutput()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)?.Trim(),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                EnabledEnvironmentVariable + "=1 is required for the external encoder smoke test.");
            return;
        }

        string? configuredDirectory = GetOptionalEnvironmentValue(EncoderDirectoryEnvironmentVariable);
        IReadOnlyList<EncoderType> requestedTypes = ReadRequestedEncoderTypes();
        EncoderType[] candidateTypes = requestedTypes.Count == 0
            ? SupportedEncoderTypes
            : requestedTypes.ToArray();
        var discovered = candidateTypes
            .Select(encoderType =>
            {
                string directory = encoderType.SearchEncoderBinary(configuredDirectory);
                return (EncoderType: encoderType, Directory: directory);
            })
            .ToArray();
        var missing = discovered
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

        var available = discovered
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
            RunSmoke(available);
        }
        finally
        {
            BassAudioWriter.EncoderDirectory = previousEncoderDirectory;
        }
    }

    private static IReadOnlyList<EncoderType> ReadRequestedEncoderTypes()
    {
        string value = GetOptionalEnvironmentValue(EncoderTypesEnvironmentVariable);
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
        var directories = new[]
        {
            configuredDirectory,
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "libs", "x64"),
            Path.Combine(AppContext.BaseDirectory, "x64")
        };

        return directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void RunSmoke(IReadOnlyList<EncoderType> encoderTypes)
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerExternalEncoderSmoke",
            Guid.NewGuid().ToString("N"));
        string inputPath = Path.Combine(temporaryDirectory, "入力 音声 (space).wav");

        ManagedBassWriterSession? session = null;
        BassAudioPlayer? source = null;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            WriteDeterministicWave(inputPath);
            session = ManagedBassWriterSession.Start();
            source = new BassAudioPlayer(inputPath, onMemory: false);

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
                    Path.GetFileName(inputPath),
                    "ManagedBass external encoder smoke"));
                source.Play();
                BassAudioWriter.StartRecording();
                BassAudioWriter.RecordToFile(TimeSpan.FromMilliseconds(100));
                BassAudioWriter.StopRecording();

                string outputPath = outputWithoutExtension + " (2)" + extension;
                Assert.IsTrue(File.Exists(outputPath), "Encoder did not create " + outputPath);
                Assert.IsTrue(new FileInfo(outputPath).Length > 0, "Encoder created an empty output.");
                CollectionAssert.AreEqual(
                    new byte[] { 0xC0, 0xFF, 0xEE },
                    File.ReadAllBytes(collisionPath),
                    "The existing output was overwritten instead of receiving a collision suffix.");
                AssertOutputSignature(encoderType, outputPath);
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
            CaptureCleanup(ref primaryFailure, () => source?.Stop());
            CaptureCleanup(ref primaryFailure, () => source?.Dispose());
            CaptureCleanup(ref primaryFailure, () => session?.Dispose());
            CaptureCleanup(ref primaryFailure, () => Directory.Delete(temporaryDirectory, recursive: true));
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

    private static void AssertOutputSignature(EncoderType encoderType, string outputPath)
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

    private static void WriteDeterministicWave(string filePath)
    {
        const int sampleRate = 44100;
        const short channels = 2;
        const short bitsPerSample = 16;
        const int frameCount = sampleRate * 2;
        const int blockAlign = channels * (bitsPerSample / 8);
        byte[] pcm = new byte[frameCount * blockAlign];

        for (int frame = 0; frame < frameCount; frame++)
        {
            short sample = (short)(Math.Sin(2d * Math.PI * 440d * frame / sampleRate) * 12000d);
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = (frame * channels + channel) * sizeof(short);
                pcm[offset] = (byte)(sample & 0xFF);
                pcm[offset + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        using FileStream stream = File.Create(filePath);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }

    private static void CaptureCleanup(ref ExceptionDispatchInfo? primaryFailure, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
        }
    }

    private sealed class ManagedBassWriterSession : IDisposable
    {
        private bool disposed;

        private ManagedBassWriterSession()
        {
        }

        internal static ManagedBassWriterSession Start()
        {
            try
            {
                BassAudioRuntime.Shutdown();
                BassAudioRuntime.Initialize();
                BassAudioWriter.Initialize();
                return new ManagedBassWriterSession();
            }
            catch
            {
                ExceptionDispatchInfo? cleanupFailure = null;
                CaptureCleanup(ref cleanupFailure, () =>
                {
                    if (!BassAudioWriter.TryReleaseEncoder())
                    {
                        throw new InvalidOperationException("The external encoder owner did not release.");
                    }
                });
                CaptureCleanup(ref cleanupFailure, () => BassAudioPlayer.Free());
                CaptureCleanup(ref cleanupFailure, () => BassAudioRuntime.Shutdown());
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ExceptionDispatchInfo? failure = null;
            CaptureCleanup(ref failure, () =>
            {
                if (BassAudioWriter.RecordState == PlayState.Playing)
                {
                    BassAudioWriter.StopRecording();
                }
            });
            CaptureCleanup(ref failure, () =>
            {
                if (!BassAudioWriter.TryReleaseEncoder())
                {
                    throw new InvalidOperationException("The external encoder owner did not release.");
                }
            });
            CaptureCleanup(ref failure, () => BassAudioPlayer.Free());
            CaptureCleanup(ref failure, () => BassAudioRuntime.Shutdown());
            failure?.Throw();
        }
    }
}
