using System;
using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncStatusServiceTests
{
    /// <summary>
    /// LR2同期を無効にした入力では同期不要と判定し、設定に反する作業の受付を検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存署名・状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void Evaluate_ReturnsNotNeededWhenFeatureIsDisabled()
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: false,
            signature: "sig1",
            nowUtc: now);

        Assert.AreEqual(Lr2SongDbSyncStatusKind.NotNeeded, snapshot.Status);
        Assert.IsFalse(snapshot.IsNeeded);
    }

    /// <summary>
    /// 保存状態がない場合は指定署名の同期が必要と判定し、未実施を完了扱いにする誤りを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存署名・状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void Evaluate_ReturnsNeededWhenStatusRowIsMissing()
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: now);

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, snapshot.Status);
        Assert.IsTrue(snapshot.IsNeeded);
        Assert.AreEqual("sig1", snapshot.Signature);
    }

    /// <summary>
    /// 実保存した完了署名が一致する場合だけ完了を再利用し、署名変更時は旧保存状態を残して再同期へ戻すことを確認します。
    /// </summary>
    /// <remarks>状態表の生成と保存署名・状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void Evaluate_ReturnsCompletedOnlyForMatchingCompletedSignature()
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkCompleted(songDb, "sig1", "run1", totalCount: 10, nowUtc: now);

        Lr2SongDbSyncStatusSnapshot sameSignature = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: now.AddMinutes(1));
        Lr2SongDbSyncStatusSnapshot changedSignature = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig2",
            nowUtc: now.AddMinutes(1));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, sameSignature.Status);
        Assert.AreEqual(10, sameSignature.ProcessedCursor);
        Assert.AreEqual(10, sameSignature.TotalCount);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, changedSignature.Status);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, changedSignature.StoredStatus);
    }

    /// <summary>
    /// 保存された失敗・旧取消・未完了を再同期が必要な状態として読み、元終端種別を取り違えて完了扱いにする誤りを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存署名・状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void Evaluate_ReturnsNeededForRestartableTerminalStates()
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkFailed(songDb, "sig1", "run1", processedCursor: 3, totalCount: 10, stage: "song", error: "failed", nowUtc: now);
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

        Lr2SongDbSyncStatusService.MarkIncomplete(songDb, "sig1", "run3", processedCursor: 5, totalCount: 10, stage: "finalize", detail: "stale", nowUtc: now.AddMinutes(2));
        AssertNeededWithStoredStatus(songDb, Lr2SongDbSyncStatusKind.Incomplete);
    }

    /// <summary>
    /// 中断位置は診断として保持し、新しい実行開始は保存カーソル0へ戻して途中からの無断再開を検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存署名・状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void NonCompletedStatusAndRunningMarkAlwaysStartAtZero()
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            "sig1",
            "previous-run",
            processedCursor: 9,
            totalCount: 10,
            stage: "song_rows",
            detail: "interrupted",
            nowUtc: now);

        Lr2SongDbSyncStatusSnapshot needed = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: now.AddMinutes(1));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, needed.Status);
        Assert.AreEqual(9, needed.ProcessedCursor);

        Lr2SongDbSyncStatusService.MarkRunning(
            songDb,
            "sig1",
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
    /// 実行中の署名・実行識別・工程・位置・総数を実DBへ保存し、完了時刻を付ける誤りや更新漏れを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存署名・状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void UpdateCursor_PersistsProgress()
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkRunning(songDb, "sig1", "run1", totalCount: 100, stage: "song", nowUtc: now);
        Lr2SongDbSyncStatusService.UpdateCursor(songDb, "sig1", "run1", processedCursor: 40, totalCount: 100, stage: "song", nowUtc: now.AddMinutes(1));

        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Running", row.status);
        Assert.AreEqual("sig1", row.signature);
        Assert.AreEqual("run1", row.run_id);
        Assert.AreEqual(40, row.processed_cursor);
        Assert.AreEqual(100, row.total_count);
        Assert.AreEqual("song", row.stage);
        Assert.IsNull(row.completed_at);
    }

    /// <summary>
    /// 同じ実行の失敗に位置指定がなければ直前の保存位置と総数を保持し、失敗記録で診断位置を消す誤りを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存署名・状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void MarkFailed_PreservesSameRunCursorWhenNoCursorIsProvided()
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkRunning(songDb, "sig1", "run1", totalCount: 100, stage: "song", nowUtc: now);
        Lr2SongDbSyncStatusService.UpdateCursor(songDb, "sig1", "run1", processedCursor: 40, totalCount: 100, stage: "song", nowUtc: now.AddMinutes(1));
        Lr2SongDbSyncStatusService.MarkFailed(songDb, "sig1", "run1", processedCursor: null, totalCount: null, stage: "failed", error: "boom", nowUtc: now.AddMinutes(2));

        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Failed", row.status);
        Assert.AreEqual(40, row.processed_cursor);
        Assert.AreEqual(100, row.total_count);
        Assert.AreEqual("failed", row.stage);
    }

    /// <summary>
    /// アプリ所有スキーマの初期化後に同期状態を実挿入・読戻しでき、状態表の生成漏れを検出します。
    /// </summary>
    /// <remarks>状態表の生成と保存署名・状態の読戻しを実SQLiteで接続して確認します。</remarks>
    [TestMethod]
    public void EnsureAppOwnedSchema_CreatesStatusTable()
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);

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
            signature: "sig1",
            nowUtc: new DateTime(2026, 6, 4, 2, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, snapshot.Status);
        Assert.AreEqual(expectedStoredStatus, snapshot.StoredStatus);
    }

    private sealed class TestDatabaseScope : IDisposable
    {
        private readonly string directoryPath;

        public string SongDbPath { get; }

        private TestDatabaseScope(string directoryPath)
        {
            this.directoryPath = directoryPath;
            SongDbPath = Path.Combine(directoryPath, "song.db");
        }

        public static TestDatabaseScope Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), nameof(Lr2SongDbSyncStatusServiceTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new TestDatabaseScope(directoryPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
