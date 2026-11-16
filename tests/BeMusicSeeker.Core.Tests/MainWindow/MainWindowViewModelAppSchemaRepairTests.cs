using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class MainWindowViewModelAppSchemaRepairTests
{

    /// <summary>共通の構造更新案内を表示資源から取得し、内部識別子を利用者へ露出しません。</summary>
    [TestMethod]
    public void BuildAppSchemaRepairWarningMessage_UsesRepairConfirmationResource()
    {
        var result = new AppSchemaPreflightResult(true, true, true, true, RepairableBmsonSchemaIssues.BmsonSongTableMissing);
        Assert.AreEqual(Resources.AppSchemaRepairWarningMessage, StartupLibraryInitializationWorkflowOwner.BuildAppSchemaRepairWarningMessage(result));
    }

}
