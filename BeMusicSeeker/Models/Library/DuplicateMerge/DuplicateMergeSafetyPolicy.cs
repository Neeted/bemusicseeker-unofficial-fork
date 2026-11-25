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

    /// <summary>判定するチャンネルの、WAVに対するOGGの音量比 <c>√(Σo² / Σw²)</c> の下限です（以上）。</summary>
    internal const double AudioVolumeRatioMinimum = 0.85;

    /// <summary>判定するチャンネルの、WAVに対するOGGの音量比 <c>√(Σo² / Σw²)</c> の上限です（以下）。</summary>
    internal const double AudioVolumeRatioMaximum = 1.15;

    /// <summary>判定するチャンネルの全帯域の正規化相関の下限です（以上）。高域の波形を保存しないVorbisの変換で下がる分を許容します。</summary>
    internal const double AudioFullBandCorrelationMinimum = 0.85;

    /// <summary>判定するチャンネルの低域の正規化相関の下限です（以上）。</summary>
    internal const double AudioLowBandCorrelationMinimum = 0.95;

    /// <summary>低域の相関を求める低域通過フィルタ（4次バターワース）の遮断周波数（Hz）です。</summary>
    internal const double AudioLowPassCutoffHertz = 4000;

    /// <summary>
    /// 低域の相関を見る条件です。WAVの低域のエネルギー <c>Σw′²</c> が、同じチャンネルのWAVの全帯域のエネルギー <c>Σw²</c> のこの割合未満なら、
    /// 低域の相関を見ません（この割合ちょうどは見ます）。OGGのエネルギーは使いません。
    /// </summary>
    internal const double AudioLowBandEnergyRatioMinimum = 0.05;

    /// <summary>サンプルレートがこの値（Hz）以下の音源には低域通過フィルタを掛けず、全帯域の波形を低域の波形として扱います。</summary>
    internal const int AudioLowPassSampleRateMaximum = 16000;

    /// <summary>区間ごとの音量を比べる区間の長さ（フレーム）です。先頭から区切り、末尾の端数の区間も比べます。</summary>
    internal const int AudioSegmentFrames = 2048;

    /// <summary>
    /// 区間のWAVとOGGのエネルギーの大きい方が、基準の区間エネルギーのこの値分の1未満なら、その区間を見ません。
    /// 基準は、判定するチャンネルの全区間について、WAVとOGGの両方を通じて最大の区間エネルギーで、チャンネルによらず一つです。
    /// </summary>
    internal const double AudioSegmentEnergyFloorDivisor = 1000;

    /// <summary>区間ごとの音量で、区間のWAVとOGGのエネルギーの小さい方が、大きい方に対して満たすべき比の下限（−12dB。以上）です。</summary>
    internal static readonly double AudioSegmentEnergyRatioMinimum = Math.Pow(10, -1.2);

    /// <summary>
    /// チャンネルのエネルギー（そのチャンネルのWAVのエネルギー <c>Σw²</c> とOGGのエネルギー <c>Σo²</c> の大きい方）が、
    /// 両音源の全チャンネルの <c>Σw²</c>・<c>Σo²</c> のうち最大の値のこの値分の1未満なら、そのチャンネルを判定しません。
    /// </summary>
    internal const double AudioChannelEnergyFloorDivisor = 10000;

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
