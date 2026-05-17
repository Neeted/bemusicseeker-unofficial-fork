using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class MaintenanceEncodingUpdateResult
{
    public List<BMSFile> SongsToUpsert { get; } = [];

    public List<BMSFileMaintenanceInfo> MaintenanceInfosToUpsert { get; } = [];
}
