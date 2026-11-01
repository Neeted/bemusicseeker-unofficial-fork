using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupSettingsSnapshotTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>ケース開始後だけ利用できる、可変ファイル領域の所有者です。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>constructorと既存field準備の成功後にケース専有領域を取得します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>既存finallyと接続・Task終端の後にケース専有領域を回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();
    /// <summary>
    /// 保証：利用者が指定した起動後処理とスコア参照の設定を一度の捕捉で渡し、設定項目の欠落や取り違えを検出する。
    /// </summary>
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

    /// <summary>
    /// 保証：旧単一ルートが未指定でも複数ルート設定を設定アダプター経由で捕捉し、起動入力から検索先を落とさない。
    /// </summary>
    [TestMethod]
    public void CreateCurrentReadsStandaloneRootsThroughSettingsAdapter()
    {
        string previousStandaloneRoots = testSettings.StandaloneBmsRootPaths;
        string previousLegacyRoot = testSettings.BMSRootPath;
        string tempDirectory = Path.Combine(CaseWorkspace.RootDirectory, "StartupSettingsSnapshotTests", Guid.NewGuid().ToString("N"));
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
