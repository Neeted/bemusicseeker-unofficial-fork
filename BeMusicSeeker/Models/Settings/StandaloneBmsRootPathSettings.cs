using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>
/// standalone mode の BMS root 設定を永続化形式から読み書きする adapter です。
/// </summary>
internal static class StandaloneBmsRootPathSettings
{
    /// <summary>
    /// 保存値または旧形式の root を読み込み、存在しない値も保持します。
    /// </summary>
    /// <param name="serializedPaths">改行区切りで保存された root。</param>
    /// <param name="legacyBmsRootPath">新形式が空の場合に使う旧 root。</param>
    /// <returns>重複と空値を除いた設定済み root。</returns>
    internal static IReadOnlyList<string> Deserialize(
        string serializedPaths,
        string legacyBmsRootPath = null)
    {
        List<string> paths = [];
        if (!string.IsNullOrWhiteSpace(serializedPaths))
        {
            paths.AddRange(serializedPaths.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries));
        }
        if (paths.Count == 0
            && !string.IsNullOrWhiteSpace(legacyBmsRootPath))
        {
            paths.Add(legacyBmsRootPath);
        }
        return NormalizeConfigured(paths);
    }

    /// <summary>
    /// 候補 root を存在確認付きで正規化します。新規入力の候補検証など、
    /// 既存ディレクトリだけを扱う read route で使用します。
    /// </summary>
    /// <param name="paths">正規化する候補 root。</param>
    /// <returns>存在する root の重複除去済み一覧。</returns>
    internal static IReadOnlyList<string> Normalize(IEnumerable<string> paths)
    {
        if (paths == null)
        {
            return [];
        }

        List<string> normalized = [];
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            string fullPath = NormalizeConfiguredPath(path);
            if (string.IsNullOrWhiteSpace(fullPath)
                || !LongPathFileSystem.DirectoryExists(fullPath))
            {
                continue;
            }
            if (!normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(fullPath);
            }
        }

        return normalized;
    }

    /// <summary>
    /// root を永続化用の改行区切り文字列へ変換します。missing root は保持します。
    /// </summary>
    /// <param name="paths">保存する root。</param>
    /// <returns>正規化済み root の改行区切り文字列。</returns>
    internal static string Serialize(IEnumerable<string> paths)
    {
        return string.Join(Environment.NewLine, NormalizeConfigured(paths));
    }

    /// <summary>
    /// 保存済みの root を存在確認なしで正規化します。
    /// missing root は次の model preflight が用途と原因を報告できるよう保持します。
    /// </summary>
    /// <param name="paths">設定として保持する root。</param>
    /// <returns>空値と重複を除いた設定 root。</returns>
    internal static IReadOnlyList<string> NormalizeConfigured(IEnumerable<string> paths)
    {
        List<string> normalized = [];
        foreach (string path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalizedPath = NormalizeConfiguredPath(path);
            if (!string.IsNullOrWhiteSpace(normalizedPath)
                && !normalized.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(normalizedPath);
            }
        }
        return normalized;
    }

    private static string NormalizeConfiguredPath(string path)
    {
        string trimmed = path?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        try
        {
            return LongPathFileSystem.TrimTrailingDirectorySeparators(
                LongPathFileSystem.NormalizePathForStorage(trimmed));
        }
        catch
        {
            // 不正な非空値は消去せず、更新入口の typed preflight へ渡します。
            return trimmed;
        }
    }
}
