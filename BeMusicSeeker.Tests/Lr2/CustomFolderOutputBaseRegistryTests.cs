using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomFolderOutputBaseRegistryTests
{
    /// <summary>
    /// 表に保存した追加出力基点名を解決してその表ディレクトリを選び、通常基点への誤出力を検出します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void GetCustomFolderOutputDirectory_UsesPlaylistAdditionalOutputBase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// 保存した追加基点名が未登録なら通常基点を選び、不明な名前から無効な出力先を作る誤りを検出します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void GetCustomFolderOutputDirectory_FallsBackToDefaultForUnknownAdditionalOutputBase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// ルート型の表は保存済み追加基点名に優先してルート出力先を使い、追加出力設定によるルート分類の上書きを検出します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void GetCustomFolderOutputDirectory_RootOutputBaseOverridesSavedAdditionalOutputBase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "CustomFolderOutputBaseRegistryTests", Guid.NewGuid().ToString("N"));
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
}
