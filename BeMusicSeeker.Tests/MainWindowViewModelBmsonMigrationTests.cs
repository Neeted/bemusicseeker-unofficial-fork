using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelBmsonMigrationTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyBmsonMigrationPreflightForStartup_Cancel_ShutsDownWithoutEnsuringSchema()
    {
        bool approvedForSession = false;
        bool ensureSchemaCalled = false;
        bool shutdownCalled = false;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsInitialSha256BackfillWarning: false);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, _ => false, delegate
        {
            ensureSchemaCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsFalse(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsFalse(ensureSchemaCalled);
        Assert.IsTrue(shutdownCalled);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyBmsonMigrationPreflightForStartup_Ok_EnsuresSchemaAndApprovesSession()
    {
        bool approvedForSession = false;
        bool ensureSchemaCalled = false;
        bool shutdownCalled = false;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsInitialSha256BackfillWarning: false);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, _ => true, delegate
        {
            ensureSchemaCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsTrue(approvedForSession);
        Assert.IsTrue(ensureSchemaCalled);
        Assert.IsFalse(shutdownCalled);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildBmsonMigrationWarningMessage_IncludesCompatibilityAndDurationWarnings()
    {
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsInitialSha256BackfillWarning: false);

        string message = MainWindowViewModel.BuildBmsonMigrationWarningMessage(result);

        StringAssert.Contains(message, "SHA-256");
        StringAssert.Contains(message, "v1.2.1.0");
        StringAssert.Contains(message, "互換性");
        StringAssert.Contains(message, "バックアップ");
    }
}
