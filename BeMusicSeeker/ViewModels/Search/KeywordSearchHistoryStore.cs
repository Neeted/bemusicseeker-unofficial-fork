using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 検索履歴を user settings 用の文字列へ変換する helper です。
/// 区切り文字を検索構文と衝突させないため、各行を UTF-8 Base64 として保存します。
/// </summary>
internal static class KeywordSearchHistoryStore
{
    /// <summary>
    /// 保存する履歴件数の上限です。
    /// </summary>
    internal const int MaxHistoryCount = 20;

    /// <summary>
    /// 設定文字列から検索履歴を復元し、保存式の共通比較で重複を除いて先頭20件を返します。
    /// </summary>
    /// <param name="serializedHistory">Base64 行形式の履歴文字列。</param>
    /// <returns>復元した履歴一覧。</returns>
    internal static IReadOnlyList<string> Deserialize(string serializedHistory)
    {
        if (string.IsNullOrWhiteSpace(serializedHistory))
        {
            return [];
        }
        List<string> history = [];
        foreach (string line in serializedHistory.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string entry = Encoding.UTF8.GetString(Convert.FromBase64String(line.Trim()));
                string normalizedEntry = NormalizeEntryForIdentity(entry);
                if (!string.IsNullOrEmpty(normalizedEntry) && !history.Contains(normalizedEntry, KeywordSearchSavedQueryComparer.Instance))
                {
                    history.Add(normalizedEntry);
                }
            }
            catch (FormatException)
            {
                // NOTE:
                // user.config は手動編集や旧版混在で壊れる可能性があるため、
                // 復元できない行は履歴全体を捨てずに無視します。
            }
        }
        return [.. history.Take(MaxHistoryCount)];
    }

    /// <summary>
    /// 保存式の共通比較で重複を除き、先頭20件の検索履歴を設定文字列へ変換します。
    /// </summary>
    /// <param name="history">保存する履歴一覧。</param>
    /// <returns>Base64 行形式の履歴文字列。</returns>
    internal static string Serialize(IEnumerable<string> history)
    {
        string[] entries = [.. NormalizeEntries(history)];
        return string.Join(
            Environment.NewLine,
            entries.Select(entry => Convert.ToBase64String(Encoding.UTF8.GetBytes(entry))));
    }

    /// <summary>
    /// 検索履歴へ新しい表記の検索式を先頭追加し、保存式の共通比較で一致する旧履歴を除きます。
    /// </summary>
    /// <param name="history">現在の履歴一覧。</param>
    /// <param name="entry">追加する検索文字列。</param>
    /// <returns>追加後の履歴一覧。</returns>
    internal static IReadOnlyList<string> AddEntry(IEnumerable<string> history, string entry)
    {
        string normalizedEntry = NormalizeEntryForIdentity(entry);
        if (string.IsNullOrEmpty(normalizedEntry))
        {
            return [.. NormalizeEntries(history)];
        }
        List<string> entries =
        [
            normalizedEntry,
            .. NormalizeEntries(history).Where(current => !KeywordSearchSavedQueryComparer.Instance.Equals(current, normalizedEntry)),
        ];
        return [.. entries.Take(MaxHistoryCount)];
    }

    private static IEnumerable<string> NormalizeEntries(IEnumerable<string> history)
    {
        List<string> entries = [];
        foreach (string entry in history ?? [])
        {
            string normalizedEntry = NormalizeEntryForIdentity(entry);
            if (!string.IsNullOrEmpty(normalizedEntry) && !entries.Contains(normalizedEntry, KeywordSearchSavedQueryComparer.Instance))
            {
                entries.Add(normalizedEntry);
            }
            if (entries.Count >= MaxHistoryCount)
            {
                break;
            }
        }
        return entries;
    }

    /// <summary>
    /// 保存式のCR・LFを一文字ずつ空白へ置換して前後空白を除き、旧保存形式との互換性を保ちます。
    /// 大小文字・内部空白・引用・式の順序は変換せず、同一性は共通の比較規則へ委ねます。
    /// </summary>
    internal static string NormalizeEntryForIdentity(string entry)
    {
        return (entry ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}
