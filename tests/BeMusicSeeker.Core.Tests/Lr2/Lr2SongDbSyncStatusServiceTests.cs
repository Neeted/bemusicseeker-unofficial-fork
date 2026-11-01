using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncStatusServiceTests
{
    /// <summary>
    /// LR2同期を無効にした入力では同期不要と判定し、設定に反する作業の受付を検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void Evaluate_ReturnsNotNeededWhenFeatureIsDisabled()
    {
        using var scope = TestDatabaseScope.Create();
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: false,
            nowUtc: now);

        Assert.AreEqual(Lr2SongDbSyncStatusKind.NotNeeded, snapshot.Status);
        Assert.IsFalse(snapshot.IsNeeded);
    }

    /// <summary>
    /// 保存状態がない場合は同期が必要と判定し、未実施を完了扱いにする誤りを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void Evaluate_ReturnsNeededWhenStatusRowIsMissing()
    {
        using var scope = TestDatabaseScope.Create();
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            nowUtc: now);

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, snapshot.Status);
        Assert.IsTrue(snapshot.IsNeeded);
    }

    /// <summary>
    /// 旧複合署名付きCompletedを再利用し、判定だけでは保存行やschemaを書き換えないことを確認します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ReusesLegacyCompletedWithoutChangingStoredRow()
    {
        using var scope = TestDatabaseScope.Create();
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
        BmsLibraryDbGateway.EnsureAppOwnedSchema(songDb);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);
        const string legacySignature = "lr2_song_db_sync_v2|appSchema=1|chartInfoSchema=1|chartInfoParser=21|operationModeLR2DB=1";
        songDb.InsertOrReplace(new LR2SongDBExtended.lr2_song_db_sync_status
        {
            name = Lr2SongDbSyncStatusService.DefaultStatusName,
            status = "Completed",
            signature = legacySignature,
            run_id = "legacy-run",
            processed_cursor = 10,
            total_count = 10,
            stage = "completed",
            last_error = "",
            updated_at = now,
            completed_at = now
        });
        string schemaBefore = songDb.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name = 'lr2_song_db_sync_status'");
        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(songDb, true, now.AddMinutes(1));
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, snapshot.Status);
        Assert.AreEqual(10, snapshot.ProcessedCursor);
        Assert.AreEqual(10, snapshot.TotalCount);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual(legacySignature, row.signature);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual("legacy-run", row.run_id);
        Assert.AreEqual(10, row.processed_cursor);
        Assert.AreEqual(10, row.total_count);
        Assert.AreEqual("completed", row.stage);
        Assert.AreEqual("", row.last_error);
        Assert.AreEqual(now, row.updated_at);
        Assert.AreEqual(now, row.completed_at);
        Assert.AreEqual(schemaBefore, songDb.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name = 'lr2_song_db_sync_status'"));
    }

    /// <summary>
    /// 保存された失敗・旧取消・未完了を再同期が必要な状態として読み、元終端種別を取り違えて完了扱いにする誤りを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void Evaluate_ReturnsNeededForRestartableTerminalStates()
    {
        using var scope = TestDatabaseScope.Create();
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkFailed(songDb, "run1", processedCursor: 3, totalCount: 10, stage: "song", error: "failed", nowUtc: now);
        AssertNeededWithStoredStatus(songDb, Lr2SongDbSyncStatusKind.Failed);

        songDb.InsertOrReplace(new LR2SongDBExtended.lr2_song_db_sync_status
        {
            name = Lr2SongDbSyncStatusService.DefaultStatusName,
            status = "Cancelled",
            signature = "sig1",
            run_id = "legacy-run",
            processed_cursor = 4,
            total_count = 10,
            stage = "folder",
            updated_at = now.AddMinutes(1)
        }, typeof(LR2SongDBExtended.lr2_song_db_sync_status));
        AssertNeededWithStoredStatus(songDb, Lr2SongDbSyncStatusKind.Cancelled);

        Lr2SongDbSyncStatusService.MarkIncomplete(songDb, "run3", processedCursor: 5, totalCount: 10, stage: "finalize", detail: "stale", nowUtc: now.AddMinutes(2));
        AssertNeededWithStoredStatus(songDb, Lr2SongDbSyncStatusKind.Incomplete);
        foreach (Lr2SongDbSyncStatusKind stored in new[] { Lr2SongDbSyncStatusKind.Needed, Lr2SongDbSyncStatusKind.Running, Lr2SongDbSyncStatusKind.NotNeeded })
        {
            songDb.Execute("UPDATE lr2_song_db_sync_status SET status = ?;", stored.ToString());
            AssertNeededWithStoredStatus(songDb, stored);
        }
    }

    /// <summary>
    /// 中断位置は診断として保持し、新しい実行開始は保存カーソル0へ戻して途中からの無断再開を検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void NonCompletedStatusAndRunningMarkAlwaysStartAtZero()
    {
        using var scope = TestDatabaseScope.Create();
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            "previous-run",
            processedCursor: 9,
            totalCount: 10,
            stage: "song_rows",
            detail: "interrupted",
            nowUtc: now);

        Lr2SongDbSyncStatusSnapshot needed = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            nowUtc: now.AddMinutes(1));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, needed.Status);
        Assert.AreEqual(9, needed.ProcessedCursor);

        Lr2SongDbSyncStatusService.MarkRunning(
            songDb,
            "new-run",
            totalCount: 10,
            stage: "normal_folders",
            nowUtc: now.AddMinutes(2),
            processedCursor: 9);

        LR2SongDBExtended.lr2_song_db_sync_status running =
            songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Running", running.status);
        Assert.AreEqual(0, running.processed_cursor);
    }

    /// <summary>
    /// 実行中の実行識別・工程・位置・総数を実DBへ保存し、完了時刻を付ける誤りや更新漏れを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void UpdateCursor_PersistsProgress()
    {
        using var scope = TestDatabaseScope.Create();
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkRunning(songDb, "run1", totalCount: 100, stage: "song", nowUtc: now);
        Lr2SongDbSyncStatusService.UpdateCursor(songDb, "run1", processedCursor: 40, totalCount: 100, stage: "song", nowUtc: now.AddMinutes(1));

        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Running", row.status);
        Assert.AreEqual(string.Empty, row.signature);
        Assert.AreEqual("run1", row.run_id);
        Assert.AreEqual(40, row.processed_cursor);
        Assert.AreEqual(100, row.total_count);
        Assert.AreEqual("song", row.stage);
        Assert.IsNull(row.completed_at);
    }

    /// <summary>
    /// 同じ実行の失敗に位置指定がなければ直前の保存位置と総数を保持し、失敗記録で診断位置を消す誤りを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void MarkFailed_PreservesSameRunCursorWhenNoCursorIsProvided()
    {
        using var scope = TestDatabaseScope.Create();
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkRunning(songDb, "run1", totalCount: 100, stage: "song", nowUtc: now);
        Lr2SongDbSyncStatusService.UpdateCursor(songDb, "run1", processedCursor: 40, totalCount: 100, stage: "song", nowUtc: now.AddMinutes(1));
        songDb.Execute("UPDATE lr2_song_db_sync_status SET signature = 'legacy-signature';");
        Lr2SongDbSyncStatusService.MarkFailed(songDb, "run1", processedCursor: null, totalCount: null, stage: "failed", error: "boom", nowUtc: now.AddMinutes(2));

        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Failed", row.status);
        Assert.AreEqual(40, row.processed_cursor);
        Assert.AreEqual(100, row.total_count);
        Assert.AreEqual("failed", row.stage);
        Assert.AreEqual(string.Empty, row.signature);
        Lr2SongDbSyncStatusService.MarkFailed(songDb, "different-run", null, null, "failed", "other", now.AddMinutes(3));
        row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.IsNull(row.processed_cursor);
        Assert.IsNull(row.total_count);
    }

    /// <summary>
    /// アプリ所有スキーマの初期化後に同期状態を実挿入・読戻しでき、状態表の生成漏れを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void EnsureAppOwnedSchema_CreatesStatusTable()
    {
        using var scope = TestDatabaseScope.Create();
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);

        BmsLibraryDbGateway.EnsureAppOwnedSchema(songDb);

        songDb.InsertOrReplace(new LR2SongDBExtended.lr2_song_db_sync_status
        {
            name = Lr2SongDbSyncStatusService.DefaultStatusName,
            status = "Completed",
            signature = "sig1",
            updated_at = DateTime.UtcNow
        }, typeof(LR2SongDBExtended.lr2_song_db_sync_status));
        Assert.IsNotNull(songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName));
    }

    private static void AssertNeededWithStoredStatus(LR2SongDBExtended songDb, Lr2SongDbSyncStatusKind expectedStoredStatus)
    {
        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            nowUtc: new DateTime(2026, 6, 4, 2, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, snapshot.Status);
        Assert.AreEqual(expectedStoredStatus, snapshot.StoredStatus);
    }

    private sealed class TestDatabaseScope : IDisposable
    {
        private readonly TestWorkspace workspace;
        private readonly string directoryPath;

        /// <summary>このfixtureの接続で共有するDBの所有者です。</summary>
        internal ApplicationDatabaseScope DatabaseScope { get; } = new();

        public string SongDbPath { get; }

        private TestDatabaseScope(TestWorkspace workspace, string directoryPath)
        {
            this.workspace = workspace;
            this.directoryPath = directoryPath;
            SongDbPath = Path.Combine(directoryPath, "song.db");
        }

        public static TestDatabaseScope Create()
        {
            var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            try
            {
                string directoryPath = Path.Combine(workspace.RootDirectory, nameof(Lr2SongDbSyncStatusServiceTests), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directoryPath);
                return new TestDatabaseScope(workspace, directoryPath);
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            workspace.Dispose();
        }
    }
}
