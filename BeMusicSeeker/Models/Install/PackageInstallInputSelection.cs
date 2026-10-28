using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>一要求の元入力が直下全項目を選択したdirectoryだけを、コピー前に捕捉します。空集合も捕捉済みの事実です。</summary>
internal static class PackageInstallInputSelection
{
    /// <summary>選択された実在譜面fileの親だけ、元の全選択と直下一覧を照合します。明示directory・書庫だけの親は列挙せず、必要な取得失敗は空の成功へ置換しません。</summary>
    internal static IReadOnlySet<string> Capture(IEnumerable<string> paths)
    {
        string[] selected = [.. (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(LongPathFileSystem.NormalizePathForStorage).Distinct(StringComparer.OrdinalIgnoreCase)];
        var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, string> group in selected.GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key)
                || !group.Any(path => ChartFileKindResolver.IsSupportedChartFilePath(path) && LongPathFileSystem.FileExists(path)))
            {
                continue;
            }
            var entries = new HashSet<string>(LongPathFileSystem.EnumerateFileSystemEntries(group.Key, "*", SearchOption.TopDirectoryOnly)
                .Select(LongPathFileSystem.NormalizePathForStorage), StringComparer.OrdinalIgnoreCase);
            if (entries.SetEquals(group)) { complete.Add(group.Key); }
        }
        return complete.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>捕捉済みの全項目選択を展開前に親directory入力へまとめます。内包書庫を別展開せず、その他の入力と書庫の順序は維持します。</summary>
    internal static IReadOnlyList<string> NormalizeForExpansion(IEnumerable<string> paths, IReadOnlySet<string> fullySelectedDirectories)
    {
        var result = new List<string>();
        var emittedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string parent = Path.GetDirectoryName(LongPathFileSystem.NormalizePathForStorage(path));
            if (!string.IsNullOrWhiteSpace(parent) && fullySelectedDirectories.Contains(parent))
            {
                if (emittedDirectories.Add(parent)) { result.Add(parent); }
            }
            else { result.Add(path); }
        }
        return result;
    }
}
