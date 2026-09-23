using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ChartFile が指す storage owner へ、chart 共通の runtime / hydrated state を反映します。
/// BMS / bmson の storage row 分岐を各 service へ広げないための境界です。
/// </summary>
internal static class ChartStorageOwnerMutator
{
    internal static bool HasSingleStorageOwner(ChartFile chart)
    {
        return (chart?.GetBmsStorageOwner() == null) != (chart?.GetBmsonStorageOwner() == null);
    }

    internal static bool HasMissingBmsSha256(ChartFile chart)
    {
        BMSFile file = chart?.GetBmsStorageOwner();
        return file != null && string.IsNullOrWhiteSpace(file.sha256);
    }

    internal static int ApplyMissingBmsSha256(
        ChartFile chart,
        string sha256,
        ICollection<BMSFile> completedDigestFiles,
        ICollection<LibraryChartDigestChange> digestChanges = null)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return 0;
        }

        BMSFile file = chart?.GetBmsStorageOwner();
        if (file == null || !string.IsNullOrWhiteSpace(file.sha256))
        {
            return 0;
        }

        string oldMd5 = file.hash;
        string oldSha256 = file.sha256;
        file.ApplySha256(sha256);
        AddDigestChangeIfChanged(digestChanges, LibraryChartDigestChange.FromBms(file, oldMd5, oldSha256));
        completedDigestFiles?.Add(file);
        return 1;
    }

    /// <summary>
    /// Creates a detached BMS row containing snapshot digest and chart-info generated columns.
    /// </summary>
    internal static BMSFile CreateBmsPersistenceCopy(
        ChartFile chart,
        string md5,
        string sha256,
        LR2SongDBExtended.chart_info row)
    {
        BMSFile owner = chart?.GetBmsStorageOwner();
        if (owner == null)
        {
            return null;
        }

        BMSFile copy = owner.CreateSongRowPersistenceCopy();
        copy.ApplySnapshotDigest(md5, sha256);
        copy.ApplyLr2ChartInfoColumns(row);
        return copy;
    }

    /// <summary>
    /// Creates an update-only projection for the existing LR2 song row owned by this chart.
    /// </summary>
    internal static Lr2ChartInfoSongProjection CreateBmsChartInfoSongProjection(
        ChartFile chart,
        LR2SongDBExtended.chart_info row)
    {
        BMSFile owner = chart?.GetBmsStorageOwner();
        return owner == null
            ? null
            : Lr2ChartInfoSongProjection.Create(owner.path, owner.hash, row);
    }

    /// <summary>
    /// Applies chart-info-derived columns after the database receipt confirms that the owned song row matched.
    /// </summary>
    internal static int ApplyCommittedBmsChartInfoProjection(
        ChartFile chart,
        LR2SongDBExtended.chart_info row,
        IReadOnlySet<Lr2ChartInfoSongProjectionIdentity> matchedIdentities)
    {
        BMSFile owner = chart?.GetBmsStorageOwner();
        Lr2ChartInfoSongProjection projection = CreateBmsChartInfoSongProjection(chart, row);
        if (owner == null
            || projection == null
            || matchedIdentities == null
            || !matchedIdentities.Contains(projection.Identity))
        {
            return 0;
        }
        return projection.ApplyTo(owner) ? 1 : 0;
    }

    /// <summary>
    /// Creates a detached BMSON row containing the snapshot digest.
    /// </summary>
    internal static LR2SongDBExtended.bmson_song CreateBmsonPersistenceCopy(
        ChartFile chart,
        string md5,
        string sha256,
        System.DateTime lastWriteTimeUtc)
    {
        LR2SongDBExtended.bmson_song owner = chart?.GetBmsonStorageOwner();
        if (owner == null)
        {
            return null;
        }

        LR2SongDBExtended.bmson_song copy = CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(owner);
        copy.md5 = md5;
        copy.sha256 = sha256;
        copy.updated_at = lastWriteTimeUtc;
        return copy;
    }

    /// <summary>
    /// Applies a durable snapshot and its optional chart-info row to the canonical storage owner.
    /// </summary>
    internal static int ApplyCommittedSnapshot(
        ChartFile chart,
        string md5,
        string sha256,
        System.DateTime lastWriteTimeUtc,
        LR2SongDBExtended.chart_info row,
        ICollection<LibraryChartDigestChange> digestChanges = null)
    {
        if (chart == null)
        {
            return 0;
        }

        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            string oldMd5 = bmsFile.hash;
            string oldSha256 = bmsFile.sha256;
            bmsFile.ApplySnapshotDigest(md5, sha256);
            bmsFile.ApplyLr2ChartInfoColumns(row);
            AddDigestChangeIfChanged(digestChanges, LibraryChartDigestChange.FromBms(bmsFile, oldMd5, oldSha256));
            return row == null ? 0 : 1;
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong == null)
        {
            return 0;
        }

        string oldBmsonMd5 = bmsonSong.md5;
        string oldBmsonSha256 = bmsonSong.sha256;
        bmsonSong.md5 = md5;
        bmsonSong.sha256 = sha256;
        bmsonSong.updated_at = lastWriteTimeUtc;
        AddDigestChangeIfChanged(digestChanges, LibraryChartDigestChange.FromBmson(bmsonSong, oldBmsonMd5, oldBmsonSha256));
        return 0;
    }

    private static void AddDigestChangeIfChanged(ICollection<LibraryChartDigestChange> digestChanges, LibraryChartDigestChange digestChange)
    {
        if (digestChange?.HasDigestChange == true)
        {
            digestChanges?.Add(digestChange);
        }
    }
}
