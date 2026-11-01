using System.Text;
using ManagedBass;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数moduleで共有する既存音声テストの準備・観測境界を一か所で所有します。</summary>
internal static class ScheduledAudioFixture
{
    /// <summary>実scheduled mixerへ委譲し、指定したnative操作だけに既存の故障を注入します。</summary>
    internal sealed class FaultInjectingScheduledNativeBoundary(
        int failResumeAt,
        bool failNextUnlock,
        int failSetPositionAt = 0,
        bool failRemoveChannel = false,
        bool failStreamFree = false,
        int failGetPositionAt = 0,
        int failNextGetPositionAt = 0,
        int failRemoveChannelAt = 0,
        Action<int>? afterSetPosition = null) : IBassScheduledMixerNativeBoundary
    {
        private const BassFlags PauseFlag = BassFlags.MixerChanPause;
        private readonly BassMixerSourceNativeBoundary inner = new();
        private int resumeCount;
        private int setPositionCount;
        private int getPositionCalls;
        private int removeChannelCalls;
        private int streamFreeCalls;
        private int failUnlock = failNextUnlock ? 1 : 0;
        private int failNextResume;
        private Errors injectedError = Errors.OK;

        internal int RemoveChannelCalls => Volatile.Read(ref removeChannelCalls);

        internal int StreamFreeCalls => Volatile.Read(ref streamFreeCalls);

        internal int GetPositionCalls => Volatile.Read(ref getPositionCalls);

        internal int SetPositionCalls => Volatile.Read(ref setPositionCount);

        internal void FailNextResume() => Interlocked.Exchange(ref failNextResume, 1);

        public int GetMixer(int sourceHandle) => inner.GetMixer(sourceHandle);

        public bool AddChannel(int mixerHandle, int sourceHandle, BassFlags flags) =>
            inner.AddChannel(mixerHandle, sourceHandle, flags);

        public bool AddChannelAt(int mixerHandle, int sourceHandle, BassFlags flags, long startBytes, long lengthBytes) =>
            inner.AddChannelAt(mixerHandle, sourceHandle, flags, startBytes, lengthBytes);

        public bool LockChannel(int mixerHandle, bool locked)
        {
            bool result = inner.LockChannel(mixerHandle, locked);
            return result
                && (locked || Interlocked.Exchange(ref failUnlock, 0) == 0 || Inject(Errors.Device));
        }

        public long GetPosition(int mixerHandle, PositionFlags mode)
        {
            int call = Interlocked.Increment(ref getPositionCalls);
            if (call == failGetPositionAt || call == failNextGetPositionAt)
            {
                injectedError = Errors.Device;
                return -1;
            }
            return inner.GetPosition(mixerHandle, mode);
        }

        public BassFlags SetMixerChannelFlags(int sourceHandle, BassFlags flags, BassFlags mask)
        {
            if (flags == BassFlags.Default && mask == PauseFlag)
            {
                int call = Interlocked.Increment(ref resumeCount);
                if (call == failResumeAt || Interlocked.Exchange(ref failNextResume, 0) != 0)
                {
                    injectedError = Errors.Device;
                    return unchecked((BassFlags)(-1));
                }
            }
            return inner.SetMixerChannelFlags(sourceHandle, flags, mask);
        }

        public BassMixerChannelInfo GetChannelInfo(int channelHandle) => inner.GetChannelInfo(channelHandle);

        public bool SetSampleRateConversion(int sourceHandle, float quality) =>
            inner.SetSampleRateConversion(sourceHandle, quality);

        public bool GetSampleRateConversion(int sourceHandle, out float quality) =>
            inner.GetSampleRateConversion(sourceHandle, out quality);

        public bool SetMatrix(int sourceHandle, float[,] matrix) => inner.SetMatrix(sourceHandle, matrix);

        public bool RemoveChannel(int sourceHandle)
        {
            int call = Interlocked.Increment(ref removeChannelCalls);
            if (failRemoveChannel || call == failRemoveChannelAt)
            {
                return Inject(Errors.Device);
            }
            return inner.RemoveChannel(sourceHandle);
        }

        public bool FreeStream(int sourceHandle)
        {
            Interlocked.Increment(ref streamFreeCalls);
            if (failStreamFree)
            {
                return Inject(Errors.Device);
            }
            return inner.FreeStream(sourceHandle);
        }

        public bool SetPosition(int sourceHandle, long position, PositionFlags mode)
        {
            int call = Interlocked.Increment(ref setPositionCount);
            if (failSetPositionAt > 0 && call == failSetPositionAt)
            {
                return Inject(Errors.Device);
            }
            bool result = inner.SetPosition(sourceHandle, position, mode);
            if (result)
            {
                afterSetPosition?.Invoke(call);
            }
            return result;
        }

        public Errors GetError()
        {
            Errors error = injectedError;
            injectedError = Errors.OK;
            return error == Errors.OK ? inner.GetError() : error;
        }

        private bool Inject(Errors error)
        {
            injectedError = error;
            return false;
        }
    }

    /// <summary>既存のstereo float WAV入力を生成し、schedulerと再生契約の入力形式を揃えます。</summary>
    internal static byte[] BuildFloatWave(int sampleRate, float[] interleavedSamples)
    {
        int dataLength = checked(interleavedSamples.Length * sizeof(float));
        byte[] wave = new byte[checked(44 + dataLength)];
        using var stream = new MemoryStream(wave);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(36 + dataLength));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)3);
        writer.Write((ushort)2);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * 2 * sizeof(float)));
        writer.Write((ushort)(2 * sizeof(float)));
        writer.Write((ushort)(sizeof(float) * 8));
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (float sample in interleavedSamples)
        {
            writer.Write(sample);
        }
        return wave;
    }
}
