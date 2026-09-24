using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BMSAutoPlayerInputTests
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
    public void LoadResources_LoadsUsedAudioOnlyAndKeepsMissingFilesOptional()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("unused-broken.wav"), [0x01, 0x02, 0x03]);
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#WAV01 unused-broken.wav\n"
            + "#WAV02 used.wav\n"
            + "#WAV03 missing.wav\n"
            + "#00111:0203\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));

        try
        {
            player.LoadResources(asParallel: true);

            Assert.IsNull(player.GetAudioPlayer(1), "An unused malformed definition must not be decoded.");
            Assert.IsNotNull(player.GetAudioPlayer(2), "The used audio file must be loaded.");
            Assert.IsNull(player.GetAudioPlayer(3), "A missing used file keeps its legacy optional behavior.");
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    [TestMethod]
    public void LoadResources_ReportsFailureForAnExistingUsedAudioFile()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("broken.wav"), [0x01, 0x02, 0x03]);
        WriteChart(directory.File("chart.bms"),
            "#WAV02 broken.wav\n"
            + "#00111:02\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));

        try
        {
            InvalidDataException failure = Assert.ThrowsException<InvalidDataException>(
                () => player.LoadResources(asParallel: false));

            StringAssert.Contains(failure.Message, "broken.wav");
            StringAssert.Contains(failure.Message, nameof(AudioSourceLoadStage.DecodeWithBass));
            Assert.IsInstanceOfType<AudioSourceLoadException>(failure.InnerException);
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    private static void WriteChart(string path, string body)
    {
        File.WriteAllText(
            path,
            "#PLAYER 1\n#TITLE audio input\n#ARTIST test\n" + body,
            Encoding.ASCII);
    }

    private static byte[] BuildPcmWave()
    {
        byte[] wave = new byte[46];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), 38);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 88200);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44), 16384);
        return wave;
    }

    private sealed class TestBMSAutoPlayer(BMSFile bms) : BMSAutoPlayer<BassAudioPlayer>(bms)
    {
        internal BassAudioPlayer? GetAudioPlayer(int index) => AudioPlayers[index];

        internal void DisposeLoadedAudio()
        {
            foreach (BassAudioPlayer? player in AudioPlayers)
            {
                player?.Dispose();
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory()
        {
            Directory.CreateDirectory(path);
        }

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose()
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
