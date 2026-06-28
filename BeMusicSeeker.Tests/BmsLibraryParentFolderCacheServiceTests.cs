using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryParentFolderCacheServiceTests
{
    [TestMethod]
    public void BuildParentFolderCandidates_BmsonChartKeepsRootEvenWhenLr2FolderExists()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ParentFolderCache_" + Guid.NewGuid().ToString("N"));
        string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
        string chartDirectoryPath = Path.Combine(libraryRootPath, "Package");
        string chartPath = Path.Combine(chartDirectoryPath, "chart.bmson");
        string customFolderPath = Path.Combine(tempRootPath, "CustomFolder");
        Directory.CreateDirectory(chartDirectoryPath);
        Directory.CreateDirectory(customFolderPath);
        File.WriteAllText(Path.Combine(chartDirectoryPath, "table.lr2folder"), string.Empty);
        try
        {
            var service = new BmsLibraryParentFolderCacheService();
            BmsLibraryOptionsSnapshot options = CreateLr2Options(customFolderPath);
            List<string> emptyResult = service.BuildParentFolderCandidates([libraryRootPath], [], options);
            List<string> bmsonResult = service.BuildParentFolderCandidates([libraryRootPath], [chartPath], options);

            CollectionAssert.DoesNotContain(emptyResult, libraryRootPath);
            CollectionAssert.Contains(bmsonResult, libraryRootPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildParentFolderCandidates_ExcludesCustomFolderOutputBasesEvenWhenChartsExist()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ParentFolderCache_" + Guid.NewGuid().ToString("N"));
        try
        {
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string additionalOutputBase = Path.Combine(tempRootPath, "AdditionalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(libraryRootPath);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            var service = new BmsLibraryParentFolderCacheService();
            BmsLibraryOptionsSnapshot options = CreateLr2Options(
                normalOutputBase + Path.DirectorySeparatorChar,
                [additionalOutputBase + Path.DirectorySeparatorChar],
                rootOutputBase + Path.DirectorySeparatorChar);
            List<string> result = service.BuildParentFolderCandidates(
                [libraryRootPath, normalOutputBase, additionalOutputBase, rootOutputChild],
                [
                    Path.Combine(libraryRootPath, "chart.bms"),
                    Path.Combine(normalOutputBase, "chart.bms"),
                    Path.Combine(additionalOutputBase, "chart.bms"),
                    Path.Combine(rootOutputChild, "chart.bms")
                ],
                options);

            CollectionAssert.Contains(result, libraryRootPath);
            CollectionAssert.DoesNotContain(result, normalOutputBase);
            CollectionAssert.DoesNotContain(result, additionalOutputBase);
            CollectionAssert.DoesNotContain(result, rootOutputChild);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BmsonSongsSetter_RaisesParentFolderCacheVersionChanged()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath);
            int parentFolderCacheVersionChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderCacheVersionChanged++;
                }
            };

            library.BmsonSongs =
            [
                new LR2SongDBExtended.bmson_song
                {
                    path = Path.Combine(Path.GetTempPath(), "chart.bmson"),
                    md5 = "0123456789abcdef0123456789abcdef"
                }
            ];

            Assert.AreEqual(1, parentFolderCacheVersionChanged);
        });
    }

    private static BmsLibraryOptionsSnapshot CreateLr2Options(string customFolderPath)
    {
        return CreateLr2Options(customFolderPath, [], Path.Combine(customFolderPath, "RootType"));
    }

    private static BmsLibraryOptionsSnapshot CreateLr2Options(
        string customFolderPath,
        IReadOnlyList<string> additionalOutputBaseDirectories,
        string rootOutputBaseDirectory)
    {
        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2CustomFolderOutputBaseDir = customFolderPath,
            LR2CustomFolderAdditionalOutputBaseDirs = additionalOutputBaseDirectories,
            LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDirectory
        };
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ParentFolderCacheDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
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
