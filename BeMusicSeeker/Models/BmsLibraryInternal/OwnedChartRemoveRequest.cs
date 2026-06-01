using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum OwnedChartRemoveMode
{
    OwnerReference,
    PathCleanup
}

internal sealed class OwnedChartRemoveRequest
{
    private OwnedChartRemoveRequest(
        OwnedChartRemoveMode mode,
        ChartFileKind kind,
        BMSFile bmsOwner,
        LR2SongDBExtended.bmson_song bmsonOwner,
        string path)
    {
        Mode = mode;
        Kind = kind;
        BmsOwner = bmsOwner;
        BmsonOwner = bmsonOwner;
        Path = path;
    }

    internal OwnedChartRemoveMode Mode { get; }

    internal ChartFileKind Kind { get; }

    internal BMSFile BmsOwner { get; }

    internal LR2SongDBExtended.bmson_song BmsonOwner { get; }

    internal string Path { get; }

    internal static OwnedChartRemoveRequest FromOwnerReference(BMSFile file)
    {
        return file == null
            ? null
            : new OwnedChartRemoveRequest(OwnedChartRemoveMode.OwnerReference, ChartFileKind.Bms, file, null, file.path);
    }

    internal static OwnedChartRemoveRequest FromOwnerReference(LR2SongDBExtended.bmson_song song)
    {
        return song == null
            ? null
            : new OwnedChartRemoveRequest(OwnedChartRemoveMode.OwnerReference, ChartFileKind.Bmson, null, song, song.path);
    }

    internal static OwnedChartRemoveRequest FromOwnerReferenceChart(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return FromOwnerReference(bmsOwner);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        return bmsonOwner == null ? null : FromOwnerReference(bmsonOwner);
    }

    internal static OwnedChartRemoveRequest FromPathCleanup(ChartFileKind kind, string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new OwnedChartRemoveRequest(OwnedChartRemoveMode.PathCleanup, kind, null, null, path);
    }

    internal ChartFile CreateChartSnapshot()
    {
        if (BmsOwner != null)
        {
            return ChartFileProjection.FromBmsStorageOwnerIdentity(BmsOwner);
        }
        if (BmsonOwner != null)
        {
            return ChartFileProjection.FromBmsonStorageOwnerIdentity(BmsonOwner);
        }
        return string.IsNullOrWhiteSpace(Path)
            ? null
            : new ChartFile(
                Kind,
                Path,
                null,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                System.IO.Path.GetDirectoryName(Path),
                string.Empty,
                string.Empty,
                null,
                0,
                null,
                null,
                null);
    }
}
