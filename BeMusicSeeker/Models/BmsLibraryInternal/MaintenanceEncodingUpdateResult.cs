using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class MaintenanceEncodingUpdateResult
{
    public List<BMSFile> SongsToUpsert { get; } = new List<BMSFile>();

    public List<BMSFileMaintenanceInfo> MaintenanceInfosToUpsert { get; } = new List<BMSFileMaintenanceInfo>();
}
