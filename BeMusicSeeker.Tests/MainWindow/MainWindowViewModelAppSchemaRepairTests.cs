using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelAppSchemaRepairTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void TryStartAppSchemaRepairForStartup_Cancel_ShutsDownWithoutApplyingRepair()
    {
        bool approvedForSession = false;
        bool repairCalled = false;
        bool shutdownCalled = false;
        var result = new AppSchemaPreflightResult(needsPlaylistEntrySha256Repair: true, needsChartDigestMapSchema: false, needsBmsonSongSchema: false, needsAppSchemaVersionRepair: false, RepairableBmsonSchemaIssues.None);

        bool shouldContinue = MainWindowViewModel.TryStartAppSchemaRepairForStartup(result, ref approvedForSession, _ => false, delegate
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

    [TestMethod]
    [TestCategory("Playlist")]
    public void TryStartAppSchemaRepairForStartup_Ok_AppliesRepairAndApprovesSession()
    {
        bool approvedForSession = false;
        bool repairCalled = false;
        bool shutdownCalled = false;
        string callOrder = string.Empty;
        var result = new AppSchemaPreflightResult(needsPlaylistEntrySha256Repair: true, needsChartDigestMapSchema: false, needsBmsonSongSchema: false, needsAppSchemaVersionRepair: false, RepairableBmsonSchemaIssues.None);

        bool shouldContinue = MainWindowViewModel.TryStartAppSchemaRepairForStartup(result, ref approvedForSession, _ => true, delegate
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

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildAppSchemaRepairWarningMessage_IncludesCompatibilityAndDurationWarnings()
    {
        var result = new AppSchemaPreflightResult(needsPlaylistEntrySha256Repair: true, needsChartDigestMapSchema: true, needsBmsonSongSchema: true, needsAppSchemaVersionRepair: true, RepairableBmsonSchemaIssues.ChartDigestMapTableMissing | RepairableBmsonSchemaIssues.BmsonSongTableMissing);

        string message = MainWindowViewModel.BuildAppSchemaRepairWarningMessage(result);

        Assert.AreEqual(Resources.AppSchemaRepairWarningMessage, message);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void TryStartAppSchemaRepairForStartup_RepairOnly_DoesNotRequestWarning()
    {
        bool approvedForSession = false;
        bool repairCalled = false;
        bool shutdownCalled = false;
        var result = new AppSchemaPreflightResult(needsPlaylistEntrySha256Repair: false, needsChartDigestMapSchema: false, needsBmsonSongSchema: true, needsAppSchemaVersionRepair: false, RepairableBmsonSchemaIssues.BmsonSongTableMissing);

        bool shouldContinue = MainWindowViewModel.TryStartAppSchemaRepairForStartup(result, ref approvedForSession, null, delegate
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

        bool shouldContinue = MainWindowViewModel.TryStartAppSchemaRepairForStartup(result, ref approvedForSession, null, delegate
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
