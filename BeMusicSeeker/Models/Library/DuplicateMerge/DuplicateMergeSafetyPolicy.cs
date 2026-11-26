using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>統合の安全性確認が使う、ファイルの分類と内容比較の定数を一か所で定めます。</summary>
internal static class DuplicateMergeSafetyPolicy
{
    /// <summary>部分比較では先頭・中央・末尾からそれぞれ64 KiBを読みます。</summary>
    internal const int SegmentLength = 64 * 1024;

    /// <summary>内容を読む資源の比較方法を、共有の拡張子分類とサイズから決めます。</summary>
    internal static DuplicateMergeComparisonMethod GetReadMethod(string fileName, long length)
    {
        const int movieLimit = 3 * SegmentLength;
        const int otherLimit = 32 * 1024 * 1024;
        if (length <= movieLimit) { return DuplicateMergeComparisonMethod.Full; }
        string extension = Path.GetExtension(fileName);
        if (ChartResourceExtensions.IsMovieExtension(extension)) { return DuplicateMergeComparisonMethod.Partial; }
        if (length <= otherLimit || ChartResourceExtensions.IsAudioExtension(extension) || ChartResourceExtensions.IsImageExtension(extension))
        {
            return DuplicateMergeComparisonMethod.Full;
        }
        return DuplicateMergeComparisonMethod.Partial;
    }

    /// <summary>個別に見るチャンネルの、WAVに対するOGGの全帯域の音量比 <c>√(Σo² / Σw²)</c> の下限です（以上）。高域の雑音成分の一部を落とすVorbisの変換を許容するため広く取ります。</summary>
    internal const double AudioVolumeRatioMinimum = 0.63;

    /// <summary>個別に見るチャンネルの、WAVに対するOGGの全帯域の音量比 <c>√(Σo² / Σw²)</c> の上限です（以下）。</summary>
    internal const double AudioVolumeRatioMaximum = 1.15;

    /// <summary>低域（4000Hz以下）の音量比 <c>√(Σo₄² / Σw₄²)</c> の下限です（以上）。意図的な音量変更は、全帯域ではなくこの比で見分けます。</summary>
    internal const double AudioLowBandVolumeRatioMinimum = 0.84;

    /// <summary>低域（4000Hz以下）の音量比 <c>√(Σo₄² / Σw₄²)</c> の上限です（以下）。</summary>
    internal const double AudioLowBandVolumeRatioMaximum = 1.07;

    /// <summary>
    /// 低域の音量比を見る条件です。個別に見るチャンネルのうち、WAVの4000Hz以下のエネルギー <c>Σw₄²</c> が、基準 <c>E</c> のこの値分の1以上のものに適用します（ちょうどは見る）。
    /// </summary>
    internal const double AudioLowBandVolumeEnergyDivisor = 1000;

    /// <summary>両チャンネルを合算した全帯域の正規化相関 <c>Σwo / √(Σw²·Σo²)</c> の下限です（以上）。Vorbisが保存しない高域の雑音成分と左右差を許容します。</summary>
    internal const double AudioFullBandCorrelationMinimum = 0.5;

    /// <summary>
    /// 個別に見るチャンネルの条件です。チャンネルのエネルギー（<c>Σw²</c> と <c>Σo²</c> の大きい方）が、基準 <c>E</c> のこの値分の1以上のチャンネルを個別に見ます（ちょうどは見る）。
    /// </summary>
    internal const double AudioIndividualChannelEnergyDivisor = 30;

    /// <summary>4次バターワース低域通過フィルタを構成する、双2次フィルタ1段目のQです。</summary>
    internal const double AudioButterworthFirstStageQ = 0.54119610;

    /// <summary>4次バターワース低域通過フィルタを構成する、双2次フィルタ2段目のQです。</summary>
    internal const double AudioButterworthSecondStageQ = 1.30656296;

    /// <summary>低域の波形を比べる、広い方の低域通過フィルタ（4次バターワース）の遮断周波数（Hz）です。</summary>
    internal const double AudioLowPassWideCutoffHertz = 4000;

    /// <summary>低域の波形を比べる、狭い方の低域通過フィルタ（4次バターワース）の遮断周波数（Hz）です。</summary>
    internal const double AudioLowPassNarrowCutoffHertz = 1000;

    /// <summary>サンプルレートが遮断周波数のこの倍数以下の音源には、その遮断周波数のフィルタを掛けず、元の列を低域の列として扱います。</summary>
    internal const int AudioLowPassSampleRateMultiple = 4;

    /// <summary>
    /// 低域の波形の誤差の上限（4000Hz以下）のうち、<c>L₄(s)</c> に掛ける係数です。区間 s の誤差エネルギーは、区間の4000Hz以下のエネルギー <c>L₄(s)</c> のこの係数倍と、<c>P</c> の次の定数倍の和以下でなければなりません。この定数は <see cref="AudioWideBandErrorPeakCoefficient"/> です。
    /// </summary>
    internal const double AudioWideBandErrorLowCoefficient = 0.2;

    /// <summary>4000Hz以下の誤差の上限が、全区間の全帯域エネルギーの最大値 <c>P</c> に掛ける係数です。</summary>
    internal const double AudioWideBandErrorPeakCoefficient = 0.005;

    /// <summary>1000Hz以下の誤差の上限が、区間の1000Hz以下のエネルギー <c>L₁(s)</c> に掛ける係数です。</summary>
    internal const double AudioNarrowBandErrorLowCoefficient = 0.05;

    /// <summary>1000Hz以下の誤差の上限が、区間の全帯域エネルギー <c>F(s)</c> に掛ける係数です。低域の割合が小さい音源の上限を保つ項です。</summary>
    internal const double AudioNarrowBandErrorFullCoefficient = 0.01;

    /// <summary>1000Hz以下の誤差の上限が、全区間の全帯域エネルギーの最大値 <c>P</c> に掛ける係数です。</summary>
    internal const double AudioNarrowBandErrorPeakCoefficient = 0.001;

    /// <summary>区間ごとの音量を比べる区間の長さ（フレーム）です。先頭から区切り、末尾の端数の区間も比べます。</summary>
    internal const int AudioSegmentFrames = 2048;

    /// <summary>
    /// 区間のWAVとOGGのエネルギーの大きい方が、基準 <c>P</c> のこの値分の1未満なら、その区間の音量を見ません（ちょうどは見る）。
    /// </summary>
    internal const double AudioSegmentEnergyFloorDivisor = 100;

    /// <summary>区間ごとの音量で、区間のWAVとOGGのエネルギーの小さい方が、大きい方に対して満たすべき比の下限（−12dB。以上）です。</summary>
    internal static readonly double AudioSegmentEnergyRatioMinimum = Math.Pow(10, -1.2);

    /// <summary>長さ違いを許す、サンプルレートの一つ目（Hz）です。</summary>
    internal const int AudioLengthToleranceSampleRateFirst = 44100;

    /// <summary>長さ違いを許す、サンプルレートの二つ目（Hz）です。</summary>
    internal const int AudioLengthToleranceSampleRateSecond = 48000;

    /// <summary>OGGがWAVより長くてよい最大のフレーム数です（その超過分はすべて0でなければなりません）。</summary>
    internal const int AudioOggLongerExcessMaximumFrames = 1152;

    /// <summary>WAVがOGGより長くてよい最大のフレーム数です（その超過分はすべて0でなければなりません）。</summary>
    internal const int AudioWavLongerExcessMaximumFrames = 128;

    /// <summary>レート違いの比較を許す、WAVのサンプルレート（Hz）です。</summary>
    internal const int AudioResampleWavSampleRate = 48000;

    /// <summary>レート違いの比較を許す、OGGのサンプルレート（Hz）です。</summary>
    internal const int AudioResampleOggSampleRate = 44100;

    /// <summary>レート違いの変換の比 44100:48000 を既約にした分子（入力位置 <c>t = m·147/160</c>）です。</summary>
    internal const int AudioResampleInputUnits = 147;

    /// <summary>レート違いの変換の比を既約にした分母で、位相（<c>t − ⌊t⌋</c>）の通り数でもあります。</summary>
    internal const int AudioResampleOutputUnits = 160;

    /// <summary>レート違いの長さの許容です。<c>|147·Nw − 160·No|</c> がこの値以下（換算した長さとの差が2フレーム以内）なら比べます。</summary>
    internal const int AudioResampleLengthToleranceUnits = 294;

    /// <summary>レート違いの変換の、一つの出力フレームに使う入力の点数です（<c>⌊t⌋ − 15</c> から <c>⌊t⌋ + 16</c>）。</summary>
    internal const int AudioResampleTaps = 32;

    /// <summary>レート違いの変換の窓関数（カイザー窓）のβです。</summary>
    internal const double AudioResampleKaiserBeta = 8.6;

    private static readonly HashSet<string> ChartExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bms", ".bme", ".bml", ".pms", ".bmson"
    };

    private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmx", ".pmx", ".txt"
    };

    /// <summary>拡張子から、ファイルを分類します。大文字小文字を区別しません。</summary>
    internal static DuplicateMergeFileCategory Classify(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        if (ChartExtensions.Contains(extension)) { return DuplicateMergeFileCategory.Chart; }
        return ProtectedExtensions.Contains(extension) ? DuplicateMergeFileCategory.Protected : DuplicateMergeFileCategory.Resource;
    }

    /// <summary>音源の拡張子かを返します。</summary>
    internal static bool IsAudio(string fileName) => ChartResourceExtensions.IsAudioExtension(Path.GetExtension(fileName));

    /// <summary>
    /// フォルダのパスを、同一・親子の比較に使える形（絶対・末尾の区切りなし）にします。整えられないパスは、前後の空白と末尾の区切りだけを除きます。
    /// </summary>
    internal static string NormalizeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return string.Empty; }
        try
        {
            return LongPathFileSystem.NormalizePhysicalScopePath(path);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or IOException)
        {
            return path.Trim().TrimEnd('\\', '/');
        }
    }
}

/// <summary>ファイルの分類です。</summary>
internal enum DuplicateMergeFileCategory
{
    /// <summary>譜面ファイル（.bms・.bme・.bml・.pms・.bmson）です。</summary>
    Chart,
    /// <summary>保護対象（.bmx・.pmx・.txt）です。</summary>
    Protected,
    /// <summary>譜面ファイルと保護対象を除く資源です。</summary>
    Resource
}
