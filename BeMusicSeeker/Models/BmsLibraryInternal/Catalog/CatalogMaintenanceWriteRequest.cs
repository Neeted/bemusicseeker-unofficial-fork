using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>保守評価が確定する不変の共通値です。保存行への変換はDB境界で行います。</summary>
internal sealed class CatalogMaintenanceWriteRequest
{
    internal CatalogMaintenanceWriteRequest(IEnumerable<ResourceHealthMaintenanceSnapshot> maintenanceInfos = null,
        IEnumerable<ChartFile> songs = null, IEnumerable<string> staleMaintenancePaths = null,
        IEnumerable<ChartFile> currentValues = null)
    {
        MaintenanceInfos = Array.AsReadOnly([.. (maintenanceInfos ?? []).Where(value => value != null && !string.IsNullOrWhiteSpace(value.Path))]);
        Songs = Array.AsReadOnly([.. (songs ?? []).Where(value => value != null && !string.IsNullOrWhiteSpace(value.Path))]);
        StaleMaintenancePaths = Array.AsReadOnly([.. (staleMaintenancePaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)]);
        CurrentValues = Array.AsReadOnly([.. (currentValues ?? []).Where(value => value != null)]);
    }
    internal IReadOnlyList<ResourceHealthMaintenanceSnapshot> MaintenanceInfos { get; }
    internal IReadOnlyList<ChartFile> Songs { get; }
    internal IReadOnlyList<string> StaleMaintenancePaths { get; }
    internal IReadOnlyList<ChartFile> CurrentValues { get; }
    internal bool HasChanges => MaintenanceInfos.Count > 0 || Songs.Count > 0 || StaleMaintenancePaths.Count > 0;
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
