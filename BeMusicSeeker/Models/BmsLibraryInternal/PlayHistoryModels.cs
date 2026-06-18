using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum PlayHistoryProvider
{
    Lr2,
    Beatoraja
}

internal enum PlayHistoryHashKind
{
    Chart,
    ExpertCourse,
    NonstopCourse,
    GradeCourse,
    Unknown
}

internal enum PlayHistoryDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

internal sealed class PlayHistoryDiagnostic
{
    internal PlayHistoryProvider Provider { get; set; }

    internal string Stage { get; set; } = string.Empty;

    internal PlayHistoryDiagnosticSeverity Severity { get; set; }

    internal string Code { get; set; } = string.Empty;

    internal string Message { get; set; } = string.Empty;

    internal string SourcePath { get; set; } = string.Empty;
}

internal sealed class PlayHistorySourceProfile
{
    internal PlayHistorySourceProfile(PlayHistoryProvider provider, string sourcePath, string displayName = null)
    {
        Provider = provider;
        SourcePath = sourcePath ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Provider.ToString() : displayName;
    }

    internal PlayHistoryProvider Provider { get; }

    internal string SourcePath { get; }

    internal string DisplayName { get; }

    internal static PlayHistorySourceProfile Lr2(string scoreDbPath)
    {
        return new PlayHistorySourceProfile(PlayHistoryProvider.Lr2, scoreDbPath, "LR2");
    }
}

internal sealed class Lr2PlayHistoryReadRequest
{
    internal string ScoreDbPath { get; set; }

    internal bool IsLr2LinkedProfile { get; set; } = true;

    internal long? PlayedAtFromInclusive { get; set; }

    internal long? PlayedAtToExclusive { get; set; }

    internal bool IncludeUnfinalized { get; set; }

    internal int? Limit { get; set; }
}

internal sealed class Lr2PlayHistoryReadResult
{
    internal Lr2PlayHistoryReadResult(
        PlayHistorySourceProfile sourceProfile,
        IReadOnlyList<Lr2PlayHistoryRecord> rows,
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics,
        Lr2PlayHistorySchemaStatus schemaStatus)
    {
        SourceProfile = sourceProfile;
        Rows = rows ?? [];
        Diagnostics = diagnostics ?? [];
        SchemaStatus = schemaStatus;
    }

    internal PlayHistorySourceProfile SourceProfile { get; }

    internal IReadOnlyList<Lr2PlayHistoryRecord> Rows { get; }

    internal IReadOnlyList<PlayHistoryDiagnostic> Diagnostics { get; }

    internal Lr2PlayHistorySchemaStatus SchemaStatus { get; }

    internal bool HasErrors
    {
        get
        {
            foreach (PlayHistoryDiagnostic diagnostic in Diagnostics)
            {
                if (diagnostic?.Severity == PlayHistoryDiagnosticSeverity.Error)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

internal sealed class Lr2PlayHistoryRecord
{
    public long history_id { get; set; }

    public string hash { get; set; }

    public long played_at { get; set; }

    public int finalized { get; set; }

    public string score_write_type { get; set; }

    public int? old_playcount { get; set; }

    public int new_playcount { get; set; }

    public int playcount_delta { get; set; }

    public int? old_clearcount { get; set; }

    public int? new_clearcount { get; set; }

    public int? clearcount_delta { get; set; }

    public int? old_failcount { get; set; }

    public int? new_failcount { get; set; }

    public int? failcount_delta { get; set; }

    public int? old_clear { get; set; }

    public int? new_clear { get; set; }

    public int? old_clear_db { get; set; }

    public int? new_clear_db { get; set; }

    public int? old_clear_sd { get; set; }

    public int? new_clear_sd { get; set; }

    public int? old_clear_ex { get; set; }

    public int? new_clear_ex { get; set; }

    public int? old_minbp { get; set; }

    public int? new_minbp { get; set; }

    public int? old_exscore { get; set; }

    public int? new_exscore { get; set; }

    public int? old_maxcombo { get; set; }

    public int? new_maxcombo { get; set; }

    public int? old_totalnotes { get; set; }

    public int? new_totalnotes { get; set; }

    public int? old_complete { get; set; }

    public int? new_complete { get; set; }

    public int? old_op_best { get; set; }

    public int? new_op_best { get; set; }

    public int? old_op_history { get; set; }

    public int? new_op_history { get; set; }

    public int? old_rseed { get; set; }

    public int? new_rseed { get; set; }

    public string old_scorehash { get; set; }

    public string new_scorehash { get; set; }

    public int? old_player_playcount { get; set; }

    public int? new_player_playcount { get; set; }

    public int? player_playcount_delta { get; set; }

    public int? old_playtime_total { get; set; }

    public int? new_playtime_total { get; set; }

    public int? playtime_delta { get; set; }

    public int? old_judge_total { get; set; }

    public int? new_judge_total { get; set; }

    public int? judge_delta { get; set; }

    public int? old_player_perfect { get; set; }

    public int? new_player_perfect { get; set; }

    public int? perfect_delta { get; set; }

    public int? old_player_great { get; set; }

    public int? new_player_great { get; set; }

    public int? great_delta { get; set; }

    public int? old_player_good { get; set; }

    public int? new_player_good { get; set; }

    public int? good_delta { get; set; }

    public int? old_player_bad { get; set; }

    public int? new_player_bad { get; set; }

    public int? bad_delta { get; set; }

    public int? old_player_poor { get; set; }

    public int? new_player_poor { get; set; }

    public int? poor_delta { get; set; }

    public int? old_player_maxcombo { get; set; }

    public int? new_player_maxcombo { get; set; }
}

internal sealed class PlayHistoryProjectionIndex
{
    private readonly IReadOnlyDictionary<string, string> sha256ByMd5;

    private readonly Func<string, string, PlaylistReferenceDisplay> playlistReferenceResolver;

    private readonly Func<string, string, LR2SongDBExtended.chart_info> chartInfoResolver;

    private PlayHistoryProjectionIndex(
        PlaylistLibraryResolveIndexSnapshot resolveIndex,
        IReadOnlyDictionary<string, string> sha256ByMd5,
        Func<string, string, PlaylistReferenceDisplay> playlistReferenceResolver,
        Func<string, string, LR2SongDBExtended.chart_info> chartInfoResolver)
    {
        ResolveIndex = resolveIndex ?? PlaylistLibraryResolveIndexSnapshot.Empty;
        var sha256Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in sha256ByMd5 ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                sha256Map[pair.Key.Trim()] = pair.Value.Trim();
            }
        }
        this.sha256ByMd5 = new ReadOnlyDictionary<string, string>(sha256Map);
        this.playlistReferenceResolver = playlistReferenceResolver;
        this.chartInfoResolver = chartInfoResolver;
    }

    internal static PlayHistoryProjectionIndex Empty { get; } = new(
        PlaylistLibraryResolveIndexSnapshot.Empty,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        null,
        null);

    internal PlaylistLibraryResolveIndexSnapshot ResolveIndex { get; }

    internal static PlayHistoryProjectionIndex Create(
        PlaylistLibraryResolveIndexSnapshot resolveIndex,
        IReadOnlyDictionary<string, string> sha256ByMd5 = null,
        Func<string, string, PlaylistReferenceDisplay> playlistReferenceResolver = null,
        Func<string, string, LR2SongDBExtended.chart_info> chartInfoResolver = null)
    {
        return new PlayHistoryProjectionIndex(resolveIndex, sha256ByMd5, playlistReferenceResolver, chartInfoResolver);
    }

    internal LibraryChartRef ResolveChartByMd5(string md5, string sha256)
    {
        return ResolveIndex.ResolveChartForPlaylistHash(md5, sha256);
    }

    internal string ResolveSha256(string md5, string currentSha256)
    {
        if (!string.IsNullOrWhiteSpace(currentSha256))
        {
            return currentSha256.Trim();
        }
        if (!string.IsNullOrWhiteSpace(md5) && sha256ByMd5.TryGetValue(md5.Trim(), out string sha256))
        {
            return sha256;
        }
        return string.Empty;
    }

    internal PlaylistReferenceDisplay ResolvePlaylistReference(string md5, string sha256)
    {
        return playlistReferenceResolver?.Invoke(md5, sha256) ?? PlaylistReferenceDisplay.Empty;
    }

    internal LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
    {
        return chartInfoResolver?.Invoke(sha256, md5);
    }
}
