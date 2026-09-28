using System;
using Ribbit.Media.Audio;

namespace Ribbit.BMS;

/// <summary>譜面内の複数WAV indexから共有される復号済み音声データです。</summary>
internal sealed class BmsAudioResource
{
    /// <summary>復号PCM、元形式、譜面ロード時に捕捉したgainを保持します。</summary>
    internal BmsAudioResource(string path, DecodedAudio audio, float sourceGain)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        SourceGain = sourceGain;
    }

    /// <summary>入力path単位で正規化された識別pathです。</summary>
    internal string Path { get; }

    /// <summary>native player間で共有する不変の復号PCMです。</summary>
    internal DecodedAudio Audio { get; }

    /// <summary>読み込み時に捕捉したBMS source gainです。</summary>
    internal float SourceGain { get; }

    /// <summary>復号済み有限入力のdurationを取得します。</summary>
    internal TimeSpan Duration => TimeSpan.FromSeconds((double)Audio.FrameCount / Audio.SampleRate);

    /// <summary>指定mixer rateへSRCしたときの有限出力frame数を取得します。</summary>
    internal long GetOutputFrameCount(int outputSampleRate) =>
        AudioFrameMath.CeilingOutputFrameCount(Audio.FrameCount, Audio.SampleRate, outputSampleRate);

    /// <summary>音源が正常に復号された0 frame入力かを取得します。</summary>
    internal bool IsEmpty => Audio.FrameCount == 0;
}
