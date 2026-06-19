using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using Newtonsoft.Json;

namespace BeMusicSeeker.ViewModels;

public enum PlayHistoryDisplayTargetKind
{
    All,
    Playlist,
    TargetSet
}

internal enum PlayHistoryDisplayTargetMode
{
    /// <summary>
    /// 対象 playlist / preset に一致する履歴だけを残し、FOLDER も同じ対象で投影します。
    /// </summary>
    FilterAndProject,

    /// <summary>
    /// 履歴行は落とさず、FOLDER だけを対象 preset で投影します。
    /// </summary>
    ProjectOnly
}

public sealed class PlayHistoryDisplayTargetItem
{
    private PlayHistoryDisplayTargetItem(
        PlayHistoryDisplayTargetKind kind,
        PlayHistoryDisplayTargetMode mode,
        string identity,
        string displayName,
        BMSTable table,
        PlayHistoryDisplayTargetSet targetSet)
    {
        Kind = kind;
        Mode = mode;
        Identity = identity ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        Table = table;
        TargetSet = targetSet;
    }

    internal PlayHistoryDisplayTargetKind Kind { get; }

    internal PlayHistoryDisplayTargetMode Mode { get; }

    internal string Identity { get; }

    public string DisplayName { get; }

    internal BMSTable Table { get; }

    internal PlayHistoryDisplayTargetSet TargetSet { get; }

    internal bool UsesProjection => Kind != PlayHistoryDisplayTargetKind.All;

    internal bool IsFiltering => UsesProjection && Mode == PlayHistoryDisplayTargetMode.FilterAndProject;

    /// <summary>
    /// 現在の表示言語で作った「すべて」表示対象を取得します。
    /// </summary>
    internal static PlayHistoryDisplayTargetItem All => CreateAll();

    /// <summary>
    /// 現在の表示言語で「すべて」表示対象を作成します。
    /// 言語切替時に候補を再構築するため、固定 singleton にはしません。
    /// </summary>
    /// <returns>全 play history row を対象にする display target。</returns>
    internal static PlayHistoryDisplayTargetItem CreateAll()
    {
        return new PlayHistoryDisplayTargetItem(
            PlayHistoryDisplayTargetKind.All,
            PlayHistoryDisplayTargetMode.FilterAndProject,
            "all",
            BeMusicSeeker.Properties.Resources.Play_history_period_all,
            null,
            null);
    }

    internal static PlayHistoryDisplayTargetItem FromPlaylist(BMSTable table)
    {
        string id = table?.playlist_id?.ToString(CultureInfo.InvariantCulture)
            ?? (table?.name ?? string.Empty) + "|" + (table?.symbol ?? string.Empty);
        string name = string.IsNullOrWhiteSpace(table?.name)
            ? table?.symbol
            : table.name;
        return new PlayHistoryDisplayTargetItem(
            PlayHistoryDisplayTargetKind.Playlist,
            PlayHistoryDisplayTargetMode.FilterAndProject,
            "playlist:" + id,
            string.IsNullOrWhiteSpace(name) ? "(playlist)" : name,
            table,
            null);
    }

    internal static PlayHistoryDisplayTargetItem FromTargetSet(PlayHistoryDisplayTargetSet targetSet)
    {
        return FromTargetSet(targetSet, PlayHistoryDisplayTargetMode.FilterAndProject);
    }

    internal static PlayHistoryDisplayTargetItem FromTargetSetProjectionOnly(PlayHistoryDisplayTargetSet targetSet)
    {
        return FromTargetSet(targetSet, PlayHistoryDisplayTargetMode.ProjectOnly);
    }

    private static PlayHistoryDisplayTargetItem FromTargetSet(PlayHistoryDisplayTargetSet targetSet, PlayHistoryDisplayTargetMode mode)
    {
        string name = string.IsNullOrWhiteSpace(targetSet?.Name)
            ? "(set)"
            : targetSet.Name.Trim();
        bool projectOnly = mode == PlayHistoryDisplayTargetMode.ProjectOnly;
        return new PlayHistoryDisplayTargetItem(
            PlayHistoryDisplayTargetKind.TargetSet,
            mode,
            (projectOnly ? "set-folder:" : "set:") + name.ToUpperInvariant(),
            string.Format(
                CultureInfo.CurrentCulture,
                projectOnly
                    ? BeMusicSeeker.Properties.Resources.Play_history_display_target_folder_only_set_format
                    : BeMusicSeeker.Properties.Resources.Play_history_display_target_set_format,
                name),
            null,
            targetSet);
    }
}

public sealed class PlayHistoryDisplayTargetSet
{
    public string Name { get; set; }

    public List<PlayHistoryDisplayTargetReference> Targets { get; set; } = [];
}

public sealed class PlayHistoryDisplayTargetReference
{
    public int? PlaylistId { get; set; }

    public string PlaylistName { get; set; }

    public string PlaylistSymbol { get; set; }

    public string FolderLabel { get; set; }
}

internal static class PlayHistoryDisplayTargetSetStore
{
    internal static IReadOnlyList<PlayHistoryDisplayTargetSet> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            List<PlayHistoryDisplayTargetSet> sets = JsonConvert.DeserializeObject<List<PlayHistoryDisplayTargetSet>>(json);
            return Normalize(sets);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string Serialize(IEnumerable<PlayHistoryDisplayTargetSet> targetSets)
    {
        IReadOnlyList<PlayHistoryDisplayTargetSet> normalized = Normalize(targetSets);
        return normalized.Count == 0
            ? string.Empty
            : JsonConvert.SerializeObject(normalized, Formatting.None);
    }

    /// <summary>
    /// 設定ダイアログの draft 変更検知用に、無効な行も落とさず安定した JSON へ変換します。
    /// 保存形式の正規化は validation 後だけに限定し、invalid draft を「変更なし」と誤判定しないために分けています。
    /// </summary>
    /// <param name="targetSets">設定ダイアログ上の未保存 target set。</param>
    /// <returns>変更検知用 JSON。draft が空の場合は空文字列。</returns>
    internal static string SerializeDraftsForChangeTracking(IEnumerable<PlayHistoryDisplayTargetSet> targetSets)
    {
        List<PlayHistoryDisplayTargetSet> drafts =
        [
            .. (targetSets ?? [])
                .Select(targetSet => new PlayHistoryDisplayTargetSet
                {
                    Name = targetSet?.Name ?? string.Empty,
                    Targets =
                    [
                        .. (targetSet?.Targets ?? [])
                            .Where(reference => reference != null)
                            .Select(reference => new PlayHistoryDisplayTargetReference
                            {
                                PlaylistId = reference.PlaylistId,
                                PlaylistName = reference.PlaylistName,
                                PlaylistSymbol = reference.PlaylistSymbol,
                                FolderLabel = reference.FolderLabel
                            })
                    ]
                })
        ];
        return drafts.Count == 0
            ? string.Empty
            : JsonConvert.SerializeObject(drafts, Formatting.None);
    }

    private static IReadOnlyList<PlayHistoryDisplayTargetSet> Normalize(IEnumerable<PlayHistoryDisplayTargetSet> targetSets)
    {
        var result = new List<PlayHistoryDisplayTargetSet>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlayHistoryDisplayTargetSet targetSet in targetSets ?? [])
        {
            string name = targetSet?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                continue;
            }
            List<PlayHistoryDisplayTargetReference> targets =
            [
                .. (targetSet.Targets ?? [])
                    .Where(HasReferenceIdentity)
                    .Select(reference => new PlayHistoryDisplayTargetReference
                    {
                        PlaylistId = reference.PlaylistId,
                        PlaylistName = NormalizeText(reference.PlaylistName),
                        PlaylistSymbol = NormalizeText(reference.PlaylistSymbol),
                        FolderLabel = NormalizeText(reference.FolderLabel)
                    })
            ];
            if (targets.Count == 0)
            {
                continue;
            }
            result.Add(new PlayHistoryDisplayTargetSet
            {
                Name = name,
                Targets = targets
            });
        }
        return new ReadOnlyCollection<PlayHistoryDisplayTargetSet>(result);
    }

    private static bool HasReferenceIdentity(PlayHistoryDisplayTargetReference reference)
    {
        return reference != null
            && (reference.PlaylistId.HasValue
                || !string.IsNullOrWhiteSpace(reference.PlaylistName)
                || !string.IsNullOrWhiteSpace(reference.PlaylistSymbol));
    }

    private static string NormalizeText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

internal sealed class PlayHistoryDisplayTargetIndex
{
    private readonly Dictionary<string, List<PlayHistoryDisplayTargetMatch>> md5Map = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<PlayHistoryDisplayTargetMatch>> sha256Map = new(StringComparer.OrdinalIgnoreCase);

    private readonly PlayHistoryDisplayTargetItem target;

    private PlayHistoryDisplayTargetIndex(PlayHistoryDisplayTargetItem target)
    {
        this.target = target ?? PlayHistoryDisplayTargetItem.All;
    }

    internal static PlayHistoryDisplayTargetIndex Create(
        PlayHistoryDisplayTargetItem target,
        IEnumerable<BMSTable> tables,
        Action<BMSTable> ensureEntriesLoaded,
        CancellationToken cancellationToken = default,
        Func<bool> shouldContinue = null)
    {
        var index = new PlayHistoryDisplayTargetIndex(target);
        if (target == null || target.Kind == PlayHistoryDisplayTargetKind.All)
        {
            return index;
        }
        IEnumerable<(BMSTable table, PlayHistoryDisplayTargetReference reference)> tableReferences = target.Kind == PlayHistoryDisplayTargetKind.Playlist
            ? [(target.Table, null)]
            : ResolveTargetSetTables(tables, target.TargetSet);
        foreach ((BMSTable table, PlayHistoryDisplayTargetReference reference) in tableReferences)
        {
            ThrowIfStale(cancellationToken, shouldContinue);
            if (table == null)
            {
                continue;
            }
            ensureEntriesLoaded?.Invoke(table);
            ThrowIfStale(cancellationToken, shouldContinue);
            List<BMSTableEntry> entries;
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                entries = [.. table.GetEntriesExceptDummy()];
            }
            for (int indexInTable = 0; indexInTable < entries.Count; indexInTable++)
            {
                if ((indexInTable & 0x7f) == 0)
                {
                    ThrowIfStale(cancellationToken, shouldContinue);
                }
                BMSTableEntry entry = entries[indexInTable];
                if (entry == null || entry.is_removed || !MatchesFolderReference(table, entry, reference))
                {
                    continue;
                }
                index.AddEntry(table, entry);
            }
        }
        return index;
    }

    private static void ThrowIfStale(CancellationToken cancellationToken, Func<bool> shouldContinue)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldContinue != null && !shouldContinue())
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    internal bool TryApply(PlayHistoryRow row, out PlayHistoryRow displayRow)
    {
        displayRow = row;
        if (row == null)
        {
            return row != null;
        }
        List<PlayHistoryDisplayTargetMatch> matches = ResolveMatches(row);
        if (matches.Count == 0)
        {
            if (target.Kind == PlayHistoryDisplayTargetKind.All)
            {
                displayRow = row;
                return true;
            }
            if (target.IsFiltering)
            {
                displayRow = null;
                return false;
            }
            displayRow = row.WithPlaylistDisplay(string.Empty);
            return true;
        }
        string folderLabels = string.Join(
            " ",
            matches
                .Select(match => FormatFolderLabel(match))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.Ordinal));
        displayRow = row.WithPlaylistDisplay(folderLabels);
        return true;
    }

    private void AddEntry(BMSTable table, BMSTableEntry entry)
    {
        var match = new PlayHistoryDisplayTargetMatch(table, entry);
        if (!string.IsNullOrWhiteSpace(entry.md5))
        {
            AddMatch(md5Map, entry.md5, match);
        }
        else
        {
            AddMatch(sha256Map, entry.sha256, match);
        }
    }

    private List<PlayHistoryDisplayTargetMatch> ResolveMatches(PlayHistoryRow row)
    {
        var matches = new List<PlayHistoryDisplayTargetMatch>();
        AddMatches(matches, md5Map, row.RawHash);
        AddMatches(matches, md5Map, row.Md5);
        AddMatches(matches, sha256Map, row.Sha256);
        return matches
            .GroupBy(match => match.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private string FormatFolderLabel(PlayHistoryDisplayTargetMatch match)
    {
        if (target.Kind == PlayHistoryDisplayTargetKind.Playlist)
        {
            return match.Entry?.folder ?? string.Empty;
        }
        string symbol = FirstNonEmpty(match.Table?.org_symbol, match.Table?.symbol);
        string level = NormalizeFolderLabel(match.Table, match.Entry);
        return string.IsNullOrWhiteSpace(symbol)
            ? level
            : (string.IsNullOrWhiteSpace(level) ? symbol : symbol + level);
    }

    private static IEnumerable<(BMSTable table, PlayHistoryDisplayTargetReference reference)> ResolveTargetSetTables(
        IEnumerable<BMSTable> tables,
        PlayHistoryDisplayTargetSet targetSet)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        Dictionary<int, List<BMSTable>> tablesByPlaylistId = tableList
            .Where(table => table.playlist_id.HasValue)
            .GroupBy(table => table.playlist_id.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (PlayHistoryDisplayTargetReference reference in targetSet?.Targets ?? [])
        {
            if (reference?.PlaylistId is int playlistId)
            {
                if (tablesByPlaylistId.TryGetValue(playlistId, out List<BMSTable> matchedTables))
                {
                    foreach (BMSTable table in matchedTables)
                    {
                        yield return (table, reference);
                    }
                }
                continue;
            }
            foreach (BMSTable table in tableList)
            {
                if (MatchesTableReference(table, reference))
                {
                    yield return (table, reference);
                }
            }
        }
    }

    private static bool MatchesTableReference(BMSTable table, PlayHistoryDisplayTargetReference reference)
    {
        if (table == null || reference == null)
        {
            return false;
        }
        if (reference.PlaylistId.HasValue)
        {
            return table.playlist_id == reference.PlaylistId;
        }
        return MatchesText(table.name, reference.PlaylistName)
            || MatchesText(table.org_name, reference.PlaylistName)
            || MatchesText(table.symbol, reference.PlaylistSymbol)
            || MatchesText(table.org_symbol, reference.PlaylistSymbol);
    }

    private static bool MatchesFolderReference(BMSTable table, BMSTableEntry entry, PlayHistoryDisplayTargetReference reference)
    {
        if (reference == null || string.IsNullOrWhiteSpace(reference.FolderLabel))
        {
            return true;
        }
        string folderLabel = reference.FolderLabel.Trim();
        return MatchesText(entry?.folder, folderLabel)
            || MatchesText(NormalizeFolderLabel(table, entry), folderLabel);
    }

    private static string NormalizeFolderLabel(BMSTable table, BMSTableEntry entry)
    {
        string folder = entry?.folder ?? string.Empty;
        return table == null ? folder : table.ConvertBackFolderNameToCompatibleLevelName(folder);
    }

    private static void AddMatch(Dictionary<string, List<PlayHistoryDisplayTargetMatch>> map, string key, PlayHistoryDisplayTargetMatch match)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        string normalizedKey = key.Trim();
        if (!map.TryGetValue(normalizedKey, out List<PlayHistoryDisplayTargetMatch> matches))
        {
            matches = [];
            map[normalizedKey] = matches;
        }
        matches.Add(match);
    }

    private static void AddMatches(
        List<PlayHistoryDisplayTargetMatch> targetMatches,
        Dictionary<string, List<PlayHistoryDisplayTargetMatch>> map,
        string key)
    {
        if (!string.IsNullOrWhiteSpace(key) && map.TryGetValue(key.Trim(), out List<PlayHistoryDisplayTargetMatch> matches))
        {
            targetMatches.AddRange(matches);
        }
    }

    private static bool MatchesText(string value, string expected)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(expected)
            && string.Equals(value.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private sealed class PlayHistoryDisplayTargetMatch
    {
        internal PlayHistoryDisplayTargetMatch(BMSTable table, BMSTableEntry entry)
        {
            Table = table;
            Entry = entry;
            Identity = (table?.playlist_id?.ToString(CultureInfo.InvariantCulture) ?? table?.name ?? string.Empty)
                + "|"
                + (entry?.md5 ?? string.Empty)
                + "|"
                + (entry?.sha256 ?? string.Empty)
                + "|"
                + (entry?.folder ?? string.Empty);
        }

        internal BMSTable Table { get; }

        internal BMSTableEntry Entry { get; }

        internal string Identity { get; }
    }
}
