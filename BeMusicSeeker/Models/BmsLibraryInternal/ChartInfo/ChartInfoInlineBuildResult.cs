using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoInlineBuildResult
{
    public bool Canceled { get; set; }

    public List<LibraryChartDigestChange> DigestChanges { get; } = [];

    public List<BeMusicSeeker.Models.ChartDetails> ChartInfoRows { get; } = [];

    public List<BeMusicSeeker.Models.ChartDetails> AppliedRows { get; } = [];

    public List<BeMusicSeeker.Models.ChartParseFailure> ParseFailureRows { get; } = [];

    public List<string> ParseFailureDeleteMd5s { get; } = [];

    /// <summary>
    /// Owner projections staged until the catalog storage transaction commits.
    /// </summary>
    internal List<ChartInfoStorageApplication> StorageApplications { get; } = [];

    public int TargetCount { get; set; }

    public int SuccessCount { get; set; }

    public int CurrentSkippedCount { get; set; }

    public int FailureSkippedCount { get; set; }

    public int ParseFailedCount { get; set; }

    public int FailurePersistedCount { get; set; }

    public int FailureClearedCount { get; set; }

    public int ReadFailedCount { get; set; }

    public long ParseMs { get; set; }
}

/// <summary>
/// Snapshot identity and owner projection staged until the catalog transaction commits.
/// Snapshot bytes are intentionally not retained.
/// </summary>
internal sealed class ChartInfoStorageApplication(
    ChartFile chart,
    string md5,
    string sha256,
    System.DateTime lastWriteTimeUtc,
    BeMusicSeeker.Models.ChartDetails row)
{
    internal ChartFile Chart { get; } = chart;

    internal string Md5 { get; } = md5 ?? string.Empty;

    internal string Sha256 { get; } = sha256 ?? string.Empty;

    internal System.DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;

    internal BeMusicSeeker.Models.ChartDetails Row { get; } = row;

    /// <summary>捕捉したファイル識別と確定した詳細値を持つ共通値を作成します。</summary>
    internal ChartFile CreateCurrentValue(ChartFile current = null)
    {
        ChartFile source = current ?? Chart;
        ChartFile next = source with { Md5 = Md5, Sha256 = Sha256, LastWriteTimeUtc = LastWriteTimeUtc };
        if (Row == null || !string.Equals(Row.md5, Md5, System.StringComparison.OrdinalIgnoreCase))
        {
            bool digestChanged = !string.Equals(source.Md5, Md5, System.StringComparison.OrdinalIgnoreCase)
                || !string.Equals(source.Sha256, Sha256, System.StringComparison.OrdinalIgnoreCase);
            return digestChanged ? ChartFileProjection.WithChartInfo(next, null) : next;
        }
        next = ChartFileProjection.WithChartInfo(next, Row);
        return next with
        {
            Level = Row.level,
            LevelText = Row.level?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty,
            Difficulty = Lr2ChartInfoSongProjection.NormalizeDifficulty(Row.difficulty),
            Mode = Row.mode
        };
    }
}
