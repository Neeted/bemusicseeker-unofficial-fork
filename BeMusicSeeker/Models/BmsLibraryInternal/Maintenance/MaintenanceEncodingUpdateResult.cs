using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>文字コード変更で確定する基本値・保守値・現在値です。</summary>
internal sealed class MaintenanceEncodingUpdateResult
{
    /// <summary>再復号の差分がある既存songの限定更新です。保留分は所持管理主体が保存前に除きます。</summary>
    internal List<SongMetadataUpdate> SongMetadataUpdates { get; } = [];
    internal List<ResourceHealthMaintenanceSnapshot> MaintenanceInfosToUpsert { get; } = [];
    internal List<ChartFile> ChangedCharts { get; } = [];
}
