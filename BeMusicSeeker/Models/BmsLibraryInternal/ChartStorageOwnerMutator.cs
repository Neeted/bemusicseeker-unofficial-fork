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

    internal static bool ApplySnapshotDigest(
        ChartFile chart,
        ChartFileSnapshot snapshot,
        ICollection<LibraryChartDigestChange> digestChanges = null)
    {
        if (chart == null || snapshot == null)
        {
            return false;
        }

        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            string oldMd5 = bmsFile.hash;
            string oldSha256 = bmsFile.sha256;
            bmsFile.ApplySnapshotDigest(snapshot.Md5, snapshot.Sha256);
            AddDigestChangeIfChanged(digestChanges, LibraryChartDigestChange.FromBms(bmsFile, oldMd5, oldSha256));
            return true;
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong == null)
        {
            return false;
        }

        string oldBmsonMd5 = bmsonSong.md5;
        string oldBmsonSha256 = bmsonSong.sha256;
        bmsonSong.md5 = snapshot.Md5;
        bmsonSong.sha256 = snapshot.Sha256;
        bmsonSong.updated_at = snapshot.LastWriteTimeUtc;
        AddDigestChangeIfChanged(digestChanges, LibraryChartDigestChange.FromBmson(bmsonSong, oldBmsonMd5, oldBmsonSha256));
        return true;
    }

    private static void AddDigestChangeIfChanged(ICollection<LibraryChartDigestChange> digestChanges, LibraryChartDigestChange digestChange)
    {
        if (digestChange?.HasDigestChange == true)
        {
            digestChanges?.Add(digestChange);
        }
    }
}
