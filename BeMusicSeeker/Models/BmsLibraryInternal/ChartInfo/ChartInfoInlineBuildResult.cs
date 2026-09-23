using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoInlineBuildResult
{
    public bool Canceled { get; set; }

    public List<LibraryChartDigestChange> DigestChanges { get; } = [];

    public List<LR2SongDBExtended.chart_info> ChartInfoRows { get; } = [];

    public List<LR2SongDBExtended.chart_info> AppliedRows { get; } = [];

    public List<LR2SongDBExtended.chart_info_parse_failure> ParseFailureRows { get; } = [];

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
    LR2SongDBExtended.chart_info row)
{
    internal ChartFile Chart { get; } = chart;

    internal string Md5 { get; } = md5 ?? string.Empty;

    internal string Sha256 { get; } = sha256 ?? string.Empty;

    internal System.DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;

    internal LR2SongDBExtended.chart_info Row { get; } = row;

    internal BMSFile CreateBmsPersistenceCopy() =>
        ChartStorageOwnerMutator.CreateBmsPersistenceCopy(Chart, Md5, Sha256, Row);

    internal LR2SongDBExtended.bmson_song CreateBmsonPersistenceCopy() =>
        ChartStorageOwnerMutator.CreateBmsonPersistenceCopy(Chart, Md5, Sha256, LastWriteTimeUtc);

    internal int ApplyCommitted(ICollection<LibraryChartDigestChange> digestChanges) =>
        ChartStorageOwnerMutator.ApplyCommittedSnapshot(
            Chart,
            Md5,
            Sha256,
            LastWriteTimeUtc,
            Row,
            digestChanges);
}
