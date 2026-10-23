using System;
using System.Collections.Generic;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

/// <summary>SQLite接続の終了追跡と読取り設定を提供します。SQL・Commitの失敗は外側で再試行せず呼出し元へ返します。</summary>
/// <remarks>Commit失敗後はproviderが内部transaction状態を解放するため、再呼出しで失敗を成功へ変換しません。</remarks>
public class SQLiteConnectionEx : SQLiteConnection
{
    private readonly IDisposable shutdownOperationLease;

    public SQLiteConnectionEx(string databasePath, bool storeDateTimeAsTicks = true)
        : base(databasePath, storeDateTimeAsTicks)
    {
        shutdownOperationLease = BeMusicSeeker.Models.ShutdownOperationTracker.TrackSqliteConnection();
    }

    public SQLiteConnectionEx(string databasePath, SQLiteOpenFlags openFlags, bool storeDateTimeAsTicks = true)
        : base(databasePath, openFlags, storeDateTimeAsTicks)
    {
        shutdownOperationLease = BeMusicSeeker.Models.ShutdownOperationTracker.TrackSqliteConnection();
    }

    public List<string> TryApplyReadOptimizedPragmas(bool enabled, int cacheSizeKb = 262144, long mmapSizeBytes = 2147483648L)
    {
        List<string> list = [];
        if (!enabled)
        {
            list.Add("enabled=false");
            return list;
        }
        TryApplyPragma("temp_store=MEMORY", list);
        TryApplyPragma("cache_size=-" + Math.Abs(cacheSizeKb), list);
        TryApplyPragma("mmap_size=" + Math.Max(0, mmapSizeBytes), list);
        return list;
    }

    private void TryApplyPragma(string pragmaBody, List<string> logs)
    {
        if (string.IsNullOrWhiteSpace(pragmaBody))
        {
            return;
        }
        try
        {
            long pragmaResult = ExecuteScalar<long>("PRAGMA " + pragmaBody + ";");
            if (pragmaBody.StartsWith("mmap_size=", StringComparison.OrdinalIgnoreCase))
            {
                logs?.Add(pragmaBody + ":ok(" + pragmaResult + ")");
            }
            else
            {
                logs?.Add(pragmaBody + ":ok");
            }
        }
        catch (Exception ex)
        {
            logs?.Add(pragmaBody + ":ng(" + ex.GetType().Name + ")");
        }
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        catch
        {
            BeMusicSeeker.Models.ShutdownOperationTracker.RecordSqliteCloseFailure();
            throw;
        }
        finally
        {
            shutdownOperationLease?.Dispose();
        }
    }
}
