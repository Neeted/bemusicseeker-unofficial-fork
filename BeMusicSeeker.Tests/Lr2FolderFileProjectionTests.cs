using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderFileProjectionTests
{
    [TestMethod]
    public void ParseDefinition_ReadsSupportedLr2FolderDirectives()
    {
        Lr2FolderFileDefinition definition = Lr2FolderFileProjection.ParseDefinition(
        [
            "  #TITLE Main Title",
            "#SUBTITLE Sub Title",
            "#CATEGORY Category",
            "#INFORMATION_A Info A",
            "#INFORMATION_B Info B",
            "#TAG old command",
            "#COMMAND song.hash in ('abc')",
            "#MAXTRACKS 42",
            "#BANNER banner.png",
            "#CUSTOMFOLDER"
        ]);

        Assert.AreEqual("Main Title", definition.Title);
        Assert.AreEqual("Sub Title", definition.Subtitle);
        Assert.AreEqual("Category", definition.Category);
        Assert.AreEqual("Info A", definition.InformationA);
        Assert.AreEqual("Info B", definition.InformationB);
        Assert.AreEqual("song.hash in ('abc')", definition.Command);
        Assert.AreEqual(42, definition.MaxTracks);
        Assert.AreEqual("banner.png", definition.Banner);
        Assert.IsTrue(definition.HasCustomFolderDirective);
    }

    [TestMethod]
    public void ParseDefinition_ReadsBeMusicSeekerGeneratedFolderText()
    {
        Lr2FolderFileDefinition definition = Lr2FolderFileProjection.ParseDefinition(
        [
            "#COMMAND song.hash in (SELECT md5 FROM playlist_entry)",
            "#MAXTRACKS 40",
            "#CATEGORY Playlist",
            "#TITLE MY BEST",
            "#INFORMATION_A ",
            "#INFORMATION_B "
        ]);

        Assert.AreEqual("song.hash in (SELECT md5 FROM playlist_entry)", definition.Command);
        Assert.AreEqual(40, definition.MaxTracks);
        Assert.AreEqual("Playlist", definition.Category);
        Assert.AreEqual("MY BEST", definition.Title);
        Assert.AreEqual(string.Empty, definition.InformationA);
        Assert.AreEqual(string.Empty, definition.InformationB);
    }

    [TestMethod]
    public void ParseDefinition_TreatsGenreAndPlayLevelAsFolderAliases()
    {
        Lr2FolderFileDefinition definition = Lr2FolderFileProjection.ParseDefinition(
        [
            "#GENRE Genre Category",
            "#PLAYLEVEL 40"
        ]);

        Assert.AreEqual("Genre Category", definition.Category);
        Assert.AreEqual(40, definition.MaxTracks);
    }

    [TestMethod]
    public void TryCreateFolderRow_ProjectsDefinitionToCustomFolderRow()
    {
        DateTime timestamp = new(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);
        DateTime generatedAt = timestamp.AddDays(1);
        string filePath = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker\0000.lr2folder");
        var existingRow = new LR2SongDB.folder
        {
            path = filePath,
            adddate = 12345
        };

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = filePath,
            ExistingRow = existingRow,
            LastWriteTimeUtc = timestamp,
            GeneratedAtUtc = generatedAt,
            Definition = Lr2FolderFileProjection.ParseDefinition(
            [
                "#COMMAND song.hash in (SELECT md5 FROM playlist_entry)",
                "#MAXTRACKS 0",
                "#CATEGORY Playlist",
                "#TITLE Folder",
                "#INFORMATION_A Artist",
                "#INFORMATION_B Sub",
                "#BANNER banner.png"
            ])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(filePath, row.path);
        Assert.AreEqual(2, row.type);
        Assert.AreEqual("Folder", row.title);
        Assert.AreEqual("Playlist", row.category);
        Assert.AreEqual("Artist", row.info_a);
        Assert.AreEqual("Sub", row.info_b);
        Assert.AreEqual("song.hash in (SELECT md5 FROM playlist_entry)", row.command);
        Assert.AreEqual("banner.png", row.banner);
        Assert.AreEqual(0, row.max);
        Assert.AreEqual(timestamp.ToUnixtime(), row.date);
        Assert.AreEqual(12345, row.adddate);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(filePath)),
            row.parent);
    }

    [TestMethod]
    public void TryCreateFolderRow_DefaultsMaxAndAddDateForNewRow()
    {
        DateTime timestamp = new(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);
        DateTime generatedAt = timestamp.AddDays(1);

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\#BeMusicSeeker\0001.lr2folder",
            LastWriteTimeUtc = timestamp,
            GeneratedAtUtc = generatedAt,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(0, row.max);
        Assert.AreEqual(generatedAt.ToUnixtime(), row.adddate);
    }

    [TestMethod]
    public void TryCreateFolderRow_AllowsKnownLr2RootRelativeDatabasePath()
    {
        DateTime timestamp = new(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);
        string filePath = Path.GetFullPath(@"D:\LR2beta3\LR2files\CustomFolder\favorite.lr2folder");

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = filePath,
            DatabasePath = @"LR2files\CustomFolder\favorite.lr2folder",
            LastWriteTimeUtc = timestamp,
            ParentHash = Lr2SongFolderParentNormalizer.RootParentHash,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Favorite"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(@"LR2files\CustomFolder\favorite.lr2folder", row.path);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, row.parent);
        Assert.AreEqual(2, row.type);
    }

    [DataTestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(6)]
    public void TryCreateFolderRow_AllowsSupportedCustomFolderTypes(int folderType)
    {
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\#BeMusicSeeker\0000.lr2folder",
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            FolderType = folderType,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(folderType, row.type);
    }

    [TestMethod]
    public void SourceClassifier_UsesRelativePathWithoutFlatteningNestedBuiltinFolders()
    {
        string lr2Root = Path.GetFullPath(@"D:\LR2beta3");
        string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
        string nestedFilePath = Path.Combine(builtinRoot, "RANDOM", "random.lr2folder");

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = nestedFilePath,
            Lr2RootPath = lr2Root,
            BuiltinSourceDirectories = [builtinRoot]
        });

        Assert.AreEqual(@"LR2files\CustomFolder\RANDOM\random.lr2folder", classification.DatabasePath);
        Assert.AreEqual(2, classification.FolderType);
        Assert.IsNull(classification.ParentHash);

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = nestedFilePath,
            DatabasePath = classification.DatabasePath,
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            ParentHash = classification.ParentHash,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\RANDOM"),
            row.parent);
        Assert.AreNotEqual(Lr2SongFolderParentNormalizer.RootParentHash, row.parent);
    }

    [TestMethod]
    public void SourceClassifier_DoesNotTreatLr2FilesRivalAsDefaultBuiltinSource()
    {
        string lr2Root = Path.GetFullPath(@"D:\LR2beta3");
        string filePath = Path.Combine(lr2Root, "LR2files", "Rival", "rival.lr2folder");

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            Lr2RootPath = lr2Root
        });

        Assert.AreEqual(filePath, classification.DatabasePath);
        Assert.AreEqual(2, classification.FolderType);
        Assert.IsNull(classification.ParentHash);

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = filePath,
            DatabasePath = classification.DatabasePath,
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            ParentHash = classification.ParentHash,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Rival"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(filePath, row.path);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(filePath)),
            row.parent);
    }

    [TestMethod]
    public void SourceClassifier_PrefersRootCustomOutputOverOverlappingBuiltinSource()
    {
        string lr2Root = Path.GetFullPath(@"D:\LR2beta3");
        string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
        string rootOutput = Path.Combine(builtinRoot, "BeMusicSeekerRoot");
        string filePath = Path.Combine(rootOutput, "0000.lr2folder");

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            Lr2RootPath = lr2Root,
            RootCustomFolderOutputBaseDir = rootOutput,
            BuiltinSourceDirectories = [builtinRoot]
        });

        Assert.AreEqual(filePath, classification.DatabasePath);
        Assert.AreEqual(2, classification.FolderType);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, classification.ParentHash);
    }

    [TestMethod]
    public void SourceClassifier_DoesNotFlattenNestedRootCustomOutputFolders()
    {
        string rootOutput = Path.GetFullPath(@"D:\BMS\ROOT");
        string tableDirectory = Path.Combine(rootOutput, "Table");
        string filePath = Path.Combine(tableDirectory, "0000.lr2folder");

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            RootCustomFolderOutputBaseDir = rootOutput
        });

        Assert.AreEqual(filePath, classification.DatabasePath);
        Assert.AreEqual(2, classification.FolderType);
        Assert.IsNull(classification.ParentHash);

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = filePath,
            DatabasePath = classification.DatabasePath,
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            ParentHash = classification.ParentHash,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Table Child"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), row.parent);
    }

    [TestMethod]
    public void CreateLr2FolderFileSyncItems_DoesNotPreserveExistingRowWhenParentDiffers()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Lr2FolderFileProjectionTests", Guid.NewGuid().ToString("N"));
        string rootOutput = Path.Combine(tempDirectory, "ROOT");
        string tableDirectory = Path.Combine(rootOutput, "Table");
        Directory.CreateDirectory(tableDirectory);
        string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
        DateTime lastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);
        try
        {
            File.WriteAllText(filePath, "#TITLE Table Child");
            File.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc);
            var request = new Lr2FullGenerationSyncRequest
            {
                Lr2RootCustomFolderOutputBaseDir = rootOutput
            };

            Lr2FullGenerationSyncService.Lr2FolderFileSyncItemsResult result =
                Lr2FullGenerationSyncService.CreateLr2FolderFileSyncItems(
                    [filePath],
                    request,
                    existingRowResolver: path => new LR2SongDB.folder
                    {
                        path = path,
                        type = 2,
                        parent = Lr2SongFolderParentNormalizer.RootParentHash,
                        date = lastWriteTimeUtc.ToUnixtime()
                    });

            Lr2FolderFileSyncItem item = result.Items.Single();
            Assert.IsFalse(item.PreserveExistingRowOnly);
            Assert.IsNotNull(item.Definition);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [DataTestMethod]
    [DataRow(@"LR2files\CustomFolder\favorite.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\TOP10.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\PLAYLEVEL\1.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\CLEAR\clear.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\RANK\rank.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\ignore.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\INSANE01\01.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\INSANE02\02.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\newsong.lr2folder", 3)]
    [DataRow(@"LR2files\CustomFolder\course1.lr2folder", 6)]
    [DataRow(@"LR2files\CustomFolder\course2.lr2folder", 6)]
    [DataRow(@"LR2files\CustomFolder\course3.lr2folder", 6)]
    public void SourceClassifier_InfersOpenLr2SpecialFolderTypeFromKnownSourcePath(string relativePath, int expectedFolderType)
    {
        string lr2Root = Path.GetFullPath(@"D:\LR2beta3");
        string filePath = Path.Combine(lr2Root, relativePath);

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            Lr2RootPath = lr2Root
        });

        Assert.AreEqual(relativePath, classification.DatabasePath);
        Assert.AreEqual(expectedFolderType, classification.FolderType);
    }

    [TestMethod]
    public void TryCreateFolderRow_SkipsCp932UnsupportedPath()
    {
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\emoji_😀\0000.lr2folder",
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsFalse(created);
        Assert.IsNull(row);
    }

    [TestMethod]
    public void TryCreateFolderRow_SkipsReservedNormalDirectoryType()
    {
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\#BeMusicSeeker\0000.lr2folder",
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            FolderType = 1,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsFalse(created);
        Assert.IsNull(row);
    }

    [TestMethod]
    public void TryCreateFolderRow_SkipsUnknownCustomFolderType()
    {
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\#BeMusicSeeker\0000.lr2folder",
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            FolderType = 999,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsFalse(created);
        Assert.IsNull(row);
    }

    [TestMethod]
    public void NormalDirectorySync_DoesNotPruneCustomFolderRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileProjectionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string customFolderPath = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker\0000.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = customFolderPath,
                type = 2,
                title = "Custom"
            }, typeof(LR2SongDB.folder));

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
                AllowPrune = true
            });

            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == customFolderPath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path.EndsWith(@"Pack\", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
