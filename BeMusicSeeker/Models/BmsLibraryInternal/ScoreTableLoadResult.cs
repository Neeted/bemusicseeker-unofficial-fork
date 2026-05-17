using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// アプリ内で有効化されている score source を表します。
/// </summary>
internal enum ActiveScoreSource
{
    None,
    Lr2,
    Beatoraja
}

internal sealed class ScoreTableLoadResult
{
    public List<BMSScore> Scores { get; } = [];

    public Dictionary<string, BMSScore> BeatorajaScoresBySha256 { get; } = new Dictionary<string, BMSScore>(System.StringComparer.OrdinalIgnoreCase);

    public ActiveScoreSource ActiveScoreSource { get; set; }

    public int LR2Id { get; set; }

    public bool EnableDownloadLr2IrScoreAndDetectUnsent { get; set; } = true;

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }
}
