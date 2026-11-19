using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 所持ディレクトリの照会から、指定したフォルダ（とその配下）を落とす読取り専用の包みです。
/// 一括整理が「今回の予定移動元を除いた確定済みの所持」を作るために、操作開始時の installed lookup へ重ねます。
/// 予定移動元にある譜面を、移動の裏付け（残るコピー）として数えないための基準です。
/// </summary>
internal sealed class SourceExcludingInstalledChartLookup : IInstalledChartLookupIndex
{
    private readonly IInstalledChartLookupIndex baseline;
    private readonly HashSet<string> excludedDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>基準の lookup と、除外するフォルダを固定します。</summary>
    /// <param name="baseline">操作開始時の installed lookup。</param>
    /// <param name="excludedDirectories">除外するフォルダ。配下のディレクトリも除外します。正規化できない入力は無視します。</param>
    internal SourceExcludingInstalledChartLookup(IInstalledChartLookupIndex baseline, IEnumerable<string> excludedDirectories)
    {
        this.baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        foreach (string directory in excludedDirectories ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                this.excludedDirectories.Add(normalized);
            }
        }
    }

    /// <summary>基準が持つハッシュ数です。除外後の件数は求めず、診断にだけ使います。</summary>
    public int HashCount => baseline.HashCount;

    /// <summary>基準が持つ distinct な主ハッシュ数です。除外後の件数は求めず、診断にだけ使います。</summary>
    public int DistinctPrimaryHashCount => baseline.DistinctPrimaryHashCount;

    /// <summary>
    /// 除外せず基準へ委譲します。除外するのはディレクトリの照会だけで、主ハッシュの判定には、除外済みの基準
    /// （予定移動元の譜面を除いた <see cref="ExcludingPrimaryHashLookup"/> など）を別に渡す前提です。
    /// </summary>
    public bool ContainsPrimaryHash(string lookupHash) => baseline.ContainsPrimaryHash(lookupHash);

    /// <summary>除外せず基準へ委譲します。主ハッシュの判定には、除外済みの基準を別に渡す前提です。</summary>
    public int GetPrimaryHashCount(string lookupHash) => baseline.GetPrimaryHashCount(lookupHash);

    /// <summary>除外フォルダの配下を落とした、主ハッシュを含むディレクトリを返します。</summary>
    public IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash)
    {
        IReadOnlyList<string> directories = baseline.GetDistinctDirectoriesByPrimaryHash(lookupHash);
        return excludedDirectories.Count == 0 || directories.Count == 0
            ? directories
            : [.. directories.Where(directory => !IsExcluded(directory))];
    }

    /// <summary>除外フォルダの配下のディレクトリは 0 件として返します。</summary>
    public int GetUniquePrimaryHashCountByDirectory(string directoryPath)
        => IsExcluded(directoryPath) ? 0 : baseline.GetUniquePrimaryHashCountByDirectory(directoryPath);

    private bool IsExcluded(string directory)
    {
        for (string current = Lr2FolderPath.NormalizeDirectoryPath(directory);
            !string.IsNullOrWhiteSpace(current);
            current = Lr2FolderPath.SafeGetParentNormalizedDirectory(current))
        {
            if (excludedDirectories.Contains(current))
            {
                return true;
            }
        }
        return false;
    }
}
