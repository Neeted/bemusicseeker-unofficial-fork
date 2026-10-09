using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BeatorajaBmtOptionsSnapshotTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
    [TestMethod]
    public void CreateCurrentCapturesAllBmtExportSettings()
    {
        bool previousEnabled = testSettings.EnableBeatorajaBmtOutput;
        bool previousKeepFiles = testSettings.KeepBeatorajaBmtFilesWhenOutputDisabled;
        string previousRootPath = testSettings.BeatorajaRootPath;
        string previousTablePath = testSettings.BeatorajaBmtTablePath;
        bool previousRegisterUrls = testSettings.RegisterBeatorajaBmtUrls;
        string previousHashMode = testSettings.BeatorajaBmtHashOutputMode;
        try
        {
            testSettings.EnableBeatorajaBmtOutput = true;
            testSettings.KeepBeatorajaBmtFilesWhenOutputDisabled = true;
            testSettings.BeatorajaRootPath = "beatoraja-root";
            testSettings.BeatorajaBmtTablePath = "table.json";
            testSettings.RegisterBeatorajaBmtUrls = true;
            testSettings.BeatorajaBmtHashOutputMode = "FillMissingMd5Sha256";

            var snapshot = BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings);

            Assert.IsTrue(snapshot.EnableBeatorajaBmtOutput);
            Assert.IsTrue(snapshot.KeepBeatorajaBmtFilesWhenOutputDisabled);
            Assert.AreEqual("beatoraja-root", snapshot.BeatorajaRootPath);
            Assert.AreEqual("table.json", snapshot.BeatorajaBmtTablePath);
            Assert.IsTrue(snapshot.RegisterBeatorajaBmtUrls);
            Assert.AreEqual("FillMissingMd5Sha256", snapshot.BeatorajaBmtHashOutputMode);
        }
        finally
        {
            testSettings.EnableBeatorajaBmtOutput = previousEnabled;
            testSettings.KeepBeatorajaBmtFilesWhenOutputDisabled = previousKeepFiles;
            testSettings.BeatorajaRootPath = previousRootPath;
            testSettings.BeatorajaBmtTablePath = previousTablePath;
            testSettings.RegisterBeatorajaBmtUrls = previousRegisterUrls;
            testSettings.BeatorajaBmtHashOutputMode = previousHashMode;
        }
    }

}
