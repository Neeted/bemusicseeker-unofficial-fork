using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable durable facts produced by the maintenance evaluator.
/// The evaluator never opens a database; the catalog mutation owner consumes this request.
/// </summary>
internal sealed class CatalogMaintenanceWriteRequest
{
    internal CatalogMaintenanceWriteRequest(
        IEnumerable<BMSFileMaintenanceInfo> maintenanceInfos = null,
        IEnumerable<BMSFile> songs = null,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs = null,
        IEnumerable<string> staleMaintenancePaths = null)
    {
        MaintenanceInfos = Array.AsReadOnly([.. (maintenanceInfos ?? [])
            .Where(info => info != null && !string.IsNullOrWhiteSpace(info.path))
            .Select(info => info.CreatePersistenceCopy())]);
        Songs = Array.AsReadOnly([.. (songs ?? [])
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
            .Select(song => song.CreateSongRowPersistenceCopy())]);
        BmsonSongs = Array.AsReadOnly([.. (bmsonSongs ?? [])
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
            .Select(CreateBmsonPersistenceCopy)]);
        StaleMaintenancePaths = [.. (staleMaintenancePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal IReadOnlyList<BMSFileMaintenanceInfo> MaintenanceInfos { get; }

    internal IReadOnlyList<BMSFile> Songs { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs { get; }

    internal IReadOnlyList<string> StaleMaintenancePaths { get; }

    internal bool HasChanges => MaintenanceInfos.Count > 0
        || Songs.Count > 0
        || BmsonSongs.Count > 0
        || StaleMaintenancePaths.Count > 0;

    private static LR2SongDBExtended.bmson_song CreateBmsonPersistenceCopy(LR2SongDBExtended.bmson_song source)
    {
        var copy = new LR2SongDBExtended.bmson_song
        {
            path = source.path,
            folder = source.folder,
            title = source.title,
            subtitle = source.subtitle,
            artist = source.artist,
            genre = source.genre,
            level = source.level,
            mode_hint = source.mode_hint,
            md5 = source.md5,
            sha256 = source.sha256,
            banner = source.banner,
            backbmp = source.backbmp,
            stagefile = source.stagefile,
            preview_music = source.preview_music,
            updated_at = source.updated_at
        };
        copy.MaintenanceInfo = source.MaintenanceInfo?.CreatePersistenceCopy();
        return copy;
    }
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
