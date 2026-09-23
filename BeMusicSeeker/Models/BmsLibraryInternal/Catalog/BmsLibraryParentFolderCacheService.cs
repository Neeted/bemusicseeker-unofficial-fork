using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryParentFolderCacheService
{
    /// <summary>
    /// 登録済み root から親フォルダ候補を構築します。
    /// installed chart path は呼び出し元が捕捉した read-only list をそのまま参照します。
    /// </summary>
    /// <param name="bmsDirectories">登録済み BMS root。</param>
    /// <param name="installedChartPaths">呼び出し元が捕捉したインストール済み chart path。</param>
    /// <param name="options">現在のライブラリ設定。</param>
    /// <returns>親フォルダ候補。</returns>
    public List<string> BuildParentFolderCandidates(IEnumerable<string> bmsDirectories, IReadOnlyList<string> installedChartPaths, BmsLibraryOptionsSnapshot options)
    {
        bool isLr2Mode = options?.OperationModeLR2DB == true;
        IReadOnlyList<string> customFolderOutputBases = isLr2Mode
            ? CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(
                new[] { options.LR2CustomFolderOutputBaseDir }
                    .Concat(options.LR2CustomFolderAdditionalOutputBaseDirs ?? [])
                    .Append(options.LR2CustomFolderOutputBaseDirRootType))
            : [];
        return [.. (bmsDirectories ?? []).Where(delegate (string directoryPath)
        {
            if (isLr2Mode)
            {
                string normalizedDirectoryPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(directoryPath);
                if (!string.IsNullOrWhiteSpace(normalizedDirectoryPath)
                    && customFolderOutputBases.Any(outputBase => IsSameOrChildPath(normalizedDirectoryPath, outputBase)))
                {
                    return false;
                }
            }
            if ((installedChartPaths ?? []).Any(delegate (string chartPath)
            {
                return !string.IsNullOrWhiteSpace(chartPath)
                    && chartPath.StartsWith(directoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }))
            {
                return true;
            }
            if (isLr2Mode)
            {
                try
                {
                    return !LongPathFileSystem.EnumerateFiles(directoryPath, "*.lr2folder", SearchOption.AllDirectories).Any();
                }
                catch
                {
                    return false;
                }
            }
            return true;
        })];
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 親フォルダ候補を所定の version 付き cache snapshot にします。
    /// </summary>
    /// <param name="version">cache version。</param>
    /// <param name="installedChartPaths">呼び出し元が捕捉したインストール済み chart path。</param>
    /// <param name="bmsDirectories">登録済み BMS root。</param>
    /// <param name="options">現在のライブラリ設定。</param>
    /// <returns>親フォルダ一覧 cache snapshot。</returns>
    public BMSLibrary.ParentFolderListCacheSnapshot BuildSnapshot(int version, IReadOnlyList<string> installedChartPaths, IEnumerable<string> bmsDirectories, BmsLibraryOptionsSnapshot options)
    {
        var stopwatch = Stopwatch.StartNew();
        List<string> parentFolders = BuildParentFolderCandidates(bmsDirectories, installedChartPaths, options);
        stopwatch.Stop();
        return new BMSLibrary.ParentFolderListCacheSnapshot
        {
            Version = version,
            RebuildMs = stopwatch.ElapsedMilliseconds,
            ParentFolders = parentFolders
        };
    }
}
