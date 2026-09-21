using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderDbWriterTests
{
    [TestMethod]
    public void ApplySyncPlan_DeletesExactStaleRowsAndUpsertsGeneratedRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderDbWriterTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string rootPath = FolderPath(@"D:\BMS");
            string generatedPath = FolderPath(@"D:\BMS\Pack");
            string driftPath = Normalize(@"D:\BMS\Pack");
            string stalePath = FolderPath(@"D:\BMS\Removed");
            string customPath = FolderPath(@"D:\BMS\Custom");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = rootPath,
                title = "BMS",
                type = 1,
                parent = Lr2SongFolderParentNormalizer.RootParentHash,
                date = 1,
                adddate = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = driftPath,
                title = "Pack",
                type = 1,
                parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\BMS"),
                date = 1,
                adddate = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = customPath,
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(new DateTime(2026, 6, 7, 1, 2, 3, DateTimeKind.Utc)),
                ExistingRows = [.. songDb.Table<LR2SongDB.folder>()],
                GeneratedAtUtc = new DateTime(2026, 6, 7, 2, 3, 4, DateTimeKind.Utc)
            });
            Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
                generation,
                [.. songDb.Table<LR2SongDB.folder>()],
                [@"D:\BMS"]);

            Lr2FolderGenerationWriteResult result = Lr2FolderDbWriter.ApplySyncPlan(songDb, plan);

            Assert.IsTrue(result.HasChanges);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == generatedPath));
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == driftPath));
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == customPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplySyncPlan_ReturnsNoChangesForEmptyPlan()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderDbWriterTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();

            Lr2FolderGenerationWriteResult result = Lr2FolderDbWriter.ApplySyncPlan(
                songDb,
                new Lr2FolderGenerationSyncPlan([], []));

            Assert.IsFalse(result.HasChanges);
            Assert.AreEqual(0, result.UpsertedCount);
            Assert.AreEqual(0, result.DeletedCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static string Normalize(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root.TrimEnd(Path.AltDirectorySeparatorChar);
        }
        return trimmed;
    }

    private static string FolderPath(string path)
    {
        string normalized = Normalize(path);
        return normalized.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || normalized.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalized
                : normalized + Path.DirectorySeparatorChar;
    }
}
