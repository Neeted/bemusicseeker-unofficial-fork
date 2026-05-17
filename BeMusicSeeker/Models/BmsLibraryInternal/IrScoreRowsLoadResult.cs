using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrScoreRowsLoadResult
{
    public List<LR2IRScore> Rows { get; } = [];

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }
}
