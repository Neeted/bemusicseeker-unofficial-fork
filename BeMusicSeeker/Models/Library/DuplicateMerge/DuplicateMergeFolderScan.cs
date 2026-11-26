using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>走査で見つけた一つのファイルです。内容の比較で一度だけ計算するハッシュと失敗を、確認の間だけ保持します。</summary>
internal sealed class DuplicateMergeScannedFile
{
    internal DuplicateMergeScannedFile(string relativePath, string fullPath, long length, DateTime lastWriteTimeUtc)
    {
        RelativePath = relativePath;
        FullPath = fullPath;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Category = DuplicateMergeSafetyPolicy.Classify(relativePath);
    }

    /// <summary>フォルダからの相対パスです。区切りは <c>\</c> です。</summary>
    internal string RelativePath { get; }

    /// <summary>ファイルのパスです。</summary>
    internal string FullPath { get; }

    /// <summary>走査時のサイズです。</summary>
    internal long Length { get; }

    /// <summary>走査時の最終書込み時刻（UTC）です。</summary>
    internal DateTime LastWriteTimeUtc { get; }

    /// <summary>拡張子による分類です。</summary>
    internal DuplicateMergeFileCategory Category { get; }

    /// <summary>内容を読む処理が済んだかです。</summary>
    internal bool DigestAttempted { get; set; }

    /// <summary>XXH3（128ビット）の値です。<see cref="DigestFailure"/> が null のときだけ有効です。</summary>
    internal UInt128 Digest { get; set; }

    /// <summary>内容を読めなかった原因です。</summary>
    internal Exception DigestFailure { get; set; }
}

/// <summary>
/// 統合の安全性確認が、一つのフォルダを走査した結果です。本体と配下の全階層（隠し・システム属性を含む）を、リンク先へ入らずにフォルダごとに一回だけ列挙します。
/// 列挙や属性の取得に失敗した位置は、成功へ読み替えず失敗の一覧へ残します。
/// </summary>
internal sealed class DuplicateMergeFolderScan
{
    private static readonly EnumerationOptions EntryEnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        // 既定は隠し・システム属性の項目を飛ばすため、見落としを避けて全属性を列挙します。
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    private DuplicateMergeFolderScan(string folder)
    {
        Folder = folder;
    }

    /// <summary>走査したフォルダです。</summary>
    internal string Folder { get; }

    /// <summary>リンク（シンボリックリンク・ジャンクション）の相対パスです。フォルダ本体がリンクなら空です。</summary>
    internal List<string> Links { get; } = [];

    /// <summary>列挙・属性取得・存在の確認に失敗した位置の相対パスです。フォルダ本体なら空です。</summary>
    internal List<string> Failures { get; } = [];

    /// <summary>配下のファイルです。キーは相対パス（大文字小文字を無視）です。</summary>
    internal Dictionary<string, DuplicateMergeScannedFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>配下のディレクトリの相対パスです。</summary>
    internal HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// フォルダを走査します。<paramref name="descend"/> が偽なら、フォルダ本体の属性だけを取得し、配下を走査しません。
    /// 本体がリンクなら、配下へ入りません。
    /// </summary>
    /// <param name="excludeDirectory">配下のディレクトリのパス（<paramref name="folder"/> を正規化した位置と相対パスをつないだ値）を受け、真なら、そのディレクトリを記録せず、その配下へも入らない判定です。重複音源の確認が、配下の別の対象フォルダを所有範囲から除くために使います。省略時はすべて走査します。</param>
    internal static DuplicateMergeFolderScan Scan(string folder, bool descend, Func<string, bool> excludeDirectory = null)
    {
        string root = DuplicateMergeSafetyPolicy.NormalizeFolder(folder);
        var scan = new DuplicateMergeFolderScan(folder);
        FileAttributes attributes;
        try
        {
            attributes = LongPathFileSystem.GetAttributes(root);
        }
        catch (Exception failure) when (IsInputOutputFailure(failure))
        {
            scan.Failures.Add(string.Empty);
            return scan;
        }
        try
        {
            string extendedRoot = LongPathFileSystem.ToExtendedPath(root);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(extendedRoot) : new FileInfo(extendedRoot);
            if (IsLink(attributes, info))
            {
                scan.Links.Add(string.Empty);
                return scan;
            }
        }
        catch (Exception failure) when (IsInputOutputFailure(failure))
        {
            scan.Failures.Add(string.Empty);
            return scan;
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            scan.Failures.Add(string.Empty);
            return scan;
        }
        if (descend) { scan.Walk(root, excludeDirectory); }
        return scan;
    }

    private void Walk(string root, Func<string, bool> excludeDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        while (pending.Count > 0)
        {
            string relativeDirectory = pending.Pop();
            string directory = relativeDirectory.Length == 0 ? root : Path.Combine(root, relativeDirectory);
            List<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(LongPathFileSystem.ToExtendedPath(directory))
                    .EnumerateFileSystemInfos("*", EntryEnumerationOptions)];
            }
            catch (Exception failure) when (IsInputOutputFailure(failure))
            {
                Failures.Add(relativeDirectory);
                continue;
            }
            foreach (FileSystemInfo entry in entries)
            {
                string relative = relativeDirectory.Length == 0 ? entry.Name : relativeDirectory + "\\" + entry.Name;
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget != null)
                    {
                        Links.Add(relative);
                        continue;
                    }
                    if (entry is DirectoryInfo)
                    {
                        if (excludeDirectory != null && excludeDirectory(Path.Combine(root, relative)))
                        {
                            continue;
                        }
                        Directories.Add(relative);
                        pending.Push(relative);
                    }
                    else if (entry is FileInfo file)
                    {
                        Files[relative] = new DuplicateMergeScannedFile(
                            relative, Path.Combine(root, relative), file.Length, file.LastWriteTimeUtc);
                    }
                }
                catch (Exception failure) when (IsInputOutputFailure(failure))
                {
                    Failures.Add(relative);
                }
            }
        }
    }

    private static bool IsLink(FileAttributes attributes, FileSystemInfo info)
        => (attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget != null;

    /// <summary>入出力・権限・パスの形式による失敗かを返します。これ以外の例外は確認の失敗として扱わず、呼出元へ伝えます。</summary>
    internal static bool IsInputOutputFailure(Exception failure)
        => failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;
}
