using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomFolderOutputBaseRegistryTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void GetCustomFolderOutputDirectory_UsesPlaylistAdditionalOutputBase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempDirectory, "Default");
            string rootOutputBase = Path.Combine(tempDirectory, "Root");
            string dpOutputBase = Path.Combine(tempDirectory, "DP");
            string serializedAdditionalOutputBases = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([dpOutputBase]);

            var table = new BMSTable
            {
                name = "Table",
                Output_dir = "Table",
                custom_folder_output_base_name = "DP"
            };

            Assert.AreEqual(
                Path.Combine(dpOutputBase, "Table"),
                BMSPlaylist.GetCustomFolderOutputDirectory(
                    table,
                    normalOutputBase,
                    rootOutputBase,
                    serializedAdditionalOutputBases));
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
    [TestCategory("Playlist")]
    public void GetCustomFolderOutputDirectory_FallsBackToDefaultForUnknownAdditionalOutputBase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempDirectory, "Default");
            string rootOutputBase = Path.Combine(tempDirectory, "Root");
            string serializedAdditionalOutputBases = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(tempDirectory, "DP")]);

            var table = new BMSTable
            {
                name = "Table",
                Output_dir = "Table",
                custom_folder_output_base_name = "Missing"
            };

            Assert.AreEqual(
                Path.Combine(normalOutputBase, "Table"),
                BMSPlaylist.GetCustomFolderOutputDirectory(
                    table,
                    normalOutputBase,
                    rootOutputBase,
                    serializedAdditionalOutputBases));
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
    [TestCategory("Playlist")]
    public void GetCustomFolderOutputDirectory_RootOutputBaseOverridesSavedAdditionalOutputBase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempDirectory, "Default");
            string rootOutputBase = Path.Combine(tempDirectory, "Root");
            string serializedAdditionalOutputBases = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(tempDirectory, "DP")]);

            var table = new BMSTable
            {
                name = "Table",
                Output_dir = "Table",
                is_root_folder = true,
                custom_folder_output_base_name = "DP"
            };

            Assert.AreEqual(
                Path.Combine(rootOutputBase, "Table"),
                BMSPlaylist.GetCustomFolderOutputDirectory(
                    table,
                    normalOutputBase,
                    rootOutputBase,
                    serializedAdditionalOutputBases));
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
