using Ribbit.Media.Audio;

namespace BeMusicSeeker.Models;

/// <summary>同等性検査が、WAVとOGGを同じ形のPCMへ読む窓口です。</summary>
/// <remarks>
/// 戻り値が <c>null</c> のときは、音源の内容に起因して復号できないことを表します。ファイルを開く・読む段階の入出力の失敗は例外（<see cref="System.IO.IOException"/> など）のまま、
/// 音声ランタイムの初期化の失敗・致命的な失敗・メモリ不足などもそのまま呼出元へ伝えます。
/// </remarks>
internal static class DuplicateMergeAudioReader
{
    /// <summary>
    /// OGGの復号に必要な音声ランタイムを、検査の前に一度だけ用意します。復号は同梱ネイティブの読込みと版の検証を要しますが、出力デバイスは開きません。
    /// すでに使える状態なら何もしません。他の音声要求との競合や終了処理中の拒否は、例外として呼出元へ伝えます。
    /// </summary>
    internal static void EnsureRuntime()
    {
        if (BassAudioRuntime.TryEnterAudioOperation(out BassAudioOperationLease lease))
        {
            lease.Dispose();
            return;
        }
        BassAudioRuntime.Initialize();
    }

    /// <summary>WAVファイルを読みます。復号できないときはPCMが <c>null</c> で、ヘッダーは読めた範囲だけを持ちます。</summary>
    internal static DuplicateMergeWaveReadResult ReadWave(string path) => DuplicateMergeWaveReader.ReadFile(path);

    /// <summary>OGGファイルを再生と同じ復号器で復号します。<see cref="EnsureRuntime"/> の後に、複数のスレッドから呼べます。</summary>
    internal static DuplicateMergeAudioSamples ReadOgg(string path)
    {
        DecodedAudio audio;
        try
        {
            audio = VorbisDecoder.Decode(path);
        }
        catch (AudioSourceLoadException failure) when (failure.IsInputFailure && !IsReadFailure(failure))
        {
            return null;
        }

        int channels = audio.ChannelCount;
        float[] samples = new float[checked(audio.FrameCount * channels)];
        for (int index = 0; index < samples.Length; index++) { samples[index] = audio.GetSample(index); }
        return new DuplicateMergeAudioSamples(audio.SampleRate, channels, samples);
    }

    /// <summary>入力を開く・読む段階の入出力の失敗か（復号の失敗ではないか）を返します。<see cref="AudioSourceLoadException"/> は入出力の例外の派生で、読取りの失敗は内側に原因を持ちます。</summary>
    private static bool IsReadFailure(AudioSourceLoadException failure)
        => failure.Stage == AudioSourceLoadStage.InspectContainer
            && failure.InnerException != null
            && DuplicateMergeFolderScan.IsInputOutputFailure(failure.InnerException);
}
