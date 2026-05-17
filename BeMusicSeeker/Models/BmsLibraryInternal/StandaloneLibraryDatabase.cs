using System;
using System.IO;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class StandaloneLibraryDatabase
{
    public static string EnsurePortableSongDb()
    {
        Directory.CreateDirectory(PortableSettingsPath.DataDirectoryPath);
        string songDbPath = PortableSettingsPath.StandaloneSongDbPath;
        using (File.Open(songDbPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
        }

        using var songDb = new LR2SongDBExtended(songDbPath);
        EnsureLibrarySchema(songDb);
        BMSPlaylist.EnsureSchema(songDbPath);
        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        return songDbPath;
    }

    private static void EnsureLibrarySchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.CreateTable<LR2SongDBExtended.install>();
        songDb.CreateTable<LR2SongDBExtended.maintenance>();
        songDb.CreateTable<LR2SongDBExtended.ir_score>();
        BmsLibraryDbGateway.EnsureIrDataSchema(songDb);
    }
}
