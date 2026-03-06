using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchGroup
{
    public string DestinationDirectory { get; set; }

    public List<PendingInstallBatchItem> Items { get; } = new List<PendingInstallBatchItem>();
}
