using ManagedBass;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数moduleで共有する既存音声テストの準備・観測境界を一か所で所有します。</summary>
internal static class CallbackPcmFixture
{
    /// <summary>callbackの管理PCM入出力だけを観測し、実nativeに接続しない既存の代替境界です。</summary>
    internal sealed class CallbackPcmNative : IAudioPcmNative
    {
        internal int LastHandle { get; private set; }
        internal bool Fail { get; set; }
        public Errors LastError => Fail ? Errors.Handle : Errors.OK;
        public int ChannelGetData(int channel, float[] buffer, int lengthBytes)
        {
            LastHandle = channel;
            if (Fail)
            {
                return -1;
            }
            // 一回に一frameだけ返し、callbackが短いreadを継続することも確認します。
            buffer[0] = 1.25f;
            buffer[1] = -1.25f;
            return 8;
        }
    }
}
