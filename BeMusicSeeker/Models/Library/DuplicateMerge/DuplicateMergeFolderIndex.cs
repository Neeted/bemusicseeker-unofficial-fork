using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>
/// 現在の重複結果の全グループのフォルダを、同一・親子の関係で問い合わせられる索引です。確認の一回の間だけ使い、グループごとの問合せは全フォルダの走査を伴いません。
/// </summary>
internal sealed class DuplicateMergeFolderIndex
{
    private readonly Dictionary<string, List<(object Owner, string Folder)>> exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly (string Key, object Owner, string Folder)[] sorted;

    /// <param name="groups">グループの識別（インスタンスの同一性で区別）と、そのフォルダです。</param>
    internal DuplicateMergeFolderIndex(IEnumerable<(object Owner, IReadOnlyList<string> Folders)> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var entries = new List<(string Key, object Owner, string Folder)>();
        foreach ((object owner, IReadOnlyList<string> folders) in groups)
        {
            foreach (string folder in folders ?? [])
            {
                string key = DuplicateMergeSafetyPolicy.NormalizeFolder(folder);
                if (key.Length == 0) { continue; }
                entries.Add((key, owner, folder));
                if (!exact.TryGetValue(key, out List<(object Owner, string Folder)> owners))
                {
                    owners = [];
                    exact[key] = owners;
                }
                owners.Add((owner, folder));
            }
        }
        sorted = [.. entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 指定フォルダと同一または親子の関係にある、<paramref name="owner"/> 以外のグループのフォルダを返します。祖先側・子孫側のどちらも含みます。
    /// </summary>
    internal IReadOnlyList<string> FindRelatedFoldersOfOtherGroups(object owner, string folder)
    {
        string key = DuplicateMergeSafetyPolicy.NormalizeFolder(folder);
        var related = new List<string>();
        if (key.Length == 0) { return related; }
        void AddFrom(string candidateKey)
        {
            if (!exact.TryGetValue(candidateKey, out List<(object Owner, string Folder)> owners)) { return; }
            foreach ((object candidateOwner, string candidateFolder) in owners)
            {
                if (!ReferenceEquals(candidateOwner, owner)) { related.Add(candidateFolder); }
            }
        }
        // 同一と祖先。
        string current = key;
        while (!string.IsNullOrEmpty(current))
        {
            AddFrom(current);
            string parent = System.IO.Path.GetDirectoryName(current);
            current = string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase) ? null : parent;
        }
        // 子孫。ソート済みの配列から、接頭辞が一致する連続した範囲を読む。
        string prefix = key.EndsWith('\\') ? key : key + "\\";
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (string.Compare(sorted[middle].Key, prefix, StringComparison.OrdinalIgnoreCase) < 0) { low = middle + 1; }
            else { high = middle; }
        }
        for (int index = low; index < sorted.Length && sorted[index].Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); index++)
        {
            if (!ReferenceEquals(sorted[index].Owner, owner)) { related.Add(sorted[index].Folder); }
        }
        return related;
    }
}
