#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models;

/// <summary>一つのcompositionが使用するDBの再入排他と、全SQLite接続の終了待機を所有します。</summary>
/// <remarks>書込みgateは取得スレッド内で解放します。受付の停止や操作の許可判断は呼出し側の責務です。</remarks>
internal sealed class ApplicationDatabaseScope
{
    private readonly object songGate = new();
    private readonly object scoreGate = new();
    private readonly object connectionState = new();
    private int activeConnectionCount;
    private int sqliteCloseFailureCount;
    private TaskCompletionSource? connectionsIdle;

    /// <summary>native接続の取得前からclose後までの、生存中の接続lease数です。</summary>
    internal int ActiveConnectionCount
    {
        get { lock (connectionState) { return activeConnectionCount; } }
    }

    /// <summary>このownerの接続で発生したclose失敗の累積数です。</summary>
    internal int SqliteCloseFailureCount => Volatile.Read(ref sqliteCloseFailureCount);

    /// <summary>呼出しスレッドがsong gateで保護されているかを観測します。再入可能なTryEnterの成功では未保持を判定できないために使用します。</summary>
    internal bool IsSongGateEnteredByCurrentThread => Monitor.IsEntered(songGate);

    /// <summary>songの排他を再入分も一回ずつ取得し、同じスレッドで解放するleaseを返します。</summary>
    internal IDisposable EnterSong() => Enter(songGate);

    /// <summary>scoreの排他を再入分も一回ずつ取得し、同じスレッドで解放するleaseを返します。</summary>
    internal IDisposable EnterScore() => Enter(scoreGate);

    /// <summary>指定時間内にsongの排他を取得し、時間切れならnullを返します。</summary>
    internal IDisposable? TryEnterSong(TimeSpan timeout) => TryEnter(songGate, timeout);

    /// <summary>指定時間内にscoreの排他を取得し、時間切れならnullを返します。</summary>
    internal IDisposable? TryEnterScore(TimeSpan timeout) => TryEnter(scoreGate, timeout);

    /// <summary>songの排他をnative open前に取得し、接続と初期化を一つの寿命で管理します。</summary>
    internal LR2SongDBExtended OpenSongWrite(string path, Action<LR2SongDBExtended>? initialize = null)
    {
        long started = Stopwatch.GetTimestamp();
        IDisposable gate = EnterSong();
        long waitMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return Open(lease => new LR2SongDBExtended(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create, lease, waitMs),
            initialize, gate, connection => connection.InitializePath(path), TimeSpan.FromMilliseconds(60000));
    }

    /// <summary>scoreの排他をnative open前に取得し、接続と初期化を一つの寿命で管理します。</summary>
    internal LR2ScoreDBExtended OpenScoreWrite(string path, Action<LR2ScoreDBExtended>? initialize = null)
    {
        long started = Stopwatch.GetTimestamp();
        IDisposable gate = EnterScore();
        long waitMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return Open(lease => new LR2ScoreDBExtended(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create, lease, waitMs),
            initialize, gate, connection => connection.InitializePath(path), TimeSpan.FromMilliseconds(60000));
    }

    /// <summary>songの書込みgateを取得せず、読取り専用接続の寿命と初期化を管理します。</summary>
    internal LR2SongDBExtended OpenSongReadOnly(string path, Action<LR2SongDBExtended>? initialize = null) =>
        Open(lease => new LR2SongDBExtended(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, lease, 0),
            initialize, validate: connection => connection.InitializePath(path), busyTimeout: TimeSpan.FromMilliseconds(60000));

    /// <summary>scoreの書込みgateを取得せず、読取り専用接続の寿命と初期化を管理します。</summary>
    internal LR2ScoreDBExtended OpenScoreReadOnly(string path, Action<LR2ScoreDBExtended>? initialize = null) =>
        Open(lease => new LR2ScoreDBExtended(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, lease, 0),
            initialize, validate: connection => connection.InitializePath(path), busyTimeout: TimeSpan.FromMilliseconds(60000));

    /// <summary>書込みgateを取得せず、provider既定のtimeoutで一般の読取り専用接続を管理します。</summary>
    internal SQLiteConnectionEx OpenReadOnly(string path, Action<SQLiteConnectionEx>? initialize = null) =>
        OpenProviderConnection(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, initialize);

    /// <summary>低位provider用に、DB種別のgateを取得せず接続寿命だけを管理し、provider既定のtimeoutを保持します。</summary>
    internal SQLiteConnectionEx OpenProviderConnection(string path,
        SQLiteOpenFlags flags = SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create,
        Action<SQLiteConnectionEx>? initialize = null) =>
        Open(lease => new SQLiteConnectionEx(path, flags, lease), initialize);

    /// <summary>現在の接続群の最後のleaseが解放されるまで待機します。次の接続群は別の待機を作ります。</summary>
    internal Task WaitForConnectionsIdleAsync()
    {
        lock (connectionState) { return connectionsIdle?.Task ?? Task.CompletedTask; }
    }

    /// <summary>native open前に寿命を取得します。writeGateがあればcloseと接続数解放の後に解放します。</summary>
    internal ConnectionLease AcquireConnectionLease(IDisposable? writeGate = null)
    {
        lock (connectionState)
        {
            if (activeConnectionCount++ == 0)
            {
                connectionsIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        return new ConnectionLease(this, writeGate);
    }

    private T Open<T>(Func<ConnectionLease, T> create, Action<T>? initialize, IDisposable? writeGate = null,
        Action<T>? validate = null, TimeSpan? busyTimeout = null)
        where T : SQLiteConnectionEx
    {
        ConnectionLease lease = AcquireConnectionLease(writeGate);
        T? connection = null;
        try
        {
            connection = create(lease);
            validate?.Invoke(connection);
            if (busyTimeout.HasValue) { connection.BusyTimeout = busyTimeout.Value; }
            initialize?.Invoke(connection);
            return connection;
        }
        catch
        {
            // 初期化例外をcleanupの二次故障で置換しない。close失敗はleaseが別途累積する。
            try
            {
                if (connection is not null) { connection.Dispose(); }
                else { lease.Close(static () => { }); }
            }
            catch { }
            throw;
        }
    }

    private static IDisposable Enter(object gate)
    {
        Monitor.Enter(gate);
        return new GateLease(gate);
    }

    private static IDisposable? TryEnter(object gate, TimeSpan timeout) =>
        Monitor.TryEnter(gate, timeout) ? new GateLease(gate) : null;

    private void ReleaseConnection()
    {
        TaskCompletionSource? completed = null;
        lock (connectionState)
        {
            if (--activeConnectionCount == 0) { completed = connectionsIdle; }
        }
        completed?.TrySetResult();
    }

    private sealed class GateLease(object gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) { Monitor.Exit(gate); }
        }
    }

    /// <summary>native closeの一回限りの実行、失敗記録、接続数とwrite gateの解放順を所有します。</summary>
    internal sealed class ConnectionLease(ApplicationDatabaseScope owner, IDisposable? writeGate)
    {
        private int closed;

        /// <summary>closeの例外をそのまま返し、成功・失敗とも接続数、write gateの順で一度だけ解放します。</summary>
        internal void Close(Action close)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) { return; }
            try { close(); }
            catch
            {
                Interlocked.Increment(ref owner.sqliteCloseFailureCount);
                throw;
            }
            finally
            {
                try { owner.ReleaseConnection(); }
                finally { writeGate?.Dispose(); }
            }
        }
    }
}
