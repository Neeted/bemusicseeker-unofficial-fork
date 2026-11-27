using BeMusicSeeker.Models;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 同名のWAVとOGGの組の診断を、メモリ上で作る補助です。ツールチップの内訳・音質の目安・検査結果の詳細のTSVを、ファイルや復号器に触れずに確認する入力に使います。
/// 値は引数で指定した測定値をそのまま持ち、既定は測定値を持つ16ビットPCMのステレオ（44100Hz・1秒）のWAVとOGGです。
/// </summary>
internal static class DuplicateMergeAudioDiagnosticTestSupport
{
    /// <summary>既定のWAVの測定値です。</summary>
    internal static DuplicateMergeWavInfo DefaultWav { get; } = new(1, 16, 44100, 2, 44100, 176444);

    /// <summary>既定のOGGの測定値です。</summary>
    internal static DuplicateMergeOggInfo DefaultOgg { get; } = new(44100, 2, 44100, 16000);

    /// <summary>
    /// 組の診断を作ります。ファイルは <paramref name="folder"/> の直下の <c>名前.wav</c> と <c>名前.ogg</c> で、フォルダ・相対パスは引数で変えられます。
    /// </summary>
    internal static DuplicateMergeAudioPairDiagnostic Pair(
        string name,
        string folder,
        DuplicateMergeAudioPairResult result = DuplicateMergeAudioPairResult.NonEquivalent,
        DuplicateMergeAudioPairReason reason = DuplicateMergeAudioPairReason.None,
        DuplicateMergeAudioQualityHints hints = DuplicateMergeAudioQualityHints.None,
        string? wavFolder = null,
        string? oggFolder = null,
        string? wavRelativePath = null,
        string? oggRelativePath = null,
        DuplicateMergeWavInfo? wav = null,
        DuplicateMergeOggInfo? ogg = null,
        long? frameDifference = null,
        bool? excessSilent = null,
        DuplicateMergeAudioComparison? comparison = null)
        => new(
            name,
            wavFolder ?? folder,
            wavRelativePath ?? name + ".wav",
            oggFolder ?? folder,
            oggRelativePath ?? name + ".ogg",
            new DuplicateMergeAudioPairMeasurement(
                result, reason, hints, wav ?? DefaultWav, ogg ?? DefaultOgg, frameDifference, excessSilent, comparison));

    /// <summary>同等な組の診断を作ります。</summary>
    internal static DuplicateMergeAudioPairDiagnostic Equivalent(string name, string folder, DuplicateMergeAudioQualityHints hints = DuplicateMergeAudioQualityHints.None)
        => Pair(name, folder, DuplicateMergeAudioPairResult.Equivalent, hints: hints);

    /// <summary>同等でない組の診断を、理由と音質の目安を指定して作ります。</summary>
    internal static DuplicateMergeAudioPairDiagnostic NonEquivalent(
        string name, string folder, DuplicateMergeAudioPairReason reason, DuplicateMergeAudioQualityHints hints = DuplicateMergeAudioQualityHints.None)
        => Pair(name, folder, DuplicateMergeAudioPairResult.NonEquivalent, reason, hints);

    /// <summary>読取り失敗の組の診断を作ります。ファイルサイズだけを持つ測定値です。</summary>
    internal static DuplicateMergeAudioPairDiagnostic ReadFailure(string name, string folder)
        => Pair(
            name, folder, DuplicateMergeAudioPairResult.ReadFailure,
            wav: new DuplicateMergeWavInfo(null, null, null, null, null, 100), ogg: new DuplicateMergeOggInfo(null, null, null, 200));
}
