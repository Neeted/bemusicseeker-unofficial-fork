using SQLite;

namespace BeMusicSeeker.Models.LR2;

public sealed class LR2ScoreDBExtended : LR2ScoreDB
{
    /// <summary>ownerが取得した接続leaseとgate待機時間を受け取り、設定とパス検査はfactoryへ委ねます。</summary>
    internal LR2ScoreDBExtended(string dbPath, SQLiteOpenFlags openFlags,
        ApplicationDatabaseScope.ConnectionLease lease, long processLockWaitMs)
        : base(dbPath, openFlags, lease)
    {
        IsReadOnlyConnection = (openFlags & SQLiteOpenFlags.ReadOnly) == SQLiteOpenFlags.ReadOnly;
        ProcessLockWaitMs = processLockWaitMs;
    }

    /// <summary>この接続が読取り専用で開かれたかを表します。</summary>
    public bool IsReadOnlyConnection { get; }

    /// <summary>ownerの同種別write gateの取得に要した時間です。読取り専用接続では0です。</summary>
    public long ProcessLockWaitMs { get; }
}
