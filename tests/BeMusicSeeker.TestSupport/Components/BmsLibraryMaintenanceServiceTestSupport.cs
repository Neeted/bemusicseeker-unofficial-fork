using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class BmsLibraryMaintenanceServiceTestSupport
{
    /// <summary>指定された識別・配置・資源条件から、各ケースが観測する譜面値を作ります。</summary>
    internal static ChartFile CreateFile(string hash)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = hash };
        return file;
    }

    /// <summary>呼出元のDB所有者で専用DBを準備し、同期callbackの終了後にケース領域を回収します。</summary>
    internal static void WithTemporarySongDb(ApplicationDatabaseScope databaseScope, Action<string> testAction)
    {
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace1.RootDirectory, "BeMusicSeeker_MaintenanceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            });
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

}
