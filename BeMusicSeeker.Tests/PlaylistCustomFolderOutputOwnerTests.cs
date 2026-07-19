using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
}
