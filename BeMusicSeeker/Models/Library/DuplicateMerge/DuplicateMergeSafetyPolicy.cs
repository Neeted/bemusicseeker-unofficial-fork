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
