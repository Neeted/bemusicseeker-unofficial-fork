using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelAppSchemaRepairTests
{
    /// <summary>利用者確認が必要な起動DB修復を拒否した場合は修復と承認を行わず終了だけ要求することを確認し、取消後にDBを変更して起動する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void TryStartAppSchemaRepairForStartup_Cancel_ShutsDownWithoutApplyingRepair()
    {
        bool approvedForSession = false;
        bool repairCalled = false;
        bool shutdownCalled = false;
        var result = new AppSchemaPreflightResult(needsPlaylistEntrySha256Repair: true, needsChartDigestMapSchema: false, needsBmsonSongSchema: false, needsAppSchemaVersionRepair: false, RepairableBmsonSchemaIssues.None);

        bool shouldContinue = StartupLibraryInitializationWorkflowOwner.TryStartAppSchemaRepairForStartup(result, ref approvedForSession, _ => false, delegate
        {
            repairCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsFalse(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsFalse(repairCalled);
        Assert.IsTrue(shutdownCalled);
    }

    /// <summary>利用者が起動DB修復を承認すると修復を実行して今回の承認を保持し終了は要求しないことを確認し、承認後も起動を中断したり修復を省く誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void TryStartAppSchemaRepairForStartup_Ok_AppliesRepairAndApprovesSession()
    {
        bool approvedForSession = false;
        bool repairCalled = false;
        bool shutdownCalled = false;
        string callOrder = string.Empty;
        var result = new AppSchemaPreflightResult(needsPlaylistEntrySha256Repair: true, needsChartDigestMapSchema: false, needsBmsonSongSchema: false, needsAppSchemaVersionRepair: false, RepairableBmsonSchemaIssues.None);

        bool shouldContinue = StartupLibraryInitializationWorkflowOwner.TryStartAppSchemaRepairForStartup(result, ref approvedForSession, _ => true, delegate
        {
            repairCalled = true;
            callOrder += "repair;";
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsTrue(approvedForSession);
        Assert.IsTrue(repairCalled);
        Assert.IsFalse(shutdownCalled);
        Assert.AreEqual("repair;", callOrder);
    }

    /// <summary>複数の起動DB修復が必要な場合に共通の修復確認リソースを返すことを確認し、DBの内部識別子や非リソース文言を利用者へ渡す誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildAppSchemaRepairWarningMessage_UsesRepairConfirmationResource()
    {
        var result = new AppSchemaPreflightResult(needsPlaylistEntrySha256Repair: true, needsChartDigestMapSchema: true, needsBmsonSongSchema: true, needsAppSchemaVersionRepair: true, RepairableBmsonSchemaIssues.ChartDigestMapTableMissing | RepairableBmsonSchemaIssues.BmsonSongTableMissing);

        string message = StartupLibraryInitializationWorkflowOwner.BuildAppSchemaRepairWarningMessage(result);

        Assert.AreEqual(Resources.AppSchemaRepairWarningMessage, message);
    }

    /// <summary>警告対象でないbmson不足テーブル修復は確認なしで実行して起動を継続することを確認し、修復の全条件を利用者承認必須として停止する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void TryStartAppSchemaRepairForStartup_RepairOnly_DoesNotRequestWarning()
    {
        bool approvedForSession = false;
        bool repairCalled = false;
        bool shutdownCalled = false;
        var result = new AppSchemaPreflightResult(needsPlaylistEntrySha256Repair: false, needsChartDigestMapSchema: false, needsBmsonSongSchema: true, needsAppSchemaVersionRepair: false, RepairableBmsonSchemaIssues.BmsonSongTableMissing);

        bool shouldContinue = StartupLibraryInitializationWorkflowOwner.TryStartAppSchemaRepairForStartup(result, ref approvedForSession, null, delegate
        {
            repairCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsTrue(repairCalled);
        Assert.IsFalse(shutdownCalled);
        Assert.IsFalse(result.WarnRequired);
        Assert.IsTrue(result.RepairRequired);
    }

    /// <summary>警告不要として判定した通常のテーブル・版準備は確認なしで実行し承認状態を変えないことを確認し、正常準備を互換修復警告へ変える誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void TryStartAppSchemaRepairForStartup_NormalPreparation_DoesNotRequestWarning()
    {
        bool approvedForSession = false;
        bool repairCalled = false;
        bool shutdownCalled = false;
        var result = new AppSchemaPreflightResult(
            needsPlaylistEntrySha256Repair: false,
            needsChartDigestMapSchema: true,
            needsBmsonSongSchema: true,
            needsAppSchemaVersionRepair: true,
            repairableBmsonSchemaIssues: RepairableBmsonSchemaIssues.ChartDigestMapTableMissing | RepairableBmsonSchemaIssues.BmsonSongTableMissing,
            needsAppSchemaVersionWarning: false);

        bool shouldContinue = StartupLibraryInitializationWorkflowOwner.TryStartAppSchemaRepairForStartup(result, ref approvedForSession, null, delegate
        {
            repairCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsTrue(repairCalled);
        Assert.IsFalse(shutdownCalled);
        Assert.IsFalse(result.WarnRequired);
        Assert.IsTrue(result.NeedsAppSchemaVersionRepair);
    }
}
