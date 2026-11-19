using System;
using System.Collections.Generic;
using System.IO;

namespace BeMusicSeeker.Models.Utils;

/// <summary>リンクの検査結果の分類です。</summary>
internal enum FolderLinkInspectionOutcome
{
    /// <summary>検査した範囲にリンクがなく、すべて確認できました。</summary>
    Clear,

    /// <summary>リンクを一件見つけました。<see cref="FolderLinkInspection.Path"/> がそのリンク自身のパスです。</summary>
    LinkFound,

    /// <summary>列挙・属性取得に失敗し、リンクの有無を確認できませんでした。<see cref="FolderLinkInspection.Path"/> が確認できなかった位置です。</summary>
    Unverifiable,
}

/// <summary>リンクの検査結果です。変更不能で、物理変更を行う前の判断だけに使います。</summary>
internal sealed class FolderLinkInspection
{
    /// <summary>リンクがなく、すべて確認できた結果です。</summary>
    internal static FolderLinkInspection Clear { get; } = new(FolderLinkInspectionOutcome.Clear, null, null);

    private FolderLinkInspection(FolderLinkInspectionOutcome outcome, string path, Exception failure)
    {
        Outcome = outcome;
        Path = path;
        Failure = failure;
    }

    /// <summary>検査結果の分類です。</summary>
    internal FolderLinkInspectionOutcome Outcome { get; }

    /// <summary>リンク自身のパス、または確認できなかった位置です。<see cref="FolderLinkInspectionOutcome.Clear"/> では null です。</summary>
    internal string Path { get; }

    /// <summary>確認できなかった原因です。<see cref="FolderLinkInspectionOutcome.Unverifiable"/> 以外では null です。</summary>
    internal Exception Failure { get; }

    internal static FolderLinkInspection ForLink(string path) => new(FolderLinkInspectionOutcome.LinkFound, path, null);

    internal static FolderLinkInspection ForUnverifiable(string path, Exception failure) => new(FolderLinkInspectionOutcome.Unverifiable, path, failure);
}

/// <summary>
/// フォルダ配下のリンク（シンボリックリンクとジャンクション）を、リンク先へ入らずに検出します。
/// OneDrive のプレースホルダーなど、これら以外の再解析ポイントはリンクとして扱いません。
/// </summary>
/// <remarks>
/// リンクかどうかは、リンク自身の属性と再解析タグで判定します（<see cref="FileSystemInfo.LinkTarget"/> は
/// シンボリックリンクとジャンクションだけを返し、リンク先が消えていても返します）。隠し・システム属性の
/// 項目も飛ばさず、列挙や属性取得に失敗した場合は成功へ読み替えず <see cref="FolderLinkInspectionOutcome.Unverifiable"/> にします。
/// 祖先のリンクは検査しません。呼出元が物理変更の前に一度だけ呼び、結果を判断します。
/// </remarks>
internal static class FolderLinkInspector
{
    private static readonly EnumerationOptions EntryEnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        // 既定は隠し・システム属性の項目を飛ばすため、リンクの見落としを避けて全属性を列挙します。
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// フォルダ統合の前提を検査します。統合元の本体と配下の全階層、統合先の本体、
    /// 統合元の各項目と同じ相対パスにある統合先の既存項目にリンクがあれば、最初に見つけた一件を返します。
    /// 統合元がリンクでなく存在しない場合と、ディレクトリでない場合は、統合先を検査せず <see cref="FolderLinkInspection.Clear"/> を返します。
    /// </summary>
    /// <param name="sourceDirectory">統合元フォルダです。</param>
    /// <param name="destinationDirectory">統合先フォルダです。存在しなくても構いません。</param>
    internal static FolderLinkInspection InspectMerge(string sourceDirectory, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(destinationDirectory);
        var inspection = new MergeInspection();
        try
        {
            return inspection.Run(sourceDirectory, destinationDirectory);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return FolderLinkInspection.ForUnverifiable(ToDisplayPath(inspection.CurrentPath), failure);
        }
    }

    /// <summary>列挙で得た拡張パスの接頭辞を除き、利用者へ示す通常のパスにします。整えられない入力はそのまま返します。</summary>
    private static string ToDisplayPath(string path)
    {
        try
        {
            return LongPathFileSystem.NormalizePathForStorage(path);
        }
        catch (Exception failure) when (failure is IOException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private sealed class MergeInspection
    {
        /// <summary>直近に調べていた位置です。失敗したときに、確認できなかった位置として報告します。</summary>
        internal string CurrentPath { get; private set; }

        internal FolderLinkInspection Run(string sourceDirectory, string destinationDirectory)
        {
            CurrentPath = sourceDirectory;
            if (!TryGetAttributes(sourceDirectory, out FileAttributes sourceAttributes))
            {
                return FolderLinkInspection.Clear;
            }
            if (IsLink(sourceDirectory, sourceAttributes))
            {
                return FolderLinkInspection.ForLink(ToDisplayPath(sourceDirectory));
            }
            if ((sourceAttributes & FileAttributes.Directory) == 0)
            {
                return FolderLinkInspection.Clear;
            }

            CurrentPath = destinationDirectory;
            string destinationRoot = null;
            if (TryGetAttributes(destinationDirectory, out FileAttributes destinationAttributes))
            {
                if (IsLink(destinationDirectory, destinationAttributes))
                {
                    return FolderLinkInspection.ForLink(ToDisplayPath(destinationDirectory));
                }
                if ((destinationAttributes & FileAttributes.Directory) != 0)
                {
                    destinationRoot = destinationDirectory;
                }
            }
            return Walk(sourceDirectory, destinationRoot);
        }

        /// <summary>統合元の一階層を調べ、同じ名前の統合先の項目もリンクかを確認します。リンクの先へは入りません。</summary>
        private FolderLinkInspection Walk(string sourceDirectory, string destinationDirectory)
        {
            Dictionary<string, FileSystemInfo> destinationEntries = null;
            if (destinationDirectory != null)
            {
                CurrentPath = destinationDirectory;
                destinationEntries = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (FileSystemInfo entry in Enumerate(destinationDirectory))
                {
                    destinationEntries[entry.Name] = entry;
                }
            }

            CurrentPath = sourceDirectory;
            List<DirectoryInfo> childDirectories = [];
            foreach (FileSystemInfo entry in Enumerate(sourceDirectory))
            {
                CurrentPath = entry.FullName;
                if (IsLink(entry))
                {
                    return FolderLinkInspection.ForLink(LongPathFileSystem.NormalizePathForStorage(entry.FullName));
                }
                if (destinationEntries != null && destinationEntries.TryGetValue(entry.Name, out FileSystemInfo counterpart))
                {
                    CurrentPath = counterpart.FullName;
                    if (IsLink(counterpart))
                    {
                        return FolderLinkInspection.ForLink(LongPathFileSystem.NormalizePathForStorage(counterpart.FullName));
                    }
                }
                if (entry is DirectoryInfo directory)
                {
                    childDirectories.Add(directory);
                }
            }

            foreach (DirectoryInfo child in childDirectories)
            {
                string destinationChild = destinationEntries != null
                    && destinationEntries.TryGetValue(child.Name, out FileSystemInfo counterpart)
                    && counterpart is DirectoryInfo
                        ? counterpart.FullName
                        : null;
                FolderLinkInspection result = Walk(child.FullName, destinationChild);
                if (result.Outcome != FolderLinkInspectionOutcome.Clear)
                {
                    return result;
                }
            }
            return FolderLinkInspection.Clear;
        }

        private static IEnumerable<FileSystemInfo> Enumerate(string directory)
            => new DirectoryInfo(LongPathFileSystem.ToExtendedPath(directory)).EnumerateFileSystemInfos("*", EntryEnumerationOptions);

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = LongPathFileSystem.GetAttributes(path);
                return true;
            }
            catch (Exception failure) when (LongPathFileSystem.IsNotFoundFailure(failure))
            {
                attributes = default;
                return false;
            }
        }

        private static bool IsLink(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }
            string extendedPath = LongPathFileSystem.ToExtendedPath(path);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(extendedPath) : new FileInfo(extendedPath);
            return info.LinkTarget != null;
        }

        private static bool IsLink(FileSystemInfo entry)
            => (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget != null;
    }
}
