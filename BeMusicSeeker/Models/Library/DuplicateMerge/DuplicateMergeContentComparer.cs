using System;
using System.IO;
using System.IO.Hashing;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>二つのファイルの内容の比較結果です。</summary>
internal enum DuplicateMergeContentComparison
{
    /// <summary>同一内容と判定しました。</summary>
    Same,
    /// <summary>別内容と判定しました。</summary>
    Different,
    /// <summary>読取りに失敗し、判定できません。失敗した原因は各ファイルの <see cref="DuplicateMergeScannedFile.DigestFailure"/> にあります。</summary>
    Unreadable
}

/// <summary>
/// 同じ相対パスの二つのファイルが同一内容かを判定します。一回の確認の中で、同じファイルは一度だけ読み、ハッシュは確認の外へ保存しません。
/// 大きいファイルの未読領域にだけ差がある場合は同一内容と判定されます。これは仕様で、全バイトを比べる経路は持ちません。
/// </summary>
internal sealed class DuplicateMergeContentComparer
{
    private readonly bool sizeAndTimeOnly;
    private byte[] segmentBuffer;

    /// <param name="sizeAndTimeOnly">サイズと更新日時が完全に一致するファイルを、読まずに同一内容とみなすかです。</param>
    internal DuplicateMergeContentComparer(bool sizeAndTimeOnly)
    {
        this.sizeAndTimeOnly = sizeAndTimeOnly;
    }

    /// <summary>
    /// 判定します。サイズが異なれば読まずに別内容、簡易確認でサイズと更新日時が一致すれば読まずに同一内容、それ以外は全量または区間のXXH3を比べます。
    /// </summary>
    /// <param name="first">一方のファイル。</param>
    /// <param name="second">他方のファイル。</param>
    /// <param name="method">同一内容と判定した場合の比較方法です。</param>
    internal DuplicateMergeContentComparison Compare(
        DuplicateMergeScannedFile first, DuplicateMergeScannedFile second, out DuplicateMergeComparisonMethod method)
    {
        method = default;
        if (first.Length != second.Length) { return DuplicateMergeContentComparison.Different; }
        if (sizeAndTimeOnly && first.LastWriteTimeUtc == second.LastWriteTimeUtc)
        {
            method = DuplicateMergeComparisonMethod.SizeAndTime;
            return DuplicateMergeContentComparison.Same;
        }
        method = first.Length <= DuplicateMergeSafetyPolicy.FullReadLimit
            ? DuplicateMergeComparisonMethod.Full : DuplicateMergeComparisonMethod.Partial;
        // 先に失敗したファイルがあれば、相手を読まずに判定不能とする。
        if (!EnsureDigest(first) || !EnsureDigest(second)) { return DuplicateMergeContentComparison.Unreadable; }
        return first.Digest == second.Digest ? DuplicateMergeContentComparison.Same : DuplicateMergeContentComparison.Different;
    }

    private bool EnsureDigest(DuplicateMergeScannedFile file)
    {
        if (!file.DigestAttempted)
        {
            file.DigestAttempted = true;
            try
            {
                file.Digest = ComputeDigest(file);
            }
            catch (Exception failure) when (DuplicateMergeFolderScan.IsInputOutputFailure(failure))
            {
                file.DigestFailure = failure;
            }
        }
        return file.DigestFailure == null;
    }

    private UInt128 ComputeDigest(DuplicateMergeScannedFile file)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(file.FullPath);
        var hash = new XxHash128();
        if (file.Length <= DuplicateMergeSafetyPolicy.FullReadLimit)
        {
            hash.Append(stream);
        }
        else
        {
            int segment = DuplicateMergeSafetyPolicy.SegmentLength;
            // 区間のバッファーは、部分読取りが必要になった時点で確保し、一回の確認の間で共有する。
            segmentBuffer ??= new byte[segment];
            // 先頭・中央（ファイルの中心をまたぐ区間）・末尾。上限が区間長の3倍なので、上限を超える大きさでは三つの区間は重ならない。
            foreach (long offset in new[] { 0L, file.Length / 2 - segment / 2, file.Length - segment })
            {
                stream.Seek(offset, SeekOrigin.Begin);
                stream.ReadExactly(segmentBuffer, 0, segment);
                hash.Append(segmentBuffer);
            }
        }
        return hash.GetCurrentHashAsUInt128();
    }
}
