using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>文字コード変更で確定する基本値・保守値・現在値です。</summary>
internal sealed class MaintenanceEncodingUpdateResult
{
    /// <summary>再復号の差分がある既存songの限定更新です。保留分は所持管理主体が保存前に除きます。</summary>
    internal List<SongMetadataUpdate> SongMetadataUpdates { get; } = [];
    internal List<ResourceHealthMaintenanceSnapshot> MaintenanceInfosToUpsert { get; } = [];
    internal List<ChartFile> ChangedCharts { get; } = [];
    /// <summary>所持分の保存・現在値確定後に同じ一括反映へ渡す健康度索引差分です。保留だけなら空です。</summary>
    internal ResourceHealthIndexMutation ResourceHealthMutation { get; } = new();
}
