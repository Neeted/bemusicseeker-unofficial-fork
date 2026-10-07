using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>文字コード変更で確定する基本値・保守値・現在値です。</summary>
internal sealed class MaintenanceEncodingUpdateResult
{
    internal List<ChartFile> SongsToUpsert { get; } = [];
    internal List<ResourceHealthMaintenanceSnapshot> MaintenanceInfosToUpsert { get; } = [];
    internal List<ChartFile> ChangedCharts { get; } = [];
}
