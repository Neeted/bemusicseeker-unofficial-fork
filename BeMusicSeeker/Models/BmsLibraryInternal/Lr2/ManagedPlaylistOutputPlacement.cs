using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>LR2管理表の実効絶対パスを比較し、同一・親子の出力配置を副作用前に拒否します。</summary>
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

    /// <summary>二つの実効先が同一、またはいずれかの子孫となるかを判定します。</summary>
    internal static bool Overlaps(string left, string right)
    {
        left = Normalize(left);
        right = Normalize(right);
        return Lr2FolderPath.IsSameOrDescendantNormalized(left, right)
            || Lr2FolderPath.IsSameOrDescendantNormalized(right, left);
    }

    /// <summary>変更後の実効先集合を検査します。未解決の表は他表を止めず個別通知へ委ね、大文字小文字だけの差も同一として拒否します。</summary>
    internal static bool IsValid(IEnumerable<string> directories)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) { continue; }
            if (!paths.Add(Normalize(directory))) { return false; }
        }
        foreach (string path in paths)
        {
            for (string parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
            {
                if (paths.Contains(Normalize(parent))) { return false; }
            }
        }
        return true;
    }

    /// <summary>衝突した集合を利用者向けの配置エラーとして停止します。</summary>
    internal static void Validate(IEnumerable<string> directories)
    {
        if (!IsValid(directories)) { throw new InvalidOperationException(Resources.Error_ManagedPlaylistOutputOverlap); }
    }
}
