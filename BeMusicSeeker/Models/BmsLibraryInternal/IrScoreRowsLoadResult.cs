using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrScoreRowsLoadResult
{
    public List<LR2IRScore> Rows { get; } = new List<LR2IRScore>();

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }
}
