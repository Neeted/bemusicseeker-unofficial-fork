using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistCustomFolderOutputOwnerTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Materialization_Lr2StageCountsActualFilesIncludingUnchangedAndIgnoresDedicatedObserverFailure(bool throwObserver)
    {
        string root = Path.Combine(Path.GetTempPath(), "bmseeker-custom-folder-progress-" + Guid.NewGuid().ToString("N"));
        var settings = new CustomFolderOutputSettingsSnapshot
        {
            LR2RootPath = root,
            LR2CustomFolderOutputBaseDir = root,
            LR2CustomFolderOutputBaseDirRootType = root,
            OperationModeLR2DB = true
        };
        var owner = new PlaylistCustomFolderOutputOwner(
            () => settings,
            (table, _) => Enumerable.Range(0, table.playlist_id == 1 ? 2 : 1)
                .Select(index => new PlaylistCustomFolderOutputOwner.CustomFolderDefinition
                {
                    RelativeDirectory = string.Empty,
                    Text = "#TITLE Fixed " + table.playlist_id + "-" + index + "\r\n"
                }).ToArray(),
            (table, _) => Path.Combine(root, table.Output_dir),
            (_, _) => CustomFolderOutputPhysicalSurface.Empty,
            (projections, _) => projections.Select(projection => projection.OutputDirectory).ToArray(),
            (directory, _, _) => [directory],
            _ => { });
        var events = new List<(string Stage, int Processed, int Total)>();
        try
        {
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection first = owner.CreateProjection(new BMSTable
            {
                playlist_id = 1,
                name = "First",
                Output_dir = "First"
            });
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection second = owner.CreateProjection(new BMSTable
            {
                playlist_id = 2,
                name = "Second",
                Output_dir = "Second"
            });
            Directory.CreateDirectory(first.OutputDirectory);
            string unchangedPath = first.Files[0].FilePath;
            string unchangedText = "#TITLE Fixed 1-0\r\n";
            File.WriteAllText(unchangedPath, unchangedText, Encoding.GetEncoding("shift_jis"));
            DateTime unchangedAt = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(unchangedPath, unchangedAt);
            string obsoletePath = Path.Combine(first.OutputDirectory, "0009.lr2folder");
            File.WriteAllText(obsoletePath, "#TITLE Obsolete", Encoding.ASCII);
            string emptyChild = Path.Combine(first.OutputDirectory, "EmptyChild");
            Directory.CreateDirectory(emptyChild);
            first.PhysicalSurface = CustomFolderOutputPhysicalSurface.FromEntries(
                [new RootFileEnumerationEntry(unchangedPath, unchangedAt, new FileInfo(unchangedPath).Length)], true);
            second.PhysicalSurface = CustomFolderOutputPhysicalSurface.Empty;
            PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult result = owner.MaterializeBatch(
                [first, second],
                stageProgressReporter: (stage, processed, total) =>
                {
                    events.Add((stage, processed, total));
                    if (throwObserver)
                    {
                        throw new InvalidOperationException("dedicated observer failure");
                    }
                });

            Assert.AreEqual(2, result.WrittenFileCount);
            Assert.AreEqual(1, result.UnchangedFileCount);
            Assert.AreEqual(1, result.DeletedFileCount);
            Assert.IsFalse(File.Exists(obsoletePath));
            Assert.IsFalse(Directory.Exists(emptyChild));
            CollectionAssert.Contains(result.EmptyOutputDirectories, emptyChild);
            Assert.IsFalse(result.HasUnverifiedFiles);
            Assert.AreEqual(3, result.SyncItems.Count);
            Assert.AreEqual(unchangedAt, File.GetLastWriteTimeUtc(unchangedPath));
            Assert.AreEqual(unchangedText, File.ReadAllText(unchangedPath, Encoding.GetEncoding("shift_jis")));
            CollectionAssert.AreEquivalent(new[] { unchangedPath, Path.Combine(root, "First", "0001.lr2folder"), Path.Combine(root, "Second", "0000.lr2folder") },
                result.SyncItems.Select(item => item.FilePath).ToArray());
            Assert.IsTrue(result.SyncItems.All(item => File.Exists(item.FilePath) && item.LastWriteTimeUtc != null));
            Assert.IsTrue(result.SyncItems.All(item => item.DatabasePath == item.FilePath && item.FolderType == 2));
            Assert.AreEqual("Fixed 1-1", result.SyncItems.Single(item => item.FilePath == first.Files[1].FilePath).Definition.Title);
            (string Stage, int Processed, int Total)[] counts = events.Where(value => value.Stage == "playlist_files").ToArray();
            Assert.IsTrue(counts.Length > 0);
            Assert.IsTrue(counts.All(value => value.Total == 3 && value.Processed >= 0 && value.Processed <= 3));
            Assert.AreEqual(3, counts.Last().Processed);
            Assert.IsTrue(events.Any(value => value.Stage == "playlist_output_discovery" && value.Total == 0));
            int cleanupIndex = events.FindIndex(value => value.Stage == "playlist_output_cleanup");
            Assert.IsTrue(cleanupIndex >= 0);
            Assert.IsTrue(events.Where(value => value.Stage == "playlist_output_cleanup")
                .All(value => value.Processed == 0 && value.Total == 0));
            Assert.IsTrue(events.Skip(cleanupIndex + 1).Any(value => value.Stage == "playlist_files"
                && value.Total == 3 && value.Processed == 2), "次の表の出力は同じ実ファイル総数の有限段階へ戻る。");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Materialization_EmptyFileProjectionOmitsCountedStage()
    {
        string root = Path.Combine(Path.GetTempPath(), "bmseeker-custom-folder-empty-" + Guid.NewGuid().ToString("N"));
        var settings = new CustomFolderOutputSettingsSnapshot
        {
            LR2RootPath = root,
            LR2CustomFolderOutputBaseDir = root,
            LR2CustomFolderOutputBaseDirRootType = root,
            OperationModeLR2DB = true
        };
        var owner = new PlaylistCustomFolderOutputOwner(
            () => settings,
            (_, _) => [],
            (_, _) => root,
            (_, _) => CustomFolderOutputPhysicalSurface.Empty,
            (_, _) => [root],
            (_, _, _) => [root],
            _ => { });
        var events = new List<(string Stage, int Processed, int Total)>();
        try
        {
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = owner.CreateProjection(
                new BMSTable { playlist_id = 3, name = "Empty", Output_dir = root });
            PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult result = owner.MaterializeBatch(
                [projection], stageProgressReporter: (stage, processed, total) => events.Add((stage, processed, total)));

            Assert.AreEqual(0, result.SyncItems.Count);
            Assert.AreEqual(0, result.WrittenFileCount);
            Assert.IsFalse(events.Any(value => value.Stage == "playlist_files"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ProjectionAndMaterialization_CreateExpectedLr2FolderFileAndSyncItem()
    {
        string root = Path.Combine(Path.GetTempPath(), "bmseeker-custom-folder-owner-" + Guid.NewGuid().ToString("N"));
        var settings = new CustomFolderOutputSettingsSnapshot
        {
            LR2RootPath = root,
            LR2CustomFolderOutputBaseDir = root,
            LR2CustomFolderOutputBaseDirRootType = root,
            OperationModeLR2DB = true
        };
        var table = new BMSTable
        {
            playlist_id = 42,
            name = "Owner test",
            Output_dir = "owner-output"
        };
        var owner = new PlaylistCustomFolderOutputOwner(
            () => settings,
            (currentTable, currentSettings) =>
            [
                new PlaylistCustomFolderOutputOwner.CustomFolderDefinition
                {
                    RelativeDirectory = "nested",
                    Text = "#COMMAND song.hash = 'abc'\r\n#TITLE Owner test\r\n",
                    IsRandomVariant = false
                }
            ],
            (currentTable, currentSettings) => Path.Combine(currentSettings.LR2CustomFolderOutputBaseDir, currentTable.Output_dir),
            (directories, reason) => CustomFolderOutputPhysicalSurface.Empty,
            (projections, currentSettings) => projections.Select(projection => projection.OutputDirectory).ToArray(),
            (directory, currentTable, currentSettings) => [directory],
            _ => { });

        try
        {
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = owner.CreateProjection(table);
            Assert.AreEqual(Path.Combine(root, "owner-output"), projection.OutputDirectory);
            Assert.AreEqual(1, projection.Files.Count);
            Assert.AreEqual("nested", projection.Files[0].RelativeDirectory);

            PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult result = owner.MaterializeBatch([projection]);
            Assert.AreEqual(1, result.WrittenFileCount);
            Assert.AreEqual(1, result.SyncItems.Count);
            Assert.IsTrue(File.Exists(projection.Files[0].FilePath));
            StringAssert.Contains(File.ReadAllText(projection.Files[0].FilePath), "#TITLE Owner test");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Materialization_RepairsStaleFileAndDoesNotRewriteExplicitlyCapturedCurrentFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "bmseeker-custom-folder-owner-" + Guid.NewGuid().ToString("N"));
        var settings = new CustomFolderOutputSettingsSnapshot
        {
            LR2RootPath = root,
            LR2CustomFolderOutputBaseDir = root,
            LR2CustomFolderOutputBaseDirRootType = root,
            OperationModeLR2DB = true
        };
        var table = new BMSTable
        {
            playlist_id = 43,
            name = "Owner idempotence test",
            Output_dir = "owner-output"
        };
        var owner = new PlaylistCustomFolderOutputOwner(
            () => settings,
            (currentTable, currentSettings) =>
            [
                new PlaylistCustomFolderOutputOwner.CustomFolderDefinition
                {
                    RelativeDirectory = string.Empty,
                    Text = "#COMMAND song.hash = 'def'\r\n#TITLE Owner idempotence test\r\n",
                    IsRandomVariant = false
                }
            ],
            (currentTable, currentSettings) => Path.Combine(currentSettings.LR2CustomFolderOutputBaseDir, currentTable.Output_dir),
            (directories, reason) => CustomFolderOutputPhysicalSurface.Empty,
            (projections, currentSettings) => projections.Select(projection => projection.OutputDirectory).ToArray(),
            (directory, currentTable, currentSettings) => [directory],
            _ => { });

        try
        {
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = owner.CreateProjection(table);
            string filePath = projection.Files[0].FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "stale content", System.Text.Encoding.GetEncoding("shift_jis"));
            PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult first = owner.MaterializeBatch([projection]);
            DateTime expectedTimestamp = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, expectedTimestamp);
            projection.PhysicalSurface = CustomFolderOutputPhysicalSurface.FromEntries(
                [new RootFileEnumerationEntry(filePath, expectedTimestamp, new FileInfo(filePath).Length)],
                discoveryComplete: true);

            PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult second = owner.MaterializeBatch([projection]);

            Assert.AreEqual(1, first.WrittenFileCount);
            Assert.AreEqual(0, second.WrittenFileCount);
            Assert.AreEqual(1, second.UnchangedFileCount);
            Assert.AreEqual(expectedTimestamp, File.GetLastWriteTimeUtc(filePath));
            CollectionAssert.AreEqual(
                System.Text.Encoding.GetEncoding("shift_jis").GetBytes("#COMMAND song.hash = 'def'\r\n#TITLE Owner idempotence test\r\n"),
                File.ReadAllBytes(filePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Materialization_LeavesLockedOmittedFileUnverified()
    {
        string root = Path.Combine(Path.GetTempPath(), "bmseeker-custom-folder-owner-" + Guid.NewGuid().ToString("N"));
        var settings = new CustomFolderOutputSettingsSnapshot
        {
            LR2RootPath = root,
            LR2CustomFolderOutputBaseDir = root,
            LR2CustomFolderOutputBaseDirRootType = root,
            OperationModeLR2DB = true
        };
        var table = new BMSTable
        {
            playlist_id = 44,
            name = "Owner locked file test",
            Output_dir = "owner-locked-output"
        };
        var owner = new PlaylistCustomFolderOutputOwner(
            () => settings,
            (currentTable, currentSettings) =>
            [
                new PlaylistCustomFolderOutputOwner.CustomFolderDefinition
                {
                    RelativeDirectory = string.Empty,
                    Text = "#COMMAND song.hash = 'ghi'\r\n#TITLE Owner locked file test\r\n",
                    IsRandomVariant = false
                }
            ],
            (currentTable, currentSettings) => Path.Combine(currentSettings.LR2CustomFolderOutputBaseDir, currentTable.Output_dir),
            (directories, reason) => CustomFolderOutputPhysicalSurface.Empty,
            (projections, currentSettings) => projections.Select(projection => projection.OutputDirectory).ToArray(),
            (directory, currentTable, currentSettings) => [directory],
            _ => { });

        try
        {
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = owner.CreateProjection(table);
            string filePath = projection.Files[0].FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, projection.Files[0].Text, System.Text.Encoding.GetEncoding("shift_jis"));
            projection.PhysicalSurface = CustomFolderOutputPhysicalSurface.FromEntries([], discoveryComplete: true);
            byte[] originalBytes = File.ReadAllBytes(filePath);

            using (var locked = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult result = owner.MaterializeBatch([projection]);

                Assert.IsTrue(result.HasUnverifiedFiles);
                CollectionAssert.Contains(result.UnverifiedFilePaths, filePath);
                Assert.AreEqual(0, result.WrittenFileCount);
                Assert.AreEqual(0, result.UnchangedFileCount);
                Assert.AreEqual(0, result.SyncItems.Count);
            }

            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(filePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
