using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationDatabaseScopeTests
{
    /// <summary>同種別gateは別threadを排他し、同threadの再入を一つ解放しても外側の排他が残ることを保証します。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Gate_ReentryBalancesEveryLeaseAndExcludesOtherThreads(bool song)
    {
        var scope = new ApplicationDatabaseScope();
        IDisposable outer = Enter(scope, song);
        IDisposable? inner = null;
        try
        {
            inner = TryEnter(scope, song, TimeSpan.Zero);
            Assert.IsNotNull(inner);
            Assert.IsFalse(CanEnterOnOtherThread(scope, song));
            outer.Dispose();
            outer.Dispose();
            Assert.IsFalse(CanEnterOnOtherThread(scope, song));
        }
        finally
        {
            inner?.Dispose();
            outer.Dispose();
        }
        Assert.IsTrue(CanEnterOnOtherThread(scope, song));
    }

    /// <summary>songとscore、および異なるcompositionのgateは独立し、無関係なDB処理を待たせません。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Gate_SeparatesDatabaseKindsAndScopeInstances(bool song)
    {
        var first = new ApplicationDatabaseScope();
        var second = new ApplicationDatabaseScope();
        using (Enter(first, song))
        {
            Assert.IsTrue(CanEnterOnOtherThread(first, !song));
            Assert.IsTrue(CanEnterOnOtherThread(second, song));
        }
    }

    /// <summary>全種類の読取り専用factoryは他threadのwrite gate中にも実SQLを実行し、接続数を解放します。</summary>
    [TestMethod]
    public void ReadOnlyFactories_DoNotAcquireWriteGates()
    {
        using var files = new DatabaseFiles();
        var scope = new ApplicationDatabaseScope();
        using (scope.OpenProviderConnection(files.Song)) { }
        using (scope.OpenProviderConnection(files.Score)) { }
        IDisposable songGate = scope.EnterSong();
        IDisposable scoreGate = scope.EnterScore();
        Task? worker = null;
        try
        {
            worker = Task.Factory.StartNew(() =>
            {
                using LR2SongDBExtended song = scope.OpenSongReadOnly(files.Song);
                using LR2ScoreDBExtended score = scope.OpenScoreReadOnly(files.Score);
                using SQLiteConnectionEx general = scope.OpenReadOnly(files.Song);
                Assert.IsTrue(song.IsReadOnlyConnection);
                Assert.IsTrue(score.IsReadOnlyConnection);
                Assert.AreEqual(TimeSpan.FromMilliseconds(60000), song.BusyTimeout);
                Assert.AreEqual(3, scope.ActiveConnectionCount);
                Assert.AreEqual(1, song.ExecuteScalar<int>("SELECT 1;"));
                Assert.AreEqual(1, score.ExecuteScalar<int>("SELECT 1;"));
                Assert.AreEqual(1, general.ExecuteScalar<int>("SELECT 1;"));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(5)), "読取り専用openがwrite gateを待機しています。");
        }
        finally
        {
            scoreGate.Dispose();
            songGate.Dispose();
            worker?.GetAwaiter().GetResult();
        }
        Assert.AreEqual(0, scope.ActiveConnectionCount);
        Assert.IsTrue(scope.WaitForConnectionsIdleAsync().IsCompletedSuccessfully);
    }

    /// <summary>実factoryのopen・パス検査・初期化失敗は元の例外を返して寿命を回収し、次の明示openを妨げません。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void WriteFactory_FailuresReleaseConnectionsAndGateForNextOpen(bool song)
    {
        using var files = new DatabaseFiles();
        var scope = new ApplicationDatabaseScope();
        string path = song ? files.Song : files.Score;
        string missingDirectory = Path.Combine(files.Root, "missing", Path.GetFileName(path));
        Assert.ThrowsExactly<SQLiteException>(() => OpenWrite(scope, song, missingDirectory));
        AssertReleased(scope, song);

        string invalidPath = Path.Combine(files.Root, song ? "other.db" : "score.txt");
        Assert.ThrowsExactly<FileNotFoundException>(() => OpenWrite(scope, song, invalidPath));
        AssertReleased(scope, song);
        File.Delete(invalidPath);

        var failure = new InvalidOperationException("initialize failure");
        InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() => OpenWrite(scope, song, path, connection =>
        {
            Assert.AreEqual(1, scope.ActiveConnectionCount);
            Assert.AreEqual(TimeSpan.FromMilliseconds(60000), connection.BusyTimeout);
            Assert.IsFalse(CanEnterOnOtherThread(scope, song));
            throw failure;
        }));
        Assert.AreSame(failure, actual);
        AssertReleased(scope, song);
        using (SQLiteConnectionEx next = OpenWrite(scope, song, path))
        {
            Assert.AreEqual(1, next.ExecuteScalar<int>("SELECT 1;"));
        }
        AssertReleased(scope, song);
        Assert.AreEqual(0, scope.SqliteCloseFailureCount);
    }

    /// <summary>最後の実接続closeだけがidle待機を完了し、次の接続群は未完了の待機を新たに作ります。</summary>
    [TestMethod]
    public void ConnectionsIdle_CompletesAfterLastCloseAndTracksNextGroup()
    {
        var scope = new ApplicationDatabaseScope();
        Assert.IsTrue(scope.WaitForConnectionsIdleAsync().IsCompletedSuccessfully);
        using SQLiteConnectionEx first = scope.OpenProviderConnection(":memory:");
        using SQLiteConnectionEx second = scope.OpenProviderConnection(":memory:");
        Task idle = scope.WaitForConnectionsIdleAsync();
        Assert.AreEqual(2, scope.ActiveConnectionCount);
        Assert.IsFalse(idle.IsCompleted);
        first.Dispose();
        first.Dispose();
        Assert.AreEqual(1, scope.ActiveConnectionCount);
        Assert.IsFalse(idle.IsCompleted);
        second.Dispose();
        Assert.AreEqual(0, scope.ActiveConnectionCount);
        Assert.IsTrue(idle.IsCompletedSuccessfully);
        using SQLiteConnectionEx next = scope.OpenProviderConnection(":memory:");
        Task nextIdle = scope.WaitForConnectionsIdleAsync();
        Assert.IsFalse(nextIdle.IsCompleted);
        Assert.IsTrue(idle.IsCompletedSuccessfully);
        next.Dispose();
        Assert.IsTrue(nextIdle.IsCompletedSuccessfully);
    }

    /// <summary>close中は接続数とgateを保持し、実SQLite close後の同一例外を一度だけ記録してidleとgateを解放します。</summary>
    [TestMethod]
    public void ConnectionLease_CloseFailurePreservesExceptionAndReleasesExactlyOnce()
    {
        var scope = new ApplicationDatabaseScope();
        using var native = new SQLiteConnection(":memory:");
        ApplicationDatabaseScope.ConnectionLease lease = scope.AcquireConnectionLease(scope.EnterSong());
        Task idle = scope.WaitForConnectionsIdleAsync();
        var failure = new InvalidOperationException("close failure");
        int callbacks = 0;
        try
        {
            InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() => lease.Close(() =>
            {
                callbacks++;
                Assert.AreEqual(1, scope.ActiveConnectionCount);
                Assert.IsFalse(idle.IsCompleted);
                Assert.IsFalse(CanEnterOnOtherThread(scope, true));
                native.Dispose();
                throw failure;
            }));
            Assert.AreSame(failure, actual);
            Assert.AreEqual(1, scope.SqliteCloseFailureCount);
            AssertReleased(scope, true);
            Assert.IsTrue(idle.IsCompletedSuccessfully);
            lease.Close(() => callbacks++);
            Assert.AreEqual(1, callbacks);
            Assert.AreEqual(1, scope.SqliteCloseFailureCount);
            Assert.AreEqual(0, scope.ActiveConnectionCount);
        }
        finally { lease.Close(native.Dispose); }
    }

    private static IDisposable Enter(ApplicationDatabaseScope scope, bool song) => song ? scope.EnterSong() : scope.EnterScore();

    private static IDisposable? TryEnter(ApplicationDatabaseScope scope, bool song, TimeSpan timeout) =>
        song ? scope.TryEnterSong(timeout) : scope.TryEnterScore(timeout);

    private static bool CanEnterOnOtherThread(ApplicationDatabaseScope scope, bool song) => OnOtherThread(() =>
    {
        using IDisposable? lease = TryEnter(scope, song, TimeSpan.FromMilliseconds(30));
        return lease is not null;
    });

    private static T OnOtherThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .GetAwaiter().GetResult();

    private static SQLiteConnectionEx OpenWrite(ApplicationDatabaseScope scope, bool song, string path,
        Action<SQLiteConnectionEx>? initialize = null) => song
        ? scope.OpenSongWrite(path, initialize is null ? null : connection => initialize(connection))
        : scope.OpenScoreWrite(path, initialize is null ? null : connection => initialize(connection));

    private static void AssertReleased(ApplicationDatabaseScope scope, bool song)
    {
        Assert.AreEqual(0, scope.ActiveConnectionCount);
        Assert.IsTrue(scope.WaitForConnectionsIdleAsync().IsCompletedSuccessfully);
        Assert.IsTrue(CanEnterOnOtherThread(scope, song));
    }

    private sealed class DatabaseFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DatabaseScope_" + Guid.NewGuid().ToString("N"));
        internal string Song => Path.Combine(Root, "song.db");
        internal string Score => Path.Combine(Root, "score.db");

        internal DatabaseFiles() { Directory.CreateDirectory(Root); }

        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }
}
