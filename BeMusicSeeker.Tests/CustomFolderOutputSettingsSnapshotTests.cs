using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomFolderOutputSettingsSnapshotTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void CreateCurrentCapturesCustomFolderOutputResolutionSettings()
    {
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        string previousRootPath = testSettings.LR2RootPath;
        string previousOutputBaseDirectory = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDirectory = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirectories = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        bool previousEnableUnsent = testSettings.EnableDownloadLr2IrScoreAndDetectUnsent;
        int previousPlaylistDefaultIgnoreFolderOutput = testSettings.PlaylistDefaultIgnoreFolderOutput;
        bool previousShowRecommUpdatedMsg = testSettings.ShowRecommUpdatedMsg;
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = "lr2-root";
            testSettings.LR2CustomFolderOutputBaseDir = "output-base";
            testSettings.LR2CustomFolderOutputBaseDirRootType = "root-output-base";
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[\"additional-output-base\"]";
            testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            testSettings.PlaylistDefaultIgnoreFolderOutput = 23;
            testSettings.ShowRecommUpdatedMsg = true;

            CustomFolderOutputSettingsSnapshot snapshot = CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings);

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
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2RootPath = previousRootPath;
            testSettings.LR2CustomFolderOutputBaseDir = previousOutputBaseDirectory;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDirectory;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirectories;
            testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = previousEnableUnsent;
            testSettings.PlaylistDefaultIgnoreFolderOutput = previousPlaylistDefaultIgnoreFolderOutput;
            testSettings.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
        }
    }
}
