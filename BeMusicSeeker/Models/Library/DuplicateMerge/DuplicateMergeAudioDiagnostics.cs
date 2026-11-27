using System;

namespace BeMusicSeeker.Models;

/// <summary>同名のWAV一つとOGG一つの組の結果です。</summary>
internal enum DuplicateMergeAudioPairResult
{
    /// <summary>同等と判定しました。</summary>
    Equivalent,

    /// <summary>同等と判定しませんでした。理由は <see cref="DuplicateMergeAudioPairReason"/> が示します。</summary>
    NonEquivalent,

    /// <summary>いずれかのファイルの読取りが入出力の失敗で終わり、判定できませんでした。理由は持ちません。</summary>
    ReadFailure
}

/// <summary>
/// 同等でない組の理由です。判定が実際に不成立を確定する順（宣言順）で最初のものを一つだけ持ち、複数が成り立つときは宣言順が早いほうです。
/// 数値が順位です。<see cref="None"/> は同等の組と読取り失敗の組が持つ値です。
/// </summary>
internal enum DuplicateMergeAudioPairReason
{
    /// <summary>理由なし（同等、または読取り失敗）。</summary>
    None = 0,

    /// <summary>WAVを復号できません（非PCM、壊れた入力、有限でない値を含む浮動小数点など）。</summary>
    WavUndecodable = 1,

    /// <summary>OGGを復号できません。WAVも復号できないときは <see cref="WavUndecodable"/> です。</summary>
    OggUndecodable = 2,

    /// <summary>チャンネル数が異なります。</summary>
    ChannelCountMismatch = 3,

    /// <summary>3チャンネル以上です（並びが形式によって異なるため比べません）。</summary>
    MultiChannel = 4,

    /// <summary>サンプルレートが異なり、WAV 48000Hz・OGG 44100Hz の組ではありません。</summary>
    UnsupportedSampleRates = 5,

    /// <summary>比べる範囲に当たらない長さの違いです（比較を打ち切ります）。</summary>
    LengthMismatch = 6,

    /// <summary>長さの違いが許容範囲内ですが、長い側の超過分に音があります（比較を打ち切ります）。</summary>
    LengthExcessAudible = 7,

    /// <summary>比べる範囲に非数・無限大のサンプルがあります。</summary>
    NonFinite = 8,

    /// <summary>一方だけが無音です。</summary>
    OneSideSilent = 9,

    /// <summary>低域（1000Hz以下・4000Hz以下）の波形が一致しません。</summary>
    LowBandWaveform = 10,

    /// <summary>全帯域の相関が不足しています。</summary>
    Correlation = 11,

    /// <summary>全帯域または4000Hz以下の音量比が範囲外です。</summary>
    Volume = 12,

    /// <summary>区間ごとの音量差が大きすぎます。</summary>
    SegmentVolume = 13
}

/// <summary>測定値だけから導く音質の目安です。複数が同時に成立し得ます。宣言順が表示の順です。</summary>
[Flags]
internal enum DuplicateMergeAudioQualityHints
{
    /// <summary>成立した目安がありません。</summary>
    None = 0,

    /// <summary>OGGが低ビットレートです（1秒以上で、ステレオは96kbps未満、モノラルは48kbps未満）。</summary>
    OggLowBitrate = 1,

    /// <summary>WAVが8ビットです。</summary>
    Wav8Bit = 2,

    /// <summary>WAVのサンプルレートが44100Hz未満です。</summary>
    WavLowSampleRate = 4,

    /// <summary>OGGのサンプルレートがWAVより低いです。</summary>
    OggLowerSampleRate = 8,

    /// <summary>WAVだけがモノラルです。</summary>
    WavOnlyMono = 16,

    /// <summary>OGGだけがモノラルです。</summary>
    OggOnlyMono = 32
}

/// <summary>WAVの <c>fmt </c> チャンクから読めた範囲のヘッダーです。読めなかった値は <c>null</c> です。</summary>
/// <param name="FormatTag">形式タグ（<c>WAVE_FORMAT_EXTENSIBLE</c> はサブフォーマットの形式。既知でないサブフォーマットは 0xFFFE）。</param>
/// <param name="BitsPerSample">ビット深度。</param>
/// <param name="SampleRate">サンプルレート。</param>
/// <param name="Channels">チャンネル数。</param>
internal readonly record struct DuplicateMergeWavHeader(int? FormatTag, int? BitsPerSample, int? SampleRate, int? Channels);

/// <summary>WAVの読取り結果です。復号できなければ <paramref name="Samples"/> は <c>null</c> で、ヘッダーは読めた範囲だけを持ちます。</summary>
/// <param name="Samples">読めたPCM。</param>
/// <param name="Header">ヘッダー。</param>
internal sealed record DuplicateMergeWaveReadResult(DuplicateMergeAudioSamples Samples, DuplicateMergeWavHeader Header)
{
    /// <summary>RIFF/WAVEとして読めず、ヘッダーも読めなかった結果です。</summary>
    internal static DuplicateMergeWaveReadResult Unreadable { get; } = new(null, default);
}

/// <summary>WAVの測定値です。測定できなかった値は <c>null</c> です。</summary>
/// <param name="FormatTag">形式タグ（PCM整数 1、浮動小数点 3。<c>WAVE_FORMAT_EXTENSIBLE</c> はサブフォーマットの形式、既知でないサブフォーマットは 0xFFFE）。</param>
/// <param name="BitsPerSample">ビット深度。</param>
/// <param name="SampleRate">サンプルレート。</param>
/// <param name="Channels">チャンネル数。</param>
/// <param name="Frames">フレーム数。復号できたときだけ測定します。</param>
/// <param name="FileBytes">走査で得たファイルサイズ。</param>
internal readonly record struct DuplicateMergeWavInfo(
    int? FormatTag, int? BitsPerSample, int? SampleRate, int? Channels, long? Frames, long FileBytes)
{
    /// <summary>長さ（秒）です。フレーム数とサンプルレートの両方が測定できたときだけ値を持ちます。</summary>
    internal double? Seconds => Frames is { } frames && SampleRate is > 0 and var rate ? (double)frames / rate : null;
}

/// <summary>OGGの測定値です。測定できなかった値は <c>null</c> です。</summary>
/// <param name="SampleRate">サンプルレート。</param>
/// <param name="Channels">チャンネル数。</param>
/// <param name="Frames">フレーム数。復号できたときだけ測定します。</param>
/// <param name="FileBytes">走査で得たファイルサイズ。</param>
internal readonly record struct DuplicateMergeOggInfo(int? SampleRate, int? Channels, long? Frames, long FileBytes)
{
    /// <summary>長さ（秒）です。フレーム数とサンプルレートの両方が測定できたときだけ値を持ちます。</summary>
    internal double? Seconds => Frames is { } frames && SampleRate is > 0 and var rate ? (double)frames / rate : null;

    /// <summary>平均ビットレート（kbps。ファイルサイズ×8÷長さ〔秒〕÷1000）です。フレーム数が0または未測定なら <c>null</c> です。</summary>
    internal double? AverageKbps
        => Frames is > 0 and var frames && SampleRate is > 0 and var rate ? FileBytes * 8.0 * rate / frames / 1000.0 : null;
}

/// <summary>
/// 波形の比較の測定値です。比べる範囲全体を一回の走査で得た統計から求めます。
/// 無限大は、一方のエネルギーが0の音量比、上限が0で誤差が正の誤差比で現れます。
/// </summary>
/// <param name="Correlation">全帯域の相関（<c>Σwo/√(Σw²·Σo²)</c>。全チャンネル・全フレームの和）。</param>
/// <param name="VolumeDb">全帯域の音量比（dB）。個別に見るチャンネルのうち0dBから最も離れた値。</param>
/// <param name="LowVolumeDb">4000Hz以下の音量比（dB）。対象のチャンネルがなければ <c>null</c>。</param>
/// <param name="LowErrorRatio1k">1000Hz以下の波形の誤差比（誤差エネルギー÷区間の上限の、全チャンネル・全区間の最大）。1を超えると不成立です。</param>
/// <param name="LowErrorRatio4k">4000Hz以下の波形の誤差比。</param>
/// <param name="SegmentMinDb">区間ごとの音量の最小比（dB）。判定が見る区間がなければ <c>null</c>。</param>
internal readonly record struct DuplicateMergeAudioComparison(
    double Correlation, double VolumeDb, double? LowVolumeDb, double LowErrorRatio1k, double LowErrorRatio4k, double? SegmentMinDb);

/// <summary>
/// 一組の判定（<see cref="DuplicateMergeAudioEquivalence.Judge"/>）の結果です。
/// 比較の測定値は、理由10〜13の組と、無音でない同等の組だけが持ちます（それ以外は未測定）。
/// </summary>
/// <param name="Reason">同等でない理由。同等なら <see cref="DuplicateMergeAudioPairReason.None"/>。</param>
/// <param name="FrameDifference">フレーム差（OGG−WAV）。サンプルレートが同じときだけ測定します。</param>
/// <param name="ExcessSilent">長い側の超過分が無音か。同じレートで長さが異なり、許容範囲内として超過分を検査した組だけが持ちます。</param>
/// <param name="Comparison">波形の比較の測定値。</param>
internal readonly record struct DuplicateMergeAudioJudgement(
    DuplicateMergeAudioPairReason Reason, long? FrameDifference, bool? ExcessSilent, DuplicateMergeAudioComparison? Comparison)
{
    /// <summary>同等かです。</summary>
    internal bool Equivalent => Reason == DuplicateMergeAudioPairReason.None;
}

/// <summary>一組（WAV一つとOGG一つ）の診断の本体です。結果・理由・音質の目安・測定値を、組あたり固定長の小さな値で持ちます。</summary>
/// <param name="Result">結果。</param>
/// <param name="Reason">同等でない理由。<paramref name="Result"/> が <see cref="DuplicateMergeAudioPairResult.NonEquivalent"/> のときだけ値を持ちます。</param>
/// <param name="Hints">音質の目安。読取り失敗の組は持ちません。</param>
/// <param name="Wav">WAVの測定値。</param>
/// <param name="Ogg">OGGの測定値。</param>
/// <param name="FrameDifference">フレーム差（OGG−WAV）。両方を復号できて同じサンプルレートのときだけ測定します。</param>
/// <param name="ExcessSilent">長い側の超過分が無音か。</param>
/// <param name="Comparison">波形の比較の測定値。</param>
internal sealed record DuplicateMergeAudioPairMeasurement(
    DuplicateMergeAudioPairResult Result,
    DuplicateMergeAudioPairReason Reason,
    DuplicateMergeAudioQualityHints Hints,
    DuplicateMergeWavInfo Wav,
    DuplicateMergeOggInfo Ogg,
    long? FrameDifference,
    bool? ExcessSilent,
    DuplicateMergeAudioComparison? Comparison);

/// <summary>
/// 確認結果が保持する、一組の診断です。ファイルは、フォルダと相対パスで持ちます（フルパスの文字列を組ごとに複製しません）。
/// 寿命は確認結果と同じで、永続化しません。
/// </summary>
/// <param name="Name">衝突名（相対ディレクトリと拡張子なしの名前）。確認結果の衝突名と同じインスタンスです。</param>
/// <param name="WavFolder">WAVのフォルダ。</param>
/// <param name="WavRelativePath">WAVのフォルダからの相対パス。</param>
/// <param name="OggFolder">OGGのフォルダ。</param>
/// <param name="OggRelativePath">OGGのフォルダからの相対パス。</param>
/// <param name="Measurement">組の結果と測定値。</param>
internal sealed record DuplicateMergeAudioPairDiagnostic(
    string Name, string WavFolder, string WavRelativePath, string OggFolder, string OggRelativePath,
    DuplicateMergeAudioPairMeasurement Measurement)
{
    /// <summary>WAVのフルパスです。</summary>
    internal string WavPath => Combine(WavFolder, WavRelativePath);

    /// <summary>OGGのフルパスです。</summary>
    internal string OggPath => Combine(OggFolder, OggRelativePath);

    private static string Combine(string folder, string relative)
        => string.IsNullOrEmpty(folder) ? relative : folder.TrimEnd('\\', '/') + "\\" + relative;
}

/// <summary>測定値から音質の目安を導きます。純粋な計算で、未測定の値がからむ目安は成立しません。</summary>
internal static class DuplicateMergeAudioQuality
{
    /// <summary>WAVが低サンプルレートとなる上限（これ未満）です。</summary>
    private const int WavLowSampleRateBelow = 44100;
    private const double StereoLowBitrateKbps = 96;
    private const double MonoLowBitrateKbps = 48;

    /// <summary>
    /// 測定値から成立した目安を返します。OGGが低ビットレートは、OGGの長さが1秒以上（1秒ちょうどを含む）で、平均ビットレートがステレオなら96kbps未満、
    /// モノラルなら48kbps未満のときです（3チャンネル以上は対象外）。
    /// </summary>
    internal static DuplicateMergeAudioQualityHints Evaluate(in DuplicateMergeWavInfo wav, in DuplicateMergeOggInfo ogg)
    {
        DuplicateMergeAudioQualityHints hints = DuplicateMergeAudioQualityHints.None;
        if (ogg.Frames is { } oggFrames && ogg.SampleRate is > 0 and var oggRate && ogg.Channels is { } oggChannels
            && oggFrames >= oggRate && ogg.AverageKbps is { } kbps
            && ((oggChannels == 2 && kbps < StereoLowBitrateKbps) || (oggChannels == 1 && kbps < MonoLowBitrateKbps)))
        {
            hints |= DuplicateMergeAudioQualityHints.OggLowBitrate;
        }
        if (wav.BitsPerSample == 8) { hints |= DuplicateMergeAudioQualityHints.Wav8Bit; }
        if (wav.SampleRate is { } wavRate && wavRate < WavLowSampleRateBelow) { hints |= DuplicateMergeAudioQualityHints.WavLowSampleRate; }
        if (wav.SampleRate is { } wavRateForOgg && ogg.SampleRate is { } oggRateForWav && oggRateForWav < wavRateForOgg)
        {
            hints |= DuplicateMergeAudioQualityHints.OggLowerSampleRate;
        }
        if (wav.Channels is { } wavChannels && ogg.Channels is { } oggChannelCount)
        {
            if (wavChannels == 1 && oggChannelCount >= 2) { hints |= DuplicateMergeAudioQualityHints.WavOnlyMono; }
            if (oggChannelCount == 1 && wavChannels >= 2) { hints |= DuplicateMergeAudioQualityHints.OggOnlyMono; }
        }
        return hints;
    }
}
