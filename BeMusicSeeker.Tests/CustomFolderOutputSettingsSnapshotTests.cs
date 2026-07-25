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
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        string previousRootPath = Settings.Default.LR2RootPath;
        string previousOutputBaseDirectory = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDirectory = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirectories = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        bool previousEnableUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent;
        int previousPlaylistDefaultIgnoreFolderOutput = Settings.Default.PlaylistDefaultIgnoreFolderOutput;
        bool previousShowRecommUpdatedMsg = Settings.Default.ShowRecommUpdatedMsg;
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2RootPath = "lr2-root";
            Settings.Default.LR2CustomFolderOutputBaseDir = "output-base";
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = "root-output-base";
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[\"additional-output-base\"]";
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            Settings.Default.PlaylistDefaultIgnoreFolderOutput = 23;
            Settings.Default.ShowRecommUpdatedMsg = true;

            CustomFolderOutputSettingsSnapshot snapshot = CustomFolderOutputSettingsSnapshot.CreateCurrent(Settings.Default);

            Assert.IsTrue(snapshot.OperationModeLR2DB);
            Assert.AreEqual("lr2-root", snapshot.LR2RootPath);
            Assert.AreEqual("output-base", snapshot.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual("root-output-base", snapshot.LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual("[\"additional-output-base\"]", snapshot.LR2CustomFolderAdditionalOutputBaseDirs);
            Assert.IsTrue(snapshot.EnableDownloadLr2IrScoreAndDetectUnsent);
            Assert.AreEqual(23, snapshot.PlaylistDefaultIgnoreFolderOutput);
            Assert.IsTrue(snapshot.ShowRecommUpdatedMsg);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.LR2RootPath = previousRootPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDirectory;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDirectory;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirectories;
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = previousEnableUnsent;
            Settings.Default.PlaylistDefaultIgnoreFolderOutput = previousPlaylistDefaultIgnoreFolderOutput;
            Settings.Default.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
        }
    }
}
