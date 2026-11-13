using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>LR2管理表の実効絶対パスを比較し、同一・親子の出力配置を求めます。編集は副作用前に拒否し、保存済みの衝突は衝突した表だけを出力対象外にします。</summary>
internal static class ManagedPlaylistOutputPlacement
{
    /// <summary>保存前の表属性から実効絶対パスを求めます。未登録の追加名は出力停止を表すnullとし、ROOTは追加名を使いません。</summary>
    internal static string Resolve(string name, string outputDirectory, string baseName, bool isRoot,
        CustomFolderOutputSettingsSnapshot settings)
    {
        string outputBase = isRoot ? settings.LR2CustomFolderOutputBaseDirRootType
            : CustomFolderOutputBaseRegistry.ResolveNormalOutputBaseDirectory(baseName,
                settings.LR2CustomFolderOutputBaseDir, settings.LR2CustomFolderAdditionalOutputBaseDirs);
        if (!isRoot && !string.IsNullOrWhiteSpace(baseName) && outputBase == null) { return null; }
        return Normalize(Path.Combine(outputBase, BMSTable.ResolveOutputDirectoryName(name, outputDirectory)));
    }

    /// <summary>現在の表の実効先を求めます。登録名変更の候補は確定前の設定検査だけで指定します。</summary>
    internal static string Resolve(BMSTable table, CustomFolderOutputSettingsSnapshot settings,
        IReadOnlyDictionary<string, string> renames = null)
    {
        string baseName = table.custom_folder_output_base_name;
        if (baseName != null && renames?.TryGetValue(baseName, out string renamed) == true
            && (renamed == null || CustomFolderOutputBaseRegistry.ContainsBaseName(renamed, settings.LR2CustomFolderAdditionalOutputBaseDirs)))
        {
            baseName = renamed;
        }
        return Resolve(table.name, table.output_dir, baseName, table.is_root_folder, settings);
    }

    /// <summary>区切り、相対要素、末尾区切りを正規化します。不正なパスを別の配置へ置換しません。</summary>
    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>表の実効先を、同一性の判定に使う識別子と対にした配置です。</summary>
    /// <param name="Key">表を表す識別子。同じ表は前後の配置で同じ値（通常は表の参照）にします。</param>
    /// <param name="Directory">実効絶対パス。未解決（未登録の追加基点など）の表はnullまたは空で、検査の対象外です。</param>
    internal readonly record struct Placement(object Key, string Directory);

    /// <summary>
    /// 実効先が他のいずれかの配置と同一（大文字小文字だけの差を含む）または親子になる配置の識別子を、すべて返します。
    /// 一操作の中で配置の一覧から一度だけ求め、永続しません。未解決の配置は他の配置を止めません。
    /// </summary>
    internal static HashSet<object> FindColliding(IEnumerable<Placement> placements)
    {
        var owners = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (Placement placement in placements ?? [])
        {
            if (string.IsNullOrWhiteSpace(placement.Directory)) { continue; }
            string path = Normalize(placement.Directory);
            if (!owners.TryGetValue(path, out List<object> keys)) { owners[path] = keys = []; }
            keys.Add(placement.Key);
        }
        var colliding = new HashSet<object>();
        foreach (KeyValuePair<string, List<object>> owner in owners)
        {
            if (owner.Value.Count > 1) { colliding.UnionWith(owner.Value); }
            for (string parent = Path.GetDirectoryName(owner.Key); parent != null; parent = Path.GetDirectoryName(parent))
            {
                if (!owners.TryGetValue(Normalize(parent), out List<object> parentKeys)) { continue; }
                colliding.UnionWith(owner.Value);
                colliding.UnionWith(parentKeys);
            }
        }
        return colliding;
    }

    /// <summary>現在の設定で、出力先が他の表と重なる表をすべて返します。<paramref name="renames"/>と<paramref name="overrides"/>は未公開の候補だけを置き換えます。</summary>
    internal static HashSet<BMSTable> FindCollidingTables(IEnumerable<BMSTable> tables, CustomFolderOutputSettingsSnapshot settings,
        IReadOnlyDictionary<string, string> renames = null, IReadOnlyDictionary<BMSTable, string> overrides = null)
    {
        var colliding = new HashSet<BMSTable>();
        if (settings?.OperationModeLR2DB != true) { return colliding; }
        List<Placement> placements = [];
        foreach (BMSTable table in tables ?? [])
        {
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir)) { continue; }
            string directory = overrides?.TryGetValue(table, out string overridden) == true ? overridden : Resolve(table, settings, renames);
            placements.Add(new Placement(table, directory));
        }
        foreach (object key in FindColliding(placements)) { colliding.Add((BMSTable)key); }
        return colliding;
    }

    /// <summary>
    /// 編集の事前検査です。公開済みの配置（前）と候補の配置（後）を比べ、配置が変わる表または新規の表のいずれかが、
    /// 後の集合で他の表と同一・親子になる場合だけ拒否します。配置が変わらない表同士の既存の衝突は問いません。
    /// 表の削除は配置を加えないため、この検査を行いません。
    /// </summary>
    internal static bool IsEditRejected(IEnumerable<Placement> before, IEnumerable<Placement> after)
    {
        var previous = new Dictionary<object, string>();
        foreach (Placement placement in before ?? []) { previous[placement.Key] = string.IsNullOrWhiteSpace(placement.Directory) ? null : Normalize(placement.Directory); }
        List<Placement> next = [.. after ?? []];
        HashSet<object> colliding = FindColliding(next);
        foreach (Placement placement in next)
        {
            if (string.IsNullOrWhiteSpace(placement.Directory) || !colliding.Contains(placement.Key)) { continue; }
            bool unchanged = previous.TryGetValue(placement.Key, out string old)
                && string.Equals(old, Normalize(placement.Directory), StringComparison.OrdinalIgnoreCase);
            if (!unchanged) { return true; }
        }
        return false;
    }

    /// <summary>編集の事前検査に失敗した配置を、利用者向けの配置エラーとして停止します。</summary>
    internal static void ValidateEdit(IEnumerable<Placement> before, IEnumerable<Placement> after)
    {
        if (IsEditRejected(before, after)) { throw new InvalidOperationException(Resources.Error_ManagedPlaylistOutputOverlap); }
    }
}
