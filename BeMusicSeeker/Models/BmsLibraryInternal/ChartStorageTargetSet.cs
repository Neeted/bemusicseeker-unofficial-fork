using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartStorageTargetSet
{
    private ChartStorageTargetSet(
        List<BMSFile> bmsFiles,
        List<LR2SongDBExtended.bmson_song> bmsonSongs,
        List<ChartFile> charts,
        List<BMSFile> databaseBmsFiles = null,
        List<LR2SongDBExtended.bmson_song> databaseBmsonSongs = null,
        List<(BMSFile Owner, string Path)> installedBmsOwnerPaths = null,
        List<(LR2SongDBExtended.bmson_song Owner, string Path)> installedBmsonOwnerPaths = null)
    {
        BmsFiles = bmsFiles ?? [];
        BmsonSongs = bmsonSongs ?? [];
        Charts = charts ?? [];
        DatabaseBmsFiles = databaseBmsFiles == null
            ? BmsFiles
            : databaseBmsFiles;
        DatabaseBmsonSongs = databaseBmsonSongs == null
            ? BmsonSongs
            : databaseBmsonSongs;
        this.installedBmsOwnerPaths = installedBmsOwnerPaths ?? [];
        this.installedBmsonOwnerPaths = installedBmsonOwnerPaths ?? [];
    }

    private readonly List<(BMSFile Owner, string Path)> installedBmsOwnerPaths;

    private readonly List<(LR2SongDBExtended.bmson_song Owner, string Path)> installedBmsonOwnerPaths;

    /// <summary>導入後にowned collectionへ渡すlive BMS ownerの列挙です。</summary>
    internal List<BMSFile> BmsFiles { get; }

    /// <summary>導入後にowned collectionへ渡すlive BMSON ownerの列挙です。</summary>
    internal List<LR2SongDBExtended.bmson_song> BmsonSongs { get; }

    /// <summary>preflight確定destinationを持つchart projection列挙です。</summary>
    internal List<ChartFile> Charts { get; }

    /// <summary>
    /// DBへ書き込むdetached rowです。導入targetではlive ownerと分離されます。
    /// </summary>
    internal List<BMSFile> DatabaseBmsFiles { get; }

    /// <summary>
    /// DBへ書き込むdetached BMSON rowです。導入targetではlive ownerと分離されます。
    /// </summary>
    internal List<LR2SongDBExtended.bmson_song> DatabaseBmsonSongs { get; }

    internal List<string> GetDistinctChartDirectories()
    {
        return [.. Charts
            .Select(chart => chart?.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(DirectoryExt.GetDirectoryNameSimple)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// DB durable receipt後に、導入先をlive storage ownerへ反映します。
    /// detached rowの書込み前には呼び出してはいけません。
    /// </summary>
    internal void ApplyInstalledOwnerPaths()
    {
        if (installedBmsOwnerPaths.Count == 0 && installedBmsonOwnerPaths.Count == 0)
        {
            return;
        }

        using (BMSFile.SuppressPropertyChangedScope())
        {
            foreach ((BMSFile owner, string path) in installedBmsOwnerPaths)
            {
                if (owner != null && !string.IsNullOrWhiteSpace(path))
                {
                    owner.path = path;
                }
            }

            foreach ((LR2SongDBExtended.bmson_song owner, string path) in installedBmsonOwnerPaths)
            {
                if (owner != null && !string.IsNullOrWhiteSpace(path))
                {
                    owner.path = path;
                    owner.folder = Path.GetDirectoryName(path) ?? string.Empty;
                }
            }
        }
    }

    /// <summary>確定した譜面のstorage ownerを集め、BMSONは同じexact path内だけで集約します。</summary>
    internal static ChartStorageTargetSet FromCharts(IEnumerable<ChartFile> charts)
    {
        List<BMSFile> bmsFiles = [];
        List<ChartFile> bmsCharts = [];
        var bmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.Ordinal);
        var bmsonChartsByPath = new Dictionary<string, ChartFile>(StringComparer.Ordinal);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null)
            {
                continue;
            }

            BMSFile bmsFile = chart.GetBmsStorageOwner();
            if (ChartFileKindResolver.IsBmsChartFile(bmsFile))
            {
                ThrowIfInvalidStorageIdentity(bmsFile.path, bmsFile.hash);
                bmsFiles.Add(bmsFile);
                bmsCharts.Add(ChartFileProjection.FromBmsFile(
                    bmsFile,
                    includeWarningSnapshot: false,
                    includeResourceReferences: true,
                    includeScoreSnapshot: false));
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
            if (bmsonSong != null)
            {
                ThrowIfInvalidStorageIdentity(bmsonSong.path, bmsonSong.md5);
                bmsonSongsByPath[bmsonSong.path] = bmsonSong;
                bmsonChartsByPath[bmsonSong.path] = ChartFileProjection.FromBmsonSong(
                    bmsonSong,
                    includeWarningSnapshot: false,
                    includeResourceReferences: true);
            }
        }

        return new ChartStorageTargetSet(
            bmsFiles,
            [.. bmsonSongsByPath.Values],
            [.. bmsCharts.Concat(bmsonChartsByPath.Values)]);
    }

    /// <summary>
    /// 導入preflightが確定したdestinationを保持したtarget setを作成します。
    /// live ownerはsource pathのまま保持し、DB用rowだけをdestination projectionへ切り離します。
    /// </summary>
    internal static ChartStorageTargetSet FromInstalledCharts(IEnumerable<ChartFile> charts)
    {
        List<BMSFile> bmsFiles = [];
        List<BMSFile> databaseBmsFiles = [];
        List<ChartFile> installedCharts = [];
        List<(BMSFile Owner, string Path)> installedBmsOwnerPaths = [];
        var bmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.Ordinal);
        var databaseBmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.Ordinal);
        var installedBmsonOwnerPathsByPath = new Dictionary<string, (LR2SongDBExtended.bmson_song Owner, string Path)>(StringComparer.Ordinal);
        var chartsByBmsonPath = new Dictionary<string, ChartFile>(StringComparer.Ordinal);

        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                continue;
            }

            BMSFile bmsFile = chart.GetBmsStorageOwner();
            if (ChartFileKindResolver.IsBmsChartFile(bmsFile))
            {
                ThrowIfInvalidStorageIdentity(chart.Path, bmsFile.hash);
                BMSFile databaseRow = bmsFile.CreateSongRowPersistenceCopy();
                databaseRow.path = chart.Path;
                bmsFiles.Add(bmsFile);
                databaseBmsFiles.Add(databaseRow);
                installedCharts.Add(chart);
                installedBmsOwnerPaths.Add((bmsFile, chart.Path));
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
            if (bmsonSong == null)
            {
                continue;
            }

            ThrowIfInvalidStorageIdentity(chart.Path, bmsonSong.md5);
            LR2SongDBExtended.bmson_song bmsonDatabaseRow =
                CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(bmsonSong);
            bmsonDatabaseRow.path = chart.Path;
            bmsonDatabaseRow.folder = Path.GetDirectoryName(chart.Path) ?? string.Empty;
            bmsonSongsByPath[chart.Path] = bmsonSong;
            databaseBmsonSongsByPath[chart.Path] = bmsonDatabaseRow;
            installedBmsonOwnerPathsByPath[chart.Path] = (bmsonSong, chart.Path);
            chartsByBmsonPath[chart.Path] = chart;
        }

        installedCharts.AddRange(chartsByBmsonPath.Values);
        return new ChartStorageTargetSet(
            bmsFiles,
            [.. bmsonSongsByPath.Values],
            installedCharts,
            databaseBmsFiles,
            [.. databaseBmsonSongsByPath.Values],
            installedBmsOwnerPaths,
            [.. installedBmsonOwnerPathsByPath.Values]);
    }

    private static void ThrowIfInvalidStorageIdentity(string path, string md5)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(md5))
        {
            throw new InvalidOperationException("Owned chart storage rows must have non-empty path and md5.");
        }
    }

    /// <summary>指定されたexact pathごとのstorage行を、別keyを畳まず反映対象にします。</summary>
    internal static ChartStorageTargetSet FromRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [];
        foreach (BMSFile file in bmsFiles ?? [])
        {
            if (!ChartFileKindResolver.IsBmsChartFile(file))
            {
                continue;
            }
            ThrowIfInvalidStorageIdentity(file.path, file.hash);
            bmsFileList.Add(file);
        }
        var bmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.Ordinal);
        foreach (LR2SongDBExtended.bmson_song bmsonSong in bmsonSongs ?? [])
        {
            if (bmsonSong != null)
            {
                ThrowIfInvalidStorageIdentity(bmsonSong.path, bmsonSong.md5);
                bmsonSongsByPath[bmsonSong.path] = bmsonSong;
            }
        }
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. bmsonSongsByPath.Values];
        return new ChartStorageTargetSet(
            bmsFileList,
            bmsonSongList,
            ChartFileProjection.FromStorageRows(
                bmsFileList,
                bmsonSongList,
                includeWarningSnapshot: false,
                requirePath: true,
                includeResourceReferences: true,
                includeScoreSnapshot: false));
    }
}
