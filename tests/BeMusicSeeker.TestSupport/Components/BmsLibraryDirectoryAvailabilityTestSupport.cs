using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class BmsLibraryDirectoryAvailabilityTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static void SeedCatalogSentinels(
        LR2SongDBExtended setup,
        string rootDirectoryB,
        string chartPathA,
        string chartPathB,
        string hashA,
        string hashB)
    {
        setup.CreateTable<LR2SongDB.song>();
        setup.CreateTable<LR2SongDBExtended.bmson_song>();
        setup.CreateTable<LR2SongDB.folder>();
        setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(((ChartTestValues.Empty() with { Path = chartPathA, AddDate = 1, Tag = "keep-a" })) with { Md5 = hashA, Favorite = 1 }), typeof(LR2SongDB.song));
        setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(((ChartTestValues.Empty() with { Path = chartPathB, AddDate = 2, Tag = "keep-b" })) with { Md5 = hashB, Favorite = 2 }), typeof(LR2SongDB.song));
        setup.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = chartPathB,
            Folder = Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
            RawTitle = "keep-bmson",
            Md5 = hashB
        }), typeof(LR2SongDBExtended.bmson_song));
        setup.InsertOrReplace(
            new LR2SongDB.folder
            {
                path = Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                title = "keep-b-folder",
                type = 1,
                adddate = 2
            },
            typeof(LR2SongDB.folder));
    }

}
