using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;

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
        bool resetColumnSettingsCalled = false;
        bool shutdownCalled = false;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsChartDigestMapSchema: false, needsBmsonSongSchema: false, needsInitialSha256BackfillWarning: false, needsBmsonAppSchemaMigration: false, RepairableBmsonSchemaIssues.None);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, _ => false, delegate
        {
            ensureSchemaCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        }, delegate
        {
            resetColumnSettingsCalled = true;
        });

        Assert.IsFalse(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsFalse(ensureSchemaCalled);
        Assert.IsFalse(resetColumnSettingsCalled);
        Assert.IsTrue(shutdownCalled);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyBmsonMigrationPreflightForStartup_Ok_EnsuresSchemaResetsColumnsAndApprovesSession()
    {
        bool approvedForSession = false;
        bool ensureSchemaCalled = false;
        bool resetColumnSettingsCalled = false;
        bool shutdownCalled = false;
        string callOrder = string.Empty;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsChartDigestMapSchema: false, needsBmsonSongSchema: false, needsInitialSha256BackfillWarning: false, needsBmsonAppSchemaMigration: false, RepairableBmsonSchemaIssues.None);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, _ => true, delegate
        {
            ensureSchemaCalled = true;
            callOrder += "schema;";
        }, delegate
        {
            shutdownCalled = true;
        }, delegate
        {
            resetColumnSettingsCalled = true;
            callOrder += "columns;";
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsTrue(approvedForSession);
        Assert.IsTrue(ensureSchemaCalled);
        Assert.IsTrue(resetColumnSettingsCalled);
        Assert.IsFalse(shutdownCalled);
        Assert.AreEqual("schema;columns;", callOrder);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildBmsonMigrationWarningMessage_IncludesCompatibilityAndDurationWarnings()
    {
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: true, needsChartDigestMapSchema: true, needsBmsonSongSchema: true, needsInitialSha256BackfillWarning: true, needsBmsonAppSchemaMigration: true, RepairableBmsonSchemaIssues.ChartDigestMapTableMissing | RepairableBmsonSchemaIssues.BmsonSongTableMissing);

        string message = MainWindowViewModel.BuildBmsonMigrationWarningMessage(result);

        Assert.AreEqual(Resources.BmsonMigrationWarningMessage, message);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyBmsonMigrationPreflightForStartup_RepairOnly_DoesNotRequestWarning()
    {
        bool approvedForSession = false;
        bool ensureSchemaCalled = false;
        bool resetColumnSettingsCalled = false;
        bool shutdownCalled = false;
        BmsonMigrationPreflightResult result = new BmsonMigrationPreflightResult(needsPlaylistEntrySha256Migration: false, needsChartDigestMapSchema: false, needsBmsonSongSchema: true, needsInitialSha256BackfillWarning: false, needsBmsonAppSchemaMigration: false, RepairableBmsonSchemaIssues.BmsonSongTableMissing);

        bool shouldContinue = MainWindowViewModel.ApplyBmsonMigrationPreflightForStartup(result, ref approvedForSession, null, delegate
        {
            ensureSchemaCalled = true;
        }, delegate
        {
            shutdownCalled = true;
        }, delegate
        {
            resetColumnSettingsCalled = true;
        });

        Assert.IsTrue(shouldContinue);
        Assert.IsFalse(approvedForSession);
        Assert.IsTrue(ensureSchemaCalled);
        Assert.IsFalse(resetColumnSettingsCalled);
        Assert.IsFalse(shutdownCalled);
        Assert.IsFalse(result.WarnRequired);
        Assert.IsTrue(result.RepairRequired);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ResetBmsonColumnSettingsForMigrationIfNeeded_VersionZero_RegeneratesMainGridSettingsAndMarksVersion()
    {
        Settings settings = new Settings();
        bool saveCalled = false;
        PlaylistSummaryColumnSettings playlistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        dataGridColumnsSettings expectedStandardColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
        dataGridColumnsSettings expectedChartInfoParseErrorColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.CHART_INFO_PARSE_ERROR);
        settings.BmsonColumnSettingsMigrationVersion = 0;
        settings.StandardColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
        settings.StandardColumnsSettings.Title.Visibility = Visibility.Hidden;
        settings.StandardColumnsSettings.Title.DisplayIndex = 99;
        settings.PlaylistSummaryColumnsSettings = playlistSummaryColumnsSettings;

        bool reset = MainWindowViewModel.ResetBmsonColumnSettingsForMigrationIfNeeded(settings, delegate
        {
            saveCalled = true;
        });

        Assert.IsTrue(reset);
        Assert.IsTrue(saveCalled);
        Assert.AreEqual(MainWindowViewModel.CurrentBmsonColumnSettingsMigrationVersion, settings.BmsonColumnSettingsMigrationVersion);
        Assert.AreEqual(expectedStandardColumnsSettings.Title.Visibility, settings.StandardColumnsSettings.Title.Visibility);
        Assert.AreEqual(expectedStandardColumnsSettings.Title.DisplayIndex, settings.StandardColumnsSettings.Title.DisplayIndex);
        Assert.AreEqual(expectedChartInfoParseErrorColumnsSettings.Warning.Visibility, settings.ChartInfoParseErrorColumnsSettings.Warning.Visibility);
        Assert.AreEqual(expectedChartInfoParseErrorColumnsSettings.Warning.DisplayIndex, settings.ChartInfoParseErrorColumnsSettings.Warning.DisplayIndex);
        Assert.AreSame(playlistSummaryColumnsSettings, settings.PlaylistSummaryColumnsSettings);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ResetBmsonColumnSettingsForMigrationIfNeeded_CurrentVersion_DoesNotOverwriteExistingChoices()
    {
        Settings settings = new Settings();
        bool saveCalled = false;
        settings.BmsonColumnSettingsMigrationVersion = MainWindowViewModel.CurrentBmsonColumnSettingsMigrationVersion;
        settings.StandardColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
        settings.StandardColumnsSettings.Title.Visibility = Visibility.Hidden;
        settings.StandardColumnsSettings.Title.DisplayIndex = 99;

        bool reset = MainWindowViewModel.ResetBmsonColumnSettingsForMigrationIfNeeded(settings, delegate
        {
            saveCalled = true;
        });

        Assert.IsFalse(reset);
        Assert.IsFalse(saveCalled);
        Assert.AreEqual(Visibility.Hidden, settings.StandardColumnsSettings.Title.Visibility);
        Assert.AreEqual(99, settings.StandardColumnsSettings.Title.DisplayIndex);
    }
}
