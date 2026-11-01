using System.Runtime.ExceptionServices;
using System.Text;
using BeMusicSeeker.TestSupport.Files;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数moduleで共有する既存音声テストの準備・観測境界を一か所で所有します。</summary>
internal static class AudioMixerSignalFixture
{
    /// <summary>同じ入力区間へのsin・cos射影から振幅を求め、性能検査と信号検査で同じ観測を使います。</summary>
    internal static (double SineAmplitude, double CosineAmplitude) MeasureProjectedComponents(
        float[] interleavedStereo,
        int sampleRate,
        int frequency,
        int startFrame,
        int frameCount)
    {
        if (interleavedStereo.Length % 2 != 0
            || startFrame < 0
            || frameCount <= 0
            || (long)(startFrame + frameCount) * 2 > interleavedStereo.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        double sineProjection = 0d;
        double cosineProjection = 0d;
        double sineNorm = 0d;
        double cosineNorm = 0d;
        for (int frame = startFrame; frame < startFrame + frameCount; frame++)
        {
            double phase = 2d * Math.PI * frequency * frame / sampleRate;
            double sine = Math.Sin(phase);
            double cosine = Math.Cos(phase);
            double sample = interleavedStereo[frame * 2];
            sineProjection += sample * sine;
            cosineProjection += sample * cosine;
            sineNorm += sine * sine;
            cosineNorm += cosine * cosine;
        }

        double sineAmplitude = sineProjection / sineNorm;
        double cosineAmplitude = cosineProjection / cosineNorm;
        return (sineAmplitude, cosineAmplitude);
    }

    /// <summary>実NULL_DEVICEのmixer・player・PCM pullと元の解放順を共有します。</summary>
    internal sealed class NativeAudioGraph : IDisposable
    {
        private const int PullBufferFrames = 16384;

        private readonly SampleRate previousFrequency;
        private readonly SampleFormat previousFormat;
        private readonly float previousDefaultVolume;
        private readonly float previousDeviceVolume;
        private readonly bool previousMute;
        private readonly List<BassAudioPlayer> players = [];
        private readonly float[] pullBuffer = new float[PullBufferFrames * 2];
        private BassAudioSession? session;
        private bool disposed;

        private NativeAudioGraph(int outputRate, int sampleRateConversionQuality)
        {
            previousFrequency = BassAudioPlayer.Frequency;
            previousFormat = BassAudioPlayer.Format;
            previousDefaultVolume = BassAudioPlayer.DefaultVolume;
            previousDeviceVolume = BassAudioPlayer.DeviceVolume;
            previousMute = BassAudioPlayer.IsDeviceMuted;

            try
            {
                BassAudioPlayer.Free();
                if (BassAudioPlayer.ActiveSession != null)
                {
                    throw new InvalidOperationException("A previous native audio session did not release.");
                }
                BassAudioRuntime.Shutdown();
                BassAudioPlayer.Frequency = (SampleRate)outputRate;
                BassAudioPlayer.Format = SampleFormat.SAMPLE_FLOAT_32BIT;
                BassAudioPlayer.InitializeOwned(
                    BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                    default,
                    0f,
                    out session,
                    sampleRateConversionQuality);
                BassAudioPlayer.DefaultVolume = 1f;
                BassAudioPlayer.DeviceVolume = 1f;
                BassAudioPlayer.IsDeviceMuted = false;

                BassAudioSession initializedSession = session
                    ?? throw new InvalidOperationException("Null-device initialization returned no owning session.");
                ChannelInfo mixer = Bass.ChannelGetInfo(initializedSession.MixerHandle);
                if (mixer.Frequency != outputRate || mixer.Channels != 2)
                {
                    throw new InvalidOperationException(
                        $"The null mixer exposed {mixer.Frequency} Hz / {mixer.Channels} channels; expected {outputRate} Hz / 2 channels.");
                }
                Renderer = new AudioPcmRenderer(initializedSession.MixerHandle, mixer.Frequency, mixer.Channels);
            }
            catch (Exception initializationFailure)
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Native audio graph initialization and cleanup both failed.", initializationFailure, cleanupFailure);
                }
                ExceptionDispatchInfo.Capture(initializationFailure).Throw();
                throw;
            }
        }

        internal AudioPcmRenderer Renderer { get; }

        internal int SampleRateConversionQuality => session?.SampleRateConversionQuality
            ?? throw new InvalidOperationException("The native audio graph has no active session.");

        internal static NativeAudioGraph Start(
            int outputRate,
            int sampleRateConversionQuality = AudioResamplingQuality.Default)
            => new(outputRate, sampleRateConversionQuality);

        internal BassAudioPlayer CreatePlayer(string path)
        {
            BassAudioPlayer player = new(path);
            players.Add(player);
            return player;
        }

        internal float[] ReadFrames(int frameCount)
        {
            float[] output = new float[checked(frameCount * 2)];
            Renderer.ReadFramesExactly(output, 0, frameCount);
            return output;
        }

        internal void DiscardFramesExactly(long frameCount, int framesPerRead = PullBufferFrames)
        {
            if (framesPerRead <= 0 || framesPerRead > PullBufferFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(framesPerRead));
            }

            long remaining = frameCount;
            while (remaining > 0)
            {
                int frames = checked((int)Math.Min(remaining, framesPerRead));
                Renderer.ReadFramesExactly(pullBuffer, 0, frames);
                remaining -= frames;
            }
        }

        internal void DisposePlayer(BassAudioPlayer player)
        {
            players.Remove(player);
            player.Dispose();
        }

        internal void DisposePlayers()
        {
            for (int index = players.Count - 1; index >= 0; index--)
            {
                DisposePlayer(players[index]);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            Exception? cleanupFailure = null;
            for (int index = players.Count - 1; index >= 0; index--)
            {
                try
                {
                    players[index].Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
            players.Clear();

            bool sessionReleased = false;
            try
            {
                if (session == null)
                {
                    BassAudioPlayer.Free();
                }
                bool released = session == null ? BassAudioPlayer.ActiveSession == null : BassAudioPlayer.Free(session);
                if (!released)
                {
                    cleanupFailure ??= new InvalidOperationException("The native mixer test session did not release.");
                }
                else
                {
                    sessionReleased = true;
                    session = null;
                }
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }

            try
            {
                if (sessionReleased)
                {
                    BassAudioRuntime.Shutdown();
                }
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
            finally
            {
                BassAudioPlayer.IsDeviceMuted = previousMute;
                BassAudioPlayer.DeviceVolume = previousDeviceVolume;
                BassAudioPlayer.DefaultVolume = previousDefaultVolume;
                BassAudioPlayer.Frequency = previousFrequency;
                BassAudioPlayer.Format = previousFormat;
            }

            if (cleanupFailure != null)
            {
                throw new InvalidOperationException("Native mixer test cleanup failed.", cleanupFailure);
            }
        }
    }

    /// <summary>既存のmono float WAV形式をケース専有領域へ生成し、外側fixtureとして最終回収を試みます。</summary>
    internal sealed class TemporaryFloatWave : IDisposable
    {
        private readonly TestWorkspace workspace;

        private TemporaryFloatWave(string path, TestWorkspace workspace)
        {
            Path = path;
            this.workspace = workspace;
        }

        internal string Path { get; }

        internal static TemporaryFloatWave Create(int sampleRate, long frameCount, Func<long, float> sample)
        {
            ArgumentNullException.ThrowIfNull(sample);
            long dataLength = checked(frameCount * sizeof(float));
            if (sampleRate <= 0
                || frameCount < 0
                || dataLength > uint.MaxValue - 48L)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            }

            var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string path = System.IO.Path.Combine(
                workspace.RootDirectory,
                "BeMusicSeeker.AudioMixerSignal." + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                using FileStream stream = File.Create(path);
                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(checked((uint)(48L + dataLength)));
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                WriteChunkHeader(writer, "fmt ", 16);
                writer.Write((ushort)3);
                writer.Write((ushort)1);
                writer.Write(sampleRate);
                writer.Write(checked(sampleRate * sizeof(float)));
                writer.Write((ushort)sizeof(float));
                writer.Write((ushort)32);
                WriteChunkHeader(writer, "fact", 4);
                writer.Write(checked((uint)frameCount));
                WriteChunkHeader(writer, "data", checked((uint)dataLength));
                for (long frame = 0; frame < frameCount; frame++)
                {
                    float value = sample(frame);
                    if (!float.IsFinite(value))
                    {
                        throw new ArgumentException("A generated float WAVE fixture must contain only finite samples.", nameof(sample));
                    }
                    writer.Write(value);
                }
            }
            catch
            {
                try { File.Delete(path); }
                finally { workspace.Dispose(); }
                throw;
            }

            return new TemporaryFloatWave(path, workspace);
        }

        private static void WriteChunkHeader(BinaryWriter writer, string id, uint length)
        {
            writer.Write(Encoding.ASCII.GetBytes(id));
            writer.Write(length);
        }

        public void Dispose()
        {
            try { File.Delete(Path); }
            finally { workspace.Dispose(); }
        }
    }
}
