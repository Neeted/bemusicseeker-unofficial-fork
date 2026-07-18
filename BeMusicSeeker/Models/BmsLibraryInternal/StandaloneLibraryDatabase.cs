using System;
using System.IO;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class StandaloneLibraryDatabase
{
    public static StandaloneLibraryDatabaseEnsureResult EnsurePortableSongDb()
    {
        Directory.CreateDirectory(PortableSettingsPath.DataDirectoryPath);
        string songDbPath = PortableSettingsPath.StandaloneSongDbPath;
        bool created = !File.Exists(songDbPath);
        using (File.Open(songDbPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
        }

        using var songDb = new LR2SongDBExtended(songDbPath);
        EnsureLibrarySchema(songDb);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        bool hasLibraryCharts =
            songDb.Table<LR2SongDB.song>().Count() > 0
            || songDb.Table<LR2SongDBExtended.bmson_song>().Count() > 0;
        return new StandaloneLibraryDatabaseEnsureResult(songDbPath, created, hasLibraryCharts);
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
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.CreateTable<LR2SongDBExtended.ir_score>();
        BmsLibraryDbGateway.EnsureIrDataSchema(songDb);
    }
}

internal sealed class StandaloneLibraryDatabaseEnsureResult(string songDbPath, bool created, bool hasLibraryCharts)
{
    public string SongDbPath { get; } = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));

    public bool Created { get; } = created;

    public bool HasLibraryCharts { get; } = hasLibraryCharts;

    public bool RequiresInitialLibraryBuild => Created || !HasLibraryCharts;

    public string InitialLibraryBuildReason
    {
        get
        {
            if (Created)
            {
                return "new_standalone_song_db";
            }
            if (!HasLibraryCharts)
            {
                return "empty_standalone_song_db";
            }
            return null;
        }
    }
}
