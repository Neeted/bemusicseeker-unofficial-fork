using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomFolderOutputSettingsSnapshotTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
    /// <summary>出力先と出力抑止設定を捕捉した後にValuesを編集しても旧snapshotは変わらず、次の捕捉だけが編集値を使うことで、処理中の利用者編集の混入を検出します。</summary>
    [TestMethod]
    public void CreateCurrentFreezesOutputSettingsUntilNextCapture()
    {
        testSettings.LR2CustomFolderOutputBaseDir = "output-base";
        testSettings.PlaylistDefaultIgnoreFolderOutput = 23;
        var snapshot = CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings);

        testSettings.LR2CustomFolderOutputBaseDir = "edited-output-base";
        testSettings.PlaylistDefaultIgnoreFolderOutput = 42;
        Assert.AreEqual("output-base", snapshot.LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(23, snapshot.PlaylistDefaultIgnoreFolderOutput);
        var next = CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings);
        Assert.AreEqual("edited-output-base", next.LR2CustomFolderOutputBaseDir);
        Assert.AreEqual(42, next.PlaylistDefaultIgnoreFolderOutput);
    }
}
