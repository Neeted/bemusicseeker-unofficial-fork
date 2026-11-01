using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryParentFolderCacheServiceTests
{
    /// <summary>
    /// bmsonの所有集合を置換したとき親フォルダcacheの世代変更を一度通知し、表示更新の起点を欠落させない。
    /// </summary>
    /// <remarks>実ライブラリの所有集合setterと通知を接続し、一覧更新の起点まで確認する。</remarks>
    [TestMethod]
    public void BmsonSongsSetter_RaisesParentFolderCacheVersionChanged()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            int parentFolderCacheVersionChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderCacheVersionChanged++;
                }
            };

            library.BmsonCharts =
            [
                ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken(),
                    Path = Path.Combine(Path.GetTempPath(), "chart.bmson"),
                    Md5 = "0123456789abcdef0123456789abcdef"
                }
            ];

            Assert.AreEqual(1, parentFolderCacheVersionChanged);
        });
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        var databaseScope = new ApplicationDatabaseScope();
        using var caseWorkspace2 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace2.RootDirectory, "BeMusicSeeker_ParentFolderCacheDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
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
