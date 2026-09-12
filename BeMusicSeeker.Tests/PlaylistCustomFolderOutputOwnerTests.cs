using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistCustomFolderOutputOwnerTests
{
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
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
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
