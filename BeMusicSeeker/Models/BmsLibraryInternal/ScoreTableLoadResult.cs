using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ScoreTableLoadResult
{
    public List<BMSScore> Scores { get; } = new List<BMSScore>();

    public int LR2Id { get; set; }
}
