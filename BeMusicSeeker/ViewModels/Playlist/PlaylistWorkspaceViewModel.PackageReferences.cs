using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>
    /// インストール済みパッケージのチャートを、現在の playlist tree の参照へ接続します。
    /// </summary>
    /// <param name="packages">インストール処理が返したパッケージ一覧です。</param>
    internal void AttachInstalledPackageReferences(IReadOnlyList<ChartPackage> packages)
    {
        List<ChartPackage> packageSnapshot = [.. (packages ?? []).Where(package => package != null)];
        if (packageSnapshot.Count == 0)
        {
            return;
        }

        BMSPlaylist playlistStore = getPlaylistStore();
        if (playlistStore == null)
        {
            return;
        }

        playlistStore.AcquireReaderLockBMSTables();
        try
        {
            List<BMSTable> tableSnapshot = [.. (PlaylistTreeTables ?? Enumerable.Empty<BMSTable>()).Where(table => table != null)];
            if (tableSnapshot.Count == 0)
            {
                return;
            }

            GetPlaylistLibrary().AddReferenceBMSTablesToPackageCharts(tableSnapshot, packageSnapshot);
        }
        finally
        {
            playlistStore.FreeReaderLockBMSTables();
        }

        RequestPlaylistReferenceSortInvalidation();
    }
}
