using System;
using System.Threading;

namespace BeMusicSeeker.Models;

internal static class ShutdownOperationTracker
{
    private static int activeSqliteConnectionCount;

    private static int sqliteCloseFailureCount;

    internal static int ActiveSqliteConnectionCount => Volatile.Read(ref activeSqliteConnectionCount);

    internal static int SqliteCloseFailureCount => Volatile.Read(ref sqliteCloseFailureCount);

    internal static bool HasSqliteCloseFailure => SqliteCloseFailureCount != 0;

    internal static IDisposable TrackSqliteConnection()
    {
        Interlocked.Increment(ref activeSqliteConnectionCount);
        return new Lease();
    }

    internal static void RecordSqliteCloseFailure()
    {
        Interlocked.Increment(ref sqliteCloseFailureCount);
    }

    private sealed class Lease : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.Decrement(ref activeSqliteConnectionCount);
            }
        }
    }
}
