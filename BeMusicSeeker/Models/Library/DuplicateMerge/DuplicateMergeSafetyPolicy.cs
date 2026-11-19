using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>統合の安全性確認が使う、ファイルの分類と内容比較の定数を一か所で定めます。</summary>
internal static class DuplicateMergeSafetyPolicy
{
    /// <summary>
    /// この大きさ以下のファイルは全量を読んで比べます。実測に基づいて確定した値です。
    /// 実ライブラリの資源約8万件で、上限の候補 64／192／512／1024 KiB のうち 192 KiB で読取りの総量が最小になり、
    /// 資源の約97%が 192 KiB 以下でした。所要時間は読取りの量よりファイルを開く回数が支配的です。
    /// </summary>
    internal const int FullReadLimit = 192 * 1024;

    /// <summary>上限を超えるファイルで、先頭・中央・末尾から読む区間の長さです。上限と合わせて確定した値で、三区間の合計は全量読取りの上限と等しくなります。</summary>
    internal const int SegmentLength = 64 * 1024;

    private static readonly HashSet<string> ChartExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bms", ".bme", ".bml", ".pms", ".bmson"
    };

    private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmx", ".pmx", ".txt"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".ogg", ".mp3", ".flac"
    };

    /// <summary>拡張子から、ファイルを分類します。大文字小文字を区別しません。</summary>
    internal static DuplicateMergeFileCategory Classify(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        if (ChartExtensions.Contains(extension)) { return DuplicateMergeFileCategory.Chart; }
        return ProtectedExtensions.Contains(extension) ? DuplicateMergeFileCategory.Protected : DuplicateMergeFileCategory.Resource;
    }

    /// <summary>音源の拡張子かを返します。</summary>
    internal static bool IsAudio(string fileName) => AudioExtensions.Contains(Path.GetExtension(fileName));

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
