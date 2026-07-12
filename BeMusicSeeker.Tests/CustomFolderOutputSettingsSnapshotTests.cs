using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CustomFolderOutputSettingsSnapshotTests
{
    [TestMethod]
    public void CreateCurrentCapturesCustomFolderOutputResolutionSettings()
    {
        string previousOutputBaseDirectory = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDirectory = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirectories = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        try
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = "output-base";
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = "root-output-base";
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[\"additional-output-base\"]";

            CustomFolderOutputSettingsSnapshot snapshot = CustomFolderOutputSettingsSnapshot.CreateCurrent();

            Assert.AreEqual("output-base", snapshot.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual("root-output-base", snapshot.LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual("[\"additional-output-base\"]", snapshot.LR2CustomFolderAdditionalOutputBaseDirs);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDirectory;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDirectory;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirectories;
        }
    }
}
