using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class LibraryRowCacheBuildStats
{
    internal int HitCount { get; set; }

    internal int MissCount { get; set; }

    internal int PrunedCount { get; set; }
}

/// <summary>所持tokenごとに表示行を保持し、変更された項目だけを更新します。</summary>
internal sealed class NormalLibraryRowCache
{
    private readonly Dictionary<OwnedChartToken, LibraryChartRow> rowsByToken = [];
    private readonly Dictionary<string, HashSet<OwnedChartToken>> tokensByMd5 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<OwnedChartToken>> tokensBySha256 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<OwnedChartToken, (string Md5, string Sha256)> scoreKeysByToken = [];

    internal int Count => rowsByToken.Count;
    internal List<LibraryChartRow> SnapshotRows() => [.. rowsByToken.Values];

    internal LibraryChartRow GetOrCreate(ChartFile chart, LibraryRowCacheBuildStats stats)
    {
        if (chart?.Token == null)
        {
            return null;
        }
        if (rowsByToken.TryGetValue(chart.Token, out LibraryChartRow row))
        {
            if (stats != null)
            {
                stats.HitCount++;
            }
            row.UpdateSourceProjection(chart);
            UpdateScoreKeys(chart);
            return row;
        }
        row = LibraryChartRow.FromChartFile(chart);
        rowsByToken.Add(chart.Token, row);
        UpdateScoreKeys(chart);
        if (stats != null)
        {
            stats.MissCount++;
        }
        return row;
    }

    /// <summary>通知で捕捉した変更値と削除tokenだけを既存の表示行へ反映します。</summary>
    internal void ApplyChanges(IEnumerable<ChartFile> changedCharts, IEnumerable<OwnedChartToken> deletedTokens,
        Action<LibraryChartRow> configureRow)
    {
        foreach (OwnedChartToken token in deletedTokens ?? [])
        {
            if (token != null && rowsByToken.Remove(token))
            {
                RemoveScoreKeys(token);
            }
        }
        foreach (ChartFile chart in changedCharts ?? [])
        {
            if (chart?.Token != null && rowsByToken.TryGetValue(chart.Token, out LibraryChartRow row))
            {
                row.UpdateSourceProjection(chart);
                UpdateScoreKeys(chart);
                configureRow?.Invoke(row);
                row.RefreshDisplayForDataDependency(MainViewDataDependency.SourceMembership);
            }
        }
    }

    /// <summary>変更したスコア識別子に対応する、作成済み表示行だけを返します。</summary>
    internal IReadOnlyList<LibraryChartRow> GetRowsForScoreKeys(IEnumerable<string> md5Keys, IEnumerable<string> sha256Keys)
    {
        HashSet<OwnedChartToken> tokens = [];
        CaptureTokens(tokensByMd5, md5Keys, tokens);
        CaptureTokens(tokensBySha256, sha256Keys, tokens);
        return [.. tokens.Select(token => rowsByToken[token])];
    }

    internal void Clear()
    {
        rowsByToken.Clear();
        tokensByMd5.Clear();
        tokensBySha256.Clear();
        scoreKeysByToken.Clear();
    }

    private void UpdateScoreKeys(ChartFile chart)
    {
        if (scoreKeysByToken.TryGetValue(chart.Token, out (string Md5, string Sha256) previous)
            && string.Equals(previous.Md5, chart.Md5, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.Sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        RemoveScoreKeys(chart.Token);
        scoreKeysByToken[chart.Token] = (chart.Md5, chart.Sha256);
        AddKey(tokensByMd5, chart.Md5, chart.Token);
        AddKey(tokensBySha256, chart.Sha256, chart.Token);
    }

    private void RemoveScoreKeys(OwnedChartToken token)
    {
        if (scoreKeysByToken.Remove(token, out (string Md5, string Sha256) keys))
        {
            RemoveKey(tokensByMd5, keys.Md5, token);
            RemoveKey(tokensBySha256, keys.Sha256, token);
        }
    }

    private static void AddKey(Dictionary<string, HashSet<OwnedChartToken>> index, string key, OwnedChartToken token)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (!index.TryGetValue(key, out HashSet<OwnedChartToken> tokens))
        {
            index.Add(key, tokens = []);
        }
        tokens.Add(token);
    }

    private static void RemoveKey(Dictionary<string, HashSet<OwnedChartToken>> index, string key, OwnedChartToken token)
    {
        if (!string.IsNullOrWhiteSpace(key) && index.TryGetValue(key, out HashSet<OwnedChartToken> tokens)
            && tokens.Remove(token) && tokens.Count == 0)
        {
            index.Remove(key);
        }
    }

    private static void CaptureTokens(Dictionary<string, HashSet<OwnedChartToken>> index,
        IEnumerable<string> keys, HashSet<OwnedChartToken> target)
    {
        foreach (string key in keys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && index.TryGetValue(key, out HashSet<OwnedChartToken> tokens))
            {
                target.UnionWith(tokens);
            }
        }
    }
}
