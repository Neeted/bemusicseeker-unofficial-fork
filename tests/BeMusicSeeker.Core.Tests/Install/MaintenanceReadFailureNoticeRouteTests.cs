using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.InstallServiceComponentTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 再検査以外の保守の経路（導入後の保守・導入先の修正・導入見込み）で、読取りスレッドが窓口へ表示せず、
/// 保守が成功しても例外で終わっても、集めた読込み失敗を既存の通知経路へ渡すことを確認します。
/// </summary>
[TestClass]
public sealed class MaintenanceReadFailureNoticeRouteTests
{
    /// <summary>
    /// 自動導入の確定後の保守が保存の失敗で例外になっても、読めなかった譜面の通知を導入の通知の列へ渡し、
    /// モデルの窓口へ即時に表示せず、保存の失敗を元の型と原因のまま保つ。例外の経路で通知を失う誤りと、
    /// 通知を即時に表示する誤りを検出する。
    /// </summary>
    /// <remarks>導入先の一件を置いた直後に排他で開いたままにして読めなくし、もう一件の保守行の保存をトリガーで拒否する。</remarks>
    [TestMethod]
    public void InstallChartPackagesAuto_WhenMaintenanceCommitFails_StillQueuesReadFailureNotice()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string installRootPath = Path.Combine(tempRootPath, "AutoInstalled");
            string firstSource = Path.Combine(tempRootPath, "AutoPendingFirst");
            string secondSource = Path.Combine(tempRootPath, "AutoPendingSecond");
            CreateBmsFile(firstSource, "first.bms", "#TITLE Auto Prefix First");
            CreateBmsFile(secondSource, "second.bms", "#TITLE Auto Prefix Second");
            string lockedPath = Path.Combine(installRootPath, "Auto Prefix Second", "second.bms");
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songDbPath))
            {
                seed.Execute("CREATE TRIGGER reject_maintenance_insert BEFORE INSERT ON maintenance BEGIN SELECT RAISE(ABORT, 'forced maintenance commit failure'); END;");
                seed.Execute("CREATE TRIGGER reject_maintenance_update BEFORE UPDATE ON maintenance BEGIN SELECT RAISE(ABORT, 'forced maintenance commit failure'); END;");
            }
            FileStream? lockStream = null;
            try
            {
                var files = new RealFileMutationService
                {
                    AfterPlace = _ =>
                    {
                        if (lockStream == null && File.Exists(lockedPath))
                        {
                            lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
                        }
                    }
                };
                var libraryDialogs = new FileDbReportRecordingDialogs();
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, files, libraryDialogs,
                    new TestUiScheduler(() => null!),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = false,
                        FolderNameFormat = "%TITLE%",
                        BMSInstallDir = installRootPath,
                        KeepInstallablePackagesPending = false
                    });
                library.BmsCharts = [];
                Directory.CreateDirectory(installRootPath);
                library.FixtureSettings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([installRootPath]);

                var notices = new LibraryOperationNotices();
                PackageInstallCommandResult result = library.InstallChartPackagesAutoWithProgressAsync(
                    [firstSource, secondSource], CancellationToken.None, new RecordingPackageInstallProgressWriter(), notices)
                    .GetAwaiter().GetResult();

                Assert.IsNotNull(lockStream, "二件目の導入先を読めなくした。");
                MaintenanceReadFailureFixture.AssertCommitFailure(result.SessionReceipt.PrimaryFailure ?? result.SessionReceipt.FinalizationFailure);
                BMSLibrary.OperationDialogMessage notice = notices.Snapshot().Single(message =>
                    message.MessageBoxText.Contains(lockedPath, StringComparison.Ordinal));
                string[] parts = Resources.Error_BmsLoadFailedSkip.Split("{0}", StringSplitOptions.None);
                StringAssert.StartsWith(notice.MessageBoxText, parts[0]);
                Assert.AreEqual(Resources.MessageBoxTitle_Error, notice.Caption);
                Assert.AreEqual(0, libraryDialogs.ModelMessages, "読取りスレッドやモデルの窓口へ即時に表示しない。");
            }
            finally { lockStream?.Dispose(); }
        });
    }

    /// <summary>
    /// 導入先の修正の確定後の保守が保存の失敗で終わっても、読めなかった譜面の通知を操作の通知の列へ渡し、
    /// 即時に表示せず、保存の失敗を型と原因の連鎖のまま保つ。try/catch の後の通知を落とす誤りと、即時表示を検出する。
    /// </summary>
    /// <remarks>二件の修正のうち一件を置いた直後に排他で開いて読めなくし、もう一件の保守行の保存をトリガーで拒否する。通知の列はスレッドの範囲で受ける。</remarks>
    [TestMethod]
    public void FixInstallationDirectory_WhenMaintenanceCommitFails_StillQueuesReadFailureNotice()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectory = Directory.CreateDirectory(Path.Combine(tempRootPath, "Source")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(tempRootPath, "Destination")).FullName;
            ChartFile Create(string name)
            {
                string path = Path.Combine(sourceDirectory, name);
                File.WriteAllText(path, "#PLAYER 1\n#TITLE " + name + "\n#BPM 120\n#00111:01\n");
                return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)) with { Token = new OwnedChartToken() };
            }
            ChartFile readable = Create("a.bms");
            ChartFile locked = Create("b.bms");
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songDbPath))
            {
                seed.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(readable), typeof(LR2SongDB.song));
                seed.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(locked), typeof(LR2SongDB.song));
                // 修正の保存が使う未評価の保守行ではなく、保守の評価結果の保存だけを拒否する。
                seed.Execute("CREATE TRIGGER reject_maintenance_insert BEFORE INSERT ON maintenance WHEN NEW.wav_files_defined IS NOT NULL BEGIN SELECT RAISE(ABORT, 'forced maintenance commit failure'); END;");
                seed.Execute("CREATE TRIGGER reject_maintenance_update BEFORE UPDATE ON maintenance WHEN NEW.wav_files_defined IS NOT NULL BEGIN SELECT RAISE(ABORT, 'forced maintenance commit failure'); END;");
            }
            string lockedPath = Path.Combine(destination, "b.bms");
            FileStream? lockStream = null;
            try
            {
                var files = new RealFileMutationService
                {
                    AfterPlace = _ =>
                    {
                        if (lockStream == null && File.Exists(lockedPath))
                        {
                            lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
                        }
                    }
                };
                var libraryDialogs = new FileDbReportRecordingDialogs();
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, files, libraryDialogs,
                    new TestUiScheduler(() => null!))
                { BmsCharts = [readable, locked], BmsonCharts = [] };
                ChartFile Target(ChartFile chart) => ChartFileProjection.WithPackageState(chart, destination, string.Empty, string.Empty, []);
                try
                {
                    using BMSLibrary.OperationDialogScope scope = library.BeginOperationDialogScope();

                    LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts([Target(readable), Target(locked)]);

                    Assert.IsNotNull(lockStream, "二件目の導入先を読めなくした。");
                    MaintenanceReadFailureFixture.AssertCommitFailure(result.SessionReceipt.FinalizationFailure ?? result.SessionReceipt.PrimaryFailure);
                    BMSLibrary.OperationDialogMessage notice = scope.Messages.Single(message =>
                        message.MessageBoxText.Contains(lockedPath, StringComparison.Ordinal));
                    StringAssert.StartsWith(notice.MessageBoxText, Resources.Error_BmsLoadFailedSkip.Split("{0}", StringSplitOptions.None)[0]);
                    Assert.AreEqual(Resources.MessageBoxTitle_Error, notice.Caption);
                    Assert.AreEqual(0, libraryDialogs.ModelMessages, "操作の通知の列へ渡し、即時に表示しない。");
                }
                finally { library.RequestShutdown("fix-installation-read-failure-notice"); }
            }
            finally { lockStream?.Dispose(); }
        });
    }

    /// <summary>
    /// 導入見込みの保守が保存の失敗で失敗結果になっても、集めた読込み失敗は延期先へ渡り、
    /// モデルの窓口へ即時に表示されず、失敗は元の型と原因のまま返る。例外の経路で通知を失う誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task EstimatedInstallMaintenance_WhenCommitFails_DefersReadFailureNoticeAndKeepsFailure()
    {
        await MaintenanceReadFailureFixture.RunAsync(fixture =>
        {
            fixture.FailMaintenanceCommit();
            var feedback = new EstimatedInstallDeferredFeedback();

            PendingEstimatedInstallPostGuardResult result = fixture.Library.ApplyEstimatedInstallMaintenanceForDeferredDispatch(
                [.. fixture.Missing, fixture.Readable], feedback, out _);

            Exception? failure = null;
            try { result.ThrowIfFailed(); }
            catch (Exception exception) { failure = exception; }
            Assert.IsNotNull(failure, "保守の失敗は結果として返る。");
            MaintenanceReadFailureFixture.AssertCommitFailure(failure);
            EstimatedInstallFeedbackNotification notice = feedback.DrainNotifications().Single(
                notification => notification.Kind == EstimatedInstallFeedbackKind.Notice);
            Assert.IsNotNull(notice.Notice);
            Assert.AreEqual(fixture.ExpectedBody(), notice.Notice.MessageBoxText);
            Assert.AreEqual(0, fixture.LibraryDialogs.ModelMessages, "延期先へ渡し、モデルの窓口へ即時に表示しない。");
            return Task.CompletedTask;
        });
    }
}
