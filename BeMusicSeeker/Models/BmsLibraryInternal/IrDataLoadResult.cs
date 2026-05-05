using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrDataLoadResult
{
    public List<LR2IRData> Rows { get; } = new List<LR2IRData>();

    public long DbReadMs { get; set; }

    public long MaterializeMs { get; set; }

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }
}
