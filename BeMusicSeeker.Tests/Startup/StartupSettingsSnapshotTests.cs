using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupSettingsSnapshotTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void CreateCurrentCapturesStartupTailSettingsAsOneSnapshot()
    {
        Uri previousTableListUrl = testSettings.TableListURL;
        bool previousBackupEnabled = testSettings.IsLR2BackupEnabled;
        Backup.Target previousBackupTarget = testSettings.LR2BackupTarget;
        string previousBackupPath = testSettings.LR2BackupPath;
        int previousBackupSpan = testSettings.LR2BackupSpan;
        int previousBackupNum = testSettings.LR2BackupNum;
        bool previousSkipInitPlaylistLoad = testSettings.SkipInitPlaylistLoad;
        bool previousStartupSelectInstallPending = testSettings.StartupSelectInstallPending;
        bool previousShowDuplicateFileCheckConfirmMsg = testSettings.ShowDuplicateFileCheckConfirmMsg;
        bool previousUseBeatorajaScoreDb = testSettings.UseBeatorajaScoreDb;
        string previousBeatorajaRootPath = testSettings.BeatorajaRootPath;
        string previousBeatorajaPlayerId = testSettings.BeatorajaPlayerId;
        string previousBeatorajaScoreDbPath = testSettings.BeatorajaScoreDbPath;
        try
        {
            Uri tableListUrl = new("https://example.invalid/startup-table-list");
            testSettings.TableListURL = tableListUrl;
            testSettings.IsLR2BackupEnabled = true;
            testSettings.LR2BackupTarget = Backup.Target.Config | Backup.Target.ScoreDB;
            testSettings.LR2BackupPath = "backup-root";
            testSettings.LR2BackupSpan = 7;
            testSettings.LR2BackupNum = 4;
            testSettings.SkipInitPlaylistLoad = true;
            testSettings.StartupSelectInstallPending = true;
            testSettings.ShowDuplicateFileCheckConfirmMsg = true;
            testSettings.UseBeatorajaScoreDb = true;
            testSettings.BeatorajaRootPath = "beatoraja-root";
            testSettings.BeatorajaPlayerId = "player-id";
            testSettings.BeatorajaScoreDbPath = "beatoraja-score.db";

            var snapshot = StartupSettingsSnapshot.CreateCurrent(testSettings);

            Assert.AreEqual(tableListUrl, snapshot.TableListURL);
            Assert.IsTrue(snapshot.IsLR2BackupEnabled);
            Assert.AreEqual(Backup.Target.Config | Backup.Target.ScoreDB, snapshot.LR2BackupTarget);
            Assert.AreEqual("backup-root", snapshot.LR2BackupPath);
            Assert.AreEqual(7, snapshot.LR2BackupSpan);
            Assert.AreEqual(4, snapshot.LR2BackupNum);
            Assert.IsTrue(snapshot.SkipInitPlaylistLoad);
            Assert.IsTrue(snapshot.StartupSelectInstallPending);
            Assert.IsTrue(snapshot.ShowDuplicateFileCheckConfirmMsg);
            Assert.IsTrue(snapshot.UseBeatorajaScoreDb);
            Assert.AreEqual("beatoraja-root", snapshot.BeatorajaRootPath);
            Assert.AreEqual("player-id", snapshot.BeatorajaPlayerId);
            Assert.AreEqual("beatoraja-score.db", snapshot.BeatorajaScoreDbPath);
        }
        finally
        {
            testSettings.TableListURL = previousTableListUrl;
            testSettings.IsLR2BackupEnabled = previousBackupEnabled;
            testSettings.LR2BackupTarget = previousBackupTarget;
            testSettings.LR2BackupPath = previousBackupPath;
            testSettings.LR2BackupSpan = previousBackupSpan;
            testSettings.LR2BackupNum = previousBackupNum;
            testSettings.SkipInitPlaylistLoad = previousSkipInitPlaylistLoad;
            testSettings.StartupSelectInstallPending = previousStartupSelectInstallPending;
            testSettings.ShowDuplicateFileCheckConfirmMsg = previousShowDuplicateFileCheckConfirmMsg;
            testSettings.UseBeatorajaScoreDb = previousUseBeatorajaScoreDb;
            testSettings.BeatorajaRootPath = previousBeatorajaRootPath;
            testSettings.BeatorajaPlayerId = previousBeatorajaPlayerId;
            testSettings.BeatorajaScoreDbPath = previousBeatorajaScoreDbPath;
        }
    }

    [TestMethod]
    public void CreateCurrentReadsStandaloneRootsThroughSettingsAdapter()
    {
        string previousStandaloneRoots = testSettings.StandaloneBmsRootPaths;
        string previousLegacyRoot = testSettings.BMSRootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "StartupSettingsSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.StandaloneBmsRootPaths = tempDirectory;
            testSettings.BMSRootPath = null;

            var snapshot = StartupSettingsSnapshot.CreateCurrent(testSettings);

            Assert.IsTrue(snapshot.StandaloneBmsRootPaths.Contains(tempDirectory, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            testSettings.StandaloneBmsRootPaths = previousStandaloneRoots;
            testSettings.BMSRootPath = previousLegacyRoot;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
