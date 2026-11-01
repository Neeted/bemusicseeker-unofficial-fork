using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>設定とプレイリストの実媒体・workspace接続で共用する既存fixtureを保持します。</summary>
internal static class PlaylistTestFixture
{

    /// <summary>
    /// 指定した変更不能な物理出力snapshotを使い、同じDB所有者の同期境界を作ります。
    /// </summary>
    /// <param name="databaseScope">接続と書込み排他を共有するfixtureのDB所有者。</param>
    internal static TestLr2PlaylistFolderSynchronizationPort CreateDeterministicLr2PlaylistFolderSynchronizationPort(
        ApplicationDatabaseScope databaseScope,
        string songDbPath,
        CustomFolderOutputPhysicalSurface physicalSurface)
    {
        ArgumentNullException.ThrowIfNull(physicalSurface);
        return new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath)
        {
            PhysicalSurfaceFactory = () => physicalSurface
        };
    }

    /// <summary>同じfixtureのDB所有者で、songとfolderの実schemaを準備します。</summary>
    /// <param name="databaseScope">準備中の接続を所有するfixtureのDB所有者。</param>
    internal static string CreateTempSongDbPath(ApplicationDatabaseScope databaseScope, string tempDirectory)
    {
        string tempSongDbPath = Path.Combine(tempDirectory, "song.db");
        using (LR2SongDBExtended db = databaseScope.OpenSongWrite(tempSongDbPath))
        {
            db.CreateTable<LR2SongDB.song>();
            db.CreateTable<LR2SongDB.folder>();
        }
        return tempSongDbPath;
    }

    internal static BMSTableEntry CreateEntry(string md5, string folder)
    {
        return new BMSTableEntry(JObject.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":\"" + folder + "\"}"));
    }
}
