using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CustomFolderOutputBaseRegistryTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void GetCustomFolderOutputDirectory_UsesPlaylistAdditionalOutputBase()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempDirectory, "Default");
            string rootOutputBase = Path.Combine(tempDirectory, "Root");
            string dpOutputBase = Path.Combine(tempDirectory, "DP");
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([dpOutputBase]);

            var table = new BMSTable
            {
                name = "Table",
                Output_dir = "Table",
                custom_folder_output_base_name = "DP"
            };

            Assert.AreEqual(
                Path.Combine(dpOutputBase, "Table"),
                BMSPlaylist.GetCustomFolderOutputDirectory(table));
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
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
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempDirectory, "Default");
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(tempDirectory, "DP")]);

            var table = new BMSTable
            {
                name = "Table",
                Output_dir = "Table",
                custom_folder_output_base_name = "Missing"
            };

            Assert.AreEqual(
                Path.Combine(normalOutputBase, "Table"),
                BMSPlaylist.GetCustomFolderOutputDirectory(table));
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
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
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string rootOutputBase = Path.Combine(tempDirectory, "Root");
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(tempDirectory, "DP")]);

            var table = new BMSTable
            {
                name = "Table",
                Output_dir = "Table",
                is_root_folder = true,
                custom_folder_output_base_name = "DP"
            };

            Assert.AreEqual(
                Path.Combine(rootOutputBase, "Table"),
                BMSPlaylist.GetCustomFolderOutputDirectory(table));
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
