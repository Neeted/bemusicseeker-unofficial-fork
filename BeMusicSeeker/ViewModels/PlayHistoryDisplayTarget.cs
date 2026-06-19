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

public sealed class PlayHistoryDisplayTargetItem
{
    private PlayHistoryDisplayTargetItem(
        PlayHistoryDisplayTargetKind kind,
        string identity,
        string displayName,
        BMSTable table,
        PlayHistoryDisplayTargetSet targetSet)
    {
        Kind = kind;
        Identity = identity ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        Table = table;
        TargetSet = targetSet;
    }

    internal PlayHistoryDisplayTargetKind Kind { get; }

    internal string Identity { get; }

    public string DisplayName { get; }

    internal BMSTable Table { get; }

    internal PlayHistoryDisplayTargetSet TargetSet { get; }

    internal bool IsFiltering => Kind != PlayHistoryDisplayTargetKind.All;

    internal static PlayHistoryDisplayTargetItem All { get; } =
        new(PlayHistoryDisplayTargetKind.All, "all", "すべて", null, null);

    internal static PlayHistoryDisplayTargetItem FromPlaylist(BMSTable table)
    {
        string id = table?.playlist_id?.ToString(CultureInfo.InvariantCulture)
            ?? (table?.name ?? string.Empty) + "|" + (table?.symbol ?? string.Empty);
        string name = string.IsNullOrWhiteSpace(table?.name)
            ? table?.symbol
            : table.name;
        return new PlayHistoryDisplayTargetItem(
            PlayHistoryDisplayTargetKind.Playlist,
            "playlist:" + id,
            string.IsNullOrWhiteSpace(name) ? "(playlist)" : name,
            table,
            null);
    }

    internal static PlayHistoryDisplayTargetItem FromTargetSet(PlayHistoryDisplayTargetSet targetSet)
    {
        string name = string.IsNullOrWhiteSpace(targetSet?.Name)
            ? "(set)"
            : targetSet.Name.Trim();
        return new PlayHistoryDisplayTargetItem(
            PlayHistoryDisplayTargetKind.TargetSet,
            "set:" + name.ToUpperInvariant(),
            "Set: " + name,
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
        if (row == null || target.Kind == PlayHistoryDisplayTargetKind.All)
        {
            return row != null;
        }
        List<PlayHistoryDisplayTargetMatch> matches = ResolveMatches(row);
        if (matches.Count == 0)
        {
            displayRow = null;
            return false;
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
        foreach (PlayHistoryDisplayTargetReference reference in targetSet?.Targets ?? [])
        {
            foreach (BMSTable table in tables ?? [])
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
        if (reference.PlaylistId.HasValue && table.playlist_id == reference.PlaylistId)
        {
            return true;
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
