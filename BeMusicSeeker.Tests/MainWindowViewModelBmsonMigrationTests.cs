using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelBmsonMigrationTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyBmsonMigrationPreflightForStartup_Cancel_ShutsDownWithoutApplyingMigration()
    {
        bool approvedForSession = false;
        bool migrationCalled = false;
        bool shutdownCalled = false;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsChartDigestMapSchema: false, needsBmsonSongSchema: false, needsBmsonAppSchemaMigration: false, RepairableBmsonSchemaIssues.None);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, _ => false, delegate
        {
            migrationCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsFalse(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsFalse(migrationCalled);
        Assert.IsTrue(shutdownCalled);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyBmsonMigrationPreflightForStartup_Ok_AppliesMigrationAndApprovesSession()
    {
        bool approvedForSession = false;
        bool migrationCalled = false;
        bool shutdownCalled = false;
        string callOrder = string.Empty;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsChartDigestMapSchema: false, needsBmsonSongSchema: false, needsBmsonAppSchemaMigration: false, RepairableBmsonSchemaIssues.None);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, _ => true, delegate
        {
            migrationCalled = true;
            callOrder += "migration;";
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsTrue(approvedForSession);
        Assert.IsTrue(migrationCalled);
        Assert.IsFalse(shutdownCalled);
        Assert.AreEqual("migration;", callOrder);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildBmsonMigrationWarningMessage_IncludesCompatibilityAndDurationWarnings()
    {
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsChartDigestMapSchema: true, needsBmsonSongSchema: true, needsBmsonAppSchemaMigration: true, RepairableBmsonSchemaIssues.ChartDigestMapTableMissing | RepairableBmsonSchemaIssues.BmsonSongTableMissing);

        string message = MainWindowViewModel.BuildBmsonMigrationWarningMessage(result);

        Assert.AreEqual(Resources.BmsonMigrationWarningMessage, message);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyBmsonMigrationPreflightForStartup_RepairOnly_DoesNotRequestWarning()
    {
        bool approvedForSession = false;
        bool migrationCalled = false;
        bool shutdownCalled = false;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: false, needsChartDigestMapSchema: false, needsBmsonSongSchema: true, needsBmsonAppSchemaMigration: false, RepairableBmsonSchemaIssues.BmsonSongTableMissing);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, null, delegate
        {
            migrationCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsTrue(migrationCalled);
        Assert.IsFalse(shutdownCalled);
        Assert.IsFalse(result.WarnRequired);
        Assert.IsTrue(result.RepairRequired);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyBmsonMigrationPreflightForStartup_NormalPreparation_DoesNotRequestWarning()
    {
        bool approvedForSession = false;
        bool migrationCalled = false;
        bool shutdownCalled = false;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(
            needsPlaylistEntrySha256Migration: false,
            needsChartDigestMapSchema: true,
            needsBmsonSongSchema: true,
            needsBmsonAppSchemaMigration: true,
            repairableBmsonSchemaIssues: RepairableBmsonSchemaIssues.ChartDigestMapTableMissing | RepairableBmsonSchemaIssues.BmsonSongTableMissing,
            needsBmsonAppSchemaWarning: false);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, null, delegate
        {
            migrationCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsTrue(migrationCalled);
        Assert.IsFalse(shutdownCalled);
        Assert.IsFalse(result.WarnRequired);
        Assert.IsTrue(result.NeedsBmsonAppSchemaMigration);
    }
}
