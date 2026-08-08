using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using Un4seen.Bass.AddOn.Tags;
using Un4seen.Bass.Misc;
using BassAudioRuntime = Ribbit.Media.Audio.BassAudioRuntime;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Fixes the observable BASS.NET behavior that the ManagedBass implementation must preserve.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BassNetMigrationCharacterizationTests
{
    [TestMethod]
    public void PersistedAudioValuesRemainStable()
    {
        AssertPersistedEnumValue(EncoderType.WAVE, 0);
        AssertPersistedEnumValue(EncoderType.MP3_LAME, 1);
        AssertPersistedEnumValue(EncoderType.AAC_NERO, 2);
        AssertPersistedEnumValue(EncoderType.OPUS, 3);
        AssertPersistedEnumValue(EncoderType.FLAC, 4);
        AssertPersistedEnumValue(EncoderType.OGG_VORBIS, 5);

        AssertPersistedEnumValue(SampleFormat.UNKNOWN, -1);
        AssertPersistedEnumValue(SampleFormat.AUTO, 0);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_8BIT, 1);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_16BIT, 2);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_24BIT, 3);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_32BIT, 4);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_FLOAT_32BIT, 5);

        AssertPersistedEnumValue(SampleRate.AUTO, 0);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_11025Hz, 11025);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_22050Hz, 22050);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_32000Hz, 32000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_44100Hz, 44100);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_48000Hz, 48000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_88200Hz, 88200);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_96000Hz, 96000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_176400Hz, 176400);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_192000Hz, 192000);

        AssertPersistedEnumValue(AudioDriver.Invalid, -2);
        AssertPersistedEnumValue(AudioDriver.NullDevice, -1);
        AssertPersistedEnumValue(AudioDriver.DirectSound, 0);
        AssertPersistedEnumValue(AudioDriver.WasapiShared, 1);
        AssertPersistedEnumValue(AudioDriver.WasapiExclusive, 2);
        AssertPersistedEnumValue(AudioDriver.Asio, 3);

        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.INVALID, -2);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.NULL_DEVICE, -1);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.DIRECT_SOUND, 0);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, 1);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE, 2);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.ASIO, 3);
    }

    [TestMethod]
    public void BassVersionPackingRoundTripsCurrentNativeVersions()
    {
        var expectedVersions = new Dictionary<uint, Version>
        {
            [0x02041203] = new(2, 4, 18, 3),
            [0x02040C00] = new(2, 4, 12, 0),
            [0x02041100] = new(2, 4, 17, 0),
            [0x02040401] = new(2, 4, 4, 1),
            [0x02040C06] = new(2, 4, 12, 6),
            [0x01040300] = new(1, 4, 3, 0)
        };

        foreach ((uint packed, Version expected) in expectedVersions)
        {
            Assert.AreEqual(expected, BassVersionPacking.Unpack(packed));
            Assert.AreEqual(packed, BassVersionPacking.Pack(expected));
        }

        Assert.AreEqual(new Version(2, 4, 18, 0), BassVersionPacking.Unpack(BassVersionPacking.Pack(new Version(2, 4, 18))));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => BassVersionPacking.Pack(new Version(2, 4, 256, 0)));
    }

    [TestMethod]
    public void EncoderExtensionsPreserveApplicationAndHelperContracts()
    {
        CollectionAssert.AreEqual(
            new[] { ".wav", ".mp3", ".aac", ".opus", ".flac", ".ogg" },
            Enum.GetValues<EncoderType>().Select(value => value.GetExtension()).ToArray());

        Assert.AreEqual(string.Empty, EncoderType.WAVE.SearchEncoderBinary());
        Assert.AreEqual(".m4a", new EncoderNeroAAC(0).DefaultOutputExtension);
        Assert.AreEqual(".wav", new EncoderWAV(0).DefaultOutputExtension);
        Assert.AreEqual(".mp3", new EncoderLAME(0).DefaultOutputExtension);
        Assert.AreEqual(".opus", new EncoderOPUS(0).DefaultOutputExtension);
        Assert.AreEqual(".flac", new EncoderFLAC(0).DefaultOutputExtension);
        Assert.AreEqual(".ogg", new EncoderOGG(0).DefaultOutputExtension);
    }

    [TestMethod]
    public void EncoderBinarySearchHonorsConfiguredDirectory()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerEncoderCharacterization", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            File.WriteAllText(Path.Combine(directoryPath, EncoderType.MP3_LAME.GetEncoderFileName()), string.Empty);

            Assert.AreEqual(directoryPath, EncoderType.MP3_LAME.SearchEncoderBinary(directoryPath));
            Assert.IsNull(EncoderType.OPUS.SearchEncoderBinary(directoryPath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EncoderQualityClampsPreserveProductionMappings()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerEncoderCharacterization", Guid.NewGuid().ToString("N"));
        string previousEncoderDirectory = BassAudioWriter.EncoderDirectory;
        Directory.CreateDirectory(directoryPath);
        foreach (string encoderFileName in new[] { "lame.exe", "neroAacEnc.exe", "opusenc.exe", "flac.exe", "oggenc2.exe" })
        {
            File.WriteAllText(Path.Combine(directoryPath, encoderFileName), string.Empty);
        }

        try
        {
            using RegistrationFreeWriterSession session = RegistrationFreeWriterSession.Start();
            BassAudioWriter.EncoderDirectory = directoryPath;

            BassAudioWriter.CreateEncoderLAME(Path.Combine(directoryPath, "lame-low"), quality: -1f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -V 9 ");
            BassAudioWriter.CreateEncoderLAME(Path.Combine(directoryPath, "lame-high"), quality: 2f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -V 0 ");

            BassAudioWriter.CreateEncoderNeroAAC(Path.Combine(directoryPath, "nero-low"), quality: -1f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -q 0.0 ");
            BassAudioWriter.CreateEncoderNeroAAC(Path.Combine(directoryPath, "nero-high"), quality: 2f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -q 1.0 ");

            BassAudioWriter.CreateEncoderOPUS(Path.Combine(directoryPath, "opus-low"), quality: -1f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--bitrate 6 ");
            BassAudioWriter.CreateEncoderOPUS(Path.Combine(directoryPath, "opus-high"), quality: 2f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--bitrate 256 ");

            BassAudioWriter.CreateEncoderFLAC(Path.Combine(directoryPath, "flac-low"), quality: -1f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--replay-gain -0 ");
            BassAudioWriter.CreateEncoderFLAC(Path.Combine(directoryPath, "flac-high"), quality: 2f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--replay-gain -8 ");

            BassAudioWriter.CreateEncoderOGG(Path.Combine(directoryPath, "ogg-low"), quality: -1f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -q 0.0 ");
            BassAudioWriter.CreateEncoderOGG(Path.Combine(directoryPath, "ogg-high"), quality: 2f);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -q 8.0 ");
        }
        finally
        {
            BassAudioWriter.EncoderDirectory = previousEncoderDirectory;
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CompressedOutputUsesTheExistingCollisionSuffixContract()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerEncoderCharacterization", Guid.NewGuid().ToString("N"));
        string previousEncoderDirectory = BassAudioWriter.EncoderDirectory;
        Directory.CreateDirectory(directoryPath);
        foreach (string encoderFileName in new[] { "lame.exe", "neroAacEnc.exe", "opusenc.exe", "flac.exe", "oggenc2.exe" })
        {
            File.WriteAllText(Path.Combine(directoryPath, encoderFileName), string.Empty);
        }

        var factories = new (string Name, string Extension, Action<string> Create)[]
        {
            ("lame", ".mp3", path => BassAudioWriter.CreateEncoderLAME(path)),
            ("nero", ".m4a", path => BassAudioWriter.CreateEncoderNeroAAC(path)),
            ("opus", ".opus", path => BassAudioWriter.CreateEncoderOPUS(path)),
            ("flac", ".flac", path => BassAudioWriter.CreateEncoderFLAC(path)),
            ("ogg", ".ogg", path => BassAudioWriter.CreateEncoderOGG(path))
        };

        try
        {
            using RegistrationFreeWriterSession session = RegistrationFreeWriterSession.Start();
            BassAudioWriter.EncoderDirectory = directoryPath;

            foreach ((string name, string extension, Action<string> create) in factories)
            {
                string outputWithoutExtension = Path.Combine(directoryPath, name);
                File.WriteAllText(outputWithoutExtension + extension, string.Empty);
                create(outputWithoutExtension);
                StringAssert.Contains(
                    BassAudioWriter.EncoderCommandLine,
                    "\"" + outputWithoutExtension + " (2)" + extension + "\"");
            }
        }
        finally
        {
            BassAudioWriter.EncoderDirectory = previousEncoderDirectory;
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void WavOutputUsesTheExistingCollisionSuffixContract()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerEncoderCharacterization", Guid.NewGuid().ToString("N"));
        string outputWithoutExtension = Path.Combine(directoryPath, "sample");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllText(outputWithoutExtension + ".wav", string.Empty);
        try
        {
            using RegistrationFreeWriterSession session = RegistrationFreeWriterSession.Start();
            BassAudioWriter.CreateEncoderWAV(outputWithoutExtension);

            StringAssert.EndsWith(BassAudioWriter.EncoderCommandLine, "sample (2).wav");
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EncoderCommandLinesPreserveQualityAndTagMapping()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            TAG_INFO tags = new()
            {
                title = "title value",
                artist = "artist value",
                genre = "genre value",
                comment = "comment value",
                bpm = "120"
            };

            var lame = new EncoderLAME(0)
            {
                EncoderDirectory = @"C:\encoder tools",
                InputFile = null,
                OutputFile = @"C:\output folder\sample file.mp3",
                LAME_UseVBR = true,
                LAME_ReplayGain = EncoderLAME.LAMEReplayGain.Accurate,
                LAME_VBRQuality = (EncoderLAME.LAMEVBRQuality)4,
                TAGs = tags
            };
            Assert.AreEqual(
                @"""C:\encoder tools\lame.exe"" -r -s 44.1 --bitwidth 16 -h --replaygain-accurate -V 4 --ignore-tag-errors --tt ""title value"" --ta ""artist value"" --tc ""comment value"" --tg ""genre value"" - ""C:\output folder\sample file.mp3""",
                lame.EncoderCommandLine);

            var nero = new EncoderNeroAAC(0)
            {
                EncoderDirectory = @"C:\encoder tools",
                InputFile = null,
                OutputFile = @"C:\output folder\sample file.m4a",
                NERO_UseQualityMode = true,
                NERO_Quality = 0.6f
            };
            Assert.AreEqual(
                @"""C:\encoder tools\neroAacEnc.exe"" -q 0.6 -if - -of ""C:\output folder\sample file.m4a""",
                nero.EncoderCommandLine);

            var opus = new EncoderOPUS(0)
            {
                EncoderDirectory = @"C:\encoder tools",
                InputFile = null,
                OutputFile = @"C:\output folder\sample file.opus",
                OPUS_Bitrate = 106
            };
            Assert.AreEqual(
                @"""C:\encoder tools\opusenc.exe"" --raw --raw-bits 16 --raw-rate 44100 --raw-chan 2 --ignorelength --bitrate 106 - ""C:\output folder\sample file.opus""",
                opus.EncoderCommandLine);

            var flac = new EncoderFLAC(0)
            {
                EncoderDirectory = @"C:\encoder tools",
                InputFile = null,
                OutputFile = @"C:\output folder\sample file.flac",
                FLAC_ReplayGain = true,
                FLAC_CompressionLevel = 4
            };
            Assert.AreEqual(
                @"""C:\encoder tools\flac.exe"" -f --force-raw-format --endian=little --sample-rate=44100 --channels=2 --bps=16 --sign=signed --replay-gain -4 -o ""C:\output folder\sample file.flac"" -- -",
                flac.EncoderCommandLine);

            var ogg = new EncoderOGG(0)
            {
                EncoderDirectory = @"C:\encoder tools",
                InputFile = null,
                OGG_UseQualityMode = true,
                OGG_Quality = 4
            };
            string commandBeforeOutput = ogg.EncoderCommandLine;
            ogg.OutputFile = @"C:\output folder\sample file.ogg";
            Assert.AreNotEqual(commandBeforeOutput, ogg.EncoderCommandLine);
            Assert.AreEqual(
                @"""C:\encoder tools\oggenc2.exe"" -r -F 1 -B 16 -C 2 -R 44100 -q 4.0 -o ""C:\output folder\sample file.ogg"" -",
                ogg.EncoderCommandLine);

            string commandBeforeTags = ogg.EncoderCommandLine;
            ogg.TAGs = tags;
            StringAssert.Contains(ogg.EncoderCommandLine, "--utf8 -t \"title value\" -a \"artist value\"");
            StringAssert.Contains(ogg.EncoderCommandLine, "-c \"COMMENT=comment value\" -c \"BPM=120\"");
            Assert.AreNotEqual(commandBeforeTags, ogg.EncoderCommandLine);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [TestMethod]
    public void ManagedBassEffectCatalogRetainsAllSupportedTypes()
    {
        IReadOnlyList<BassAudioEffectDefinition> definitions = BassAudioEffectCatalog.Definitions;

        Assert.AreEqual(32, definitions.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                BassAudioEffectType.Dx8Chorus,
                BassAudioEffectType.Dx8Compressor,
                BassAudioEffectType.Dx8Distortion,
                BassAudioEffectType.Dx8Echo,
                BassAudioEffectType.Dx8Flanger,
                BassAudioEffectType.Dx8Gargle,
                BassAudioEffectType.Dx8I3dl2Reverb,
                BassAudioEffectType.Dx8ParamEq,
                BassAudioEffectType.Dx8Reverb,
                BassAudioEffectType.BfxRotate,
                BassAudioEffectType.BfxEcho,
                BassAudioEffectType.BfxFlanger,
                BassAudioEffectType.BfxVolume,
                BassAudioEffectType.BfxPeakEq,
                BassAudioEffectType.BfxReverb,
                BassAudioEffectType.BfxLpf,
                BassAudioEffectType.BfxMix,
                BassAudioEffectType.BfxDamp,
                BassAudioEffectType.BfxAutoWah,
                BassAudioEffectType.BfxEcho2,
                BassAudioEffectType.BfxPhaser,
                BassAudioEffectType.BfxEcho3,
                BassAudioEffectType.BfxChorus,
                BassAudioEffectType.BfxApf,
                BassAudioEffectType.BfxCompressor,
                BassAudioEffectType.BfxDistortion,
                BassAudioEffectType.BfxCompressor2,
                BassAudioEffectType.BfxVolumeEnvelope,
                BassAudioEffectType.BfxBqf,
                BassAudioEffectType.BfxEcho4,
                BassAudioEffectType.BfxPitchShift,
                BassAudioEffectType.BfxFreeverb
            },
            definitions.Select(definition => definition.Type).ToArray());

        Assert.AreEqual(
            definitions.Count,
            definitions.Select(definition => definition.ParameterType).Distinct().Count());
    }

    [TestMethod]
    public void AudioWriterRejectsStopBeforeStart()
    {
        Assert.AreEqual(PlayState.Stopped, BassAudioWriter.RecordState);
        Assert.ThrowsException<InvalidOperationException>(BassAudioWriter.StopRecording);
        Assert.AreEqual(PlayState.Stopped, BassAudioWriter.RecordState);
    }

    [TestMethod]
    public void AudioWriterStartsAndStopsWavRecordingWithoutPhysicalDevice()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerEncoderCharacterization", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            using RegistrationFreeWriterSession session = RegistrationFreeWriterSession.Start();
            BassAudioWriter.CreateEncoderWAV(Path.Combine(directoryPath, "recording"));

            BassAudioWriter.StartRecording();
            Assert.AreEqual(PlayState.Playing, BassAudioWriter.RecordState);

            BassAudioWriter.StopRecording();
            Assert.AreEqual(PlayState.Stopped, BassAudioWriter.RecordState);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private static void AssertPersistedEnumValue<TEnum>(TEnum value, int expected)
        where TEnum : struct, Enum
    {
        Assert.AreEqual(expected, Convert.ToInt32(value, CultureInfo.InvariantCulture), value.ToString());
    }

    /// <summary>
    /// Owns one registration-free native runtime and ordinary null-device writer session.
    /// </summary>
    private sealed class RegistrationFreeWriterSession : IDisposable
    {
        private bool disposed;

        private RegistrationFreeWriterSession()
        {
        }

        internal static RegistrationFreeWriterSession Start()
        {
            try
            {
                BassAudioRuntime.Shutdown();
                BassAudioRuntime.InitializeWithoutWrapperRegistrationForCharacterization();
                BassAudioWriter.Initialize();
                return new RegistrationFreeWriterSession();
            }
            catch
            {
                try
                {
                    BassAudioPlayer.Free();
                }
                finally
                {
                    BassAudioRuntime.Shutdown();
                }

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
            try
            {
                if (BassAudioWriter.RecordState == PlayState.Playing)
                {
                    BassAudioWriter.StopRecording();
                }
            }
            finally
            {
                try
                {
                    BassAudioPlayer.Free();
                }
                finally
                {
                    BassAudioRuntime.Shutdown();
                }
            }
        }
    }
}
