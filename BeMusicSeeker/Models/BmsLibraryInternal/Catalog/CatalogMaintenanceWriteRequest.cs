using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>保守値と既存songのメタデータ限定更新を、同じDB確定境界へ渡す不変の要求です。</summary>
internal sealed class CatalogMaintenanceWriteRequest
{
    /// <summary>通常の保守値と保留の文字コード限定更新を、同じ保存境界へ渡す変更不能な要求に捕捉します。</summary>
    internal CatalogMaintenanceWriteRequest(IEnumerable<ResourceHealthMaintenanceSnapshot> maintenanceInfos = null,
        IEnumerable<SongMetadataUpdate> songMetadataUpdates = null, IEnumerable<string> staleMaintenancePaths = null,
        IEnumerable<ChartFile> currentValues = null,
        IEnumerable<ResourceHealthMaintenanceSnapshot> encodingOnlyInfos = null)
    {
        MaintenanceInfos = Array.AsReadOnly([.. (maintenanceInfos ?? []).Where(value => value != null && !string.IsNullOrWhiteSpace(value.Path))]);
        SongMetadataUpdates = Array.AsReadOnly([.. (songMetadataUpdates ?? []).Where(value => value != null && !string.IsNullOrWhiteSpace(value.Path))]);
        StaleMaintenancePaths = Array.AsReadOnly([.. (staleMaintenancePaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)]);
        CurrentValues = Array.AsReadOnly([.. (currentValues ?? []).Where(value => value != null)]);
        EncodingOnlyInfos = Array.AsReadOnly([.. (encodingOnlyInfos ?? []).Where(value => value != null && !string.IsNullOrWhiteSpace(value.Path))]);
    }
    internal IReadOnlyList<ResourceHealthMaintenanceSnapshot> MaintenanceInfos { get; }
    /// <summary>生メタデータ5列と更新時刻だけを更新します。識別不一致・未登録のsongを新設しません。</summary>
    internal IReadOnlyList<SongMetadataUpdate> SongMetadataUpdates { get; }
    internal IReadOnlyList<string> StaleMaintenancePaths { get; }
    internal IReadOnlyList<ChartFile> CurrentValues { get; }
    /// <summary>保留の文字コードだけを保存する対象です。Path・Hash・Encoding・EncodingFixed以外を保存へ流用しません。</summary>
    internal IReadOnlyList<ResourceHealthMaintenanceSnapshot> EncodingOnlyInfos { get; }
    internal bool HasChanges => MaintenanceInfos.Count > 0 || SongMetadataUpdates.Count > 0 || StaleMaintenancePaths.Count > 0 || EncodingOnlyInfos.Count > 0;
}

internal sealed class CatalogMaintenanceWriteReceipt
{
    internal static CatalogMaintenanceWriteReceipt NotApplied { get; } =
        new(false, 0, 0, 0, 0);

    internal CatalogMaintenanceWriteReceipt(
        bool applied,
        int maintenanceInfoCount,
        int songCount,
        int bmsonSongCount,
        int deletedMaintenanceCount)
    {
        Applied = applied;
        MaintenanceInfoCount = maintenanceInfoCount;
        SongCount = songCount;
        BmsonSongCount = bmsonSongCount;
        DeletedMaintenanceCount = deletedMaintenanceCount;
    }

    internal bool Applied { get; }

    internal int MaintenanceInfoCount { get; }

    internal int SongCount { get; }

    internal int BmsonSongCount { get; }

    internal int DeletedMaintenanceCount { get; }
}
