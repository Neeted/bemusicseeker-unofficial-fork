using System.IO;
using System.Text;
using BeMusicSeeker.TestSupport.Files;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassMixerSourceControllerTests
{
    /// <summary>実6chの非対称impulseを左右へdownmixし、再演奏でもnative行列の行・speaker順の交換を検出する。</summary>
    /// <remarks>実NULL_DEVICEのmixer出力を読み、native行列接続の誤りを検査するため共有native状態をこのケースだけ直列所有する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void NativeMixer_AsymmetricSixChannelImpulsesUseNativeOutputRowsAndWaveSpeakerOrder()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        SampleFormat previousFormat = BassAudioPlayer.Format;
        float previousDefaultVolume = BassAudioPlayer.DefaultVolume;
        float previousDeviceVolume = BassAudioPlayer.DeviceVolume;
        bool previousMute = BassAudioPlayer.IsDeviceMuted;
        string path = Path.Combine(
            workspace.RootDirectory,
            "BeMusicSeeker.MixerMatrix." + Guid.NewGuid().ToString("N") + ".wav");
        BassAudioSession? session = null;
        BassAudioPlayer? player = null;
        try
        {
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_48000Hz;
            BassAudioPlayer.Format = SampleFormat.SAMPLE_FLOAT_32BIT;
            BassAudioPlayer.DefaultVolume = 1f;
            BassAudioPlayer.DeviceVolume = 1f;
            BassAudioPlayer.IsDeviceMuted = false;
            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out session);
            BassAudioPlayer.DefaultVolume = 1f;

            int mixerFrequency = Bass.ChannelGetInfo(session.MixerHandle).Frequency;
            WriteSixChannelImpulseWave(path, mixerFrequency);
            player = new BassAudioPlayer(path);
            player.Play();

            byte[] output = new byte[8 * sizeof(float)];
            int bytesRead = Bass.ChannelGetData(session.MixerHandle, output, output.Length);
            Assert.IsTrue(bytesRead >= 2 * sizeof(float), Bass.LastError.ToString());
            float[] expected =
            [
                0.5f * 0.70710677f, 0f,
                0f, 0.5f * 0.70710677f,
                0f, 0f,
                0.5f * 0.70710677f, 0.5f * 0.70710677f
            ];
            for (int playback = 0; playback < 2; playback++)
            {
                if (playback != 0)
                {
                    player.Play();
                    Assert.AreEqual(output.Length, Bass.ChannelGetData(session.MixerHandle, output, output.Length));
                }
                for (int sample = 0; sample < expected.Length; sample++)
                {
                    float actual = BitConverter.Int32BitsToSingle(
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                            output.AsSpan(sample * sizeof(float), sizeof(float))));
                    Assert.AreEqual(expected[sample], actual, 0.00001f, $"playback {playback}, output sample {sample}");
                }
            }
        }
        finally
        {
            try
            {
                player?.Dispose();
            }
            finally
            {
                try
                {
                    if (session == null)
                    {
                        BassAudioPlayer.Free();
                    }
                    else if (!BassAudioPlayer.Free(session))
                    {
                        throw new InvalidOperationException("The native mixer test session did not release.");
                    }
                }
                finally
                {
                    try
                    {
                        BassAudioRuntime.Shutdown();
                    }
                    finally
                    {
                        File.Delete(path);
                        BassAudioPlayer.IsDeviceMuted = previousMute;
                        BassAudioPlayer.DeviceVolume = previousDeviceVolume;
                        BassAudioPlayer.DefaultVolume = previousDefaultVolume;
                        BassAudioPlayer.Frequency = previousFrequency;
                        BassAudioPlayer.Format = previousFormat;
                    }
                }
            }
        }
    }

    private static void WriteSixChannelImpulseWave(string path, int sampleRate)
    {
        const int channelCount = 6;
        const int frameCount = 1024;
        const int bytesPerSample = sizeof(float);
        int blockAlign = channelCount * bytesPerSample;
        int dataLength = frameCount * blockAlign;
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(72 + dataLength));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(40);
        writer.Write((ushort)0xFFFE);
        writer.Write((ushort)channelCount);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * blockAlign));
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)32);
        writer.Write((ushort)22);
        writer.Write((ushort)32);
        writer.Write(0x0000003Fu);
        writer.Write(new byte[]
        {
            0x03, 0, 0, 0, 0, 0, 0x10, 0,
            0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71
        });
        writer.Write(Encoding.ASCII.GetBytes("fact"));
        writer.Write(4);
        writer.Write(frameCount);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        for (int frame = 0; frame < frameCount; frame++)
        {
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(frame == 3 ? 0.5f : 0f);
            writer.Write(frame == 2 ? 0.5f : 0f);
            writer.Write(frame == 0 ? 0.5f : 0f);
            writer.Write(frame == 1 ? 0.5f : 0f);
        }
    }
}
