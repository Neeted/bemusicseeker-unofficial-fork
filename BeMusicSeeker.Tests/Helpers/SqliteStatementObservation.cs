using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Tests.Helpers;

/// <summary>
/// 個別の実SQLite接続に対するtrace_v2の観測を、テストの接続lifecycle内だけで行います。
/// </summary>
internal sealed class SqliteStatementObservation : IDisposable
{
    private const uint SqliteTraceStatement = 0x01;
    private const uint SqliteTraceProfile = 0x02;
    private const uint SqliteTraceRow = 0x04;
    private const int SqliteStmtstatusFullscanStep = 1;
    private const int SqliteStmtstatusVmStep = 4;

    private readonly object syncRoot = new();
    private readonly Dictionary<IntPtr, MutableStatement> activeStatements = [];
    private readonly List<SQLitePCL.sqlite3> handles = [];
    private readonly List<MutableStatement> statements = [];
    private readonly TraceV2Callback traceCallback;
    private Exception? callbackException;
    private bool disposed;

    /// <summary>
    /// 接続単位のSQLite statement観測を準備します。
    /// </summary>
    internal SqliteStatementObservation()
    {
        traceCallback = Trace;
    }

    /// <summary>
    /// trace_v2を登録した実 song.db 接続を生成します。
    /// </summary>
    /// <param name="databasePath">観測対象の song.db パス。</param>
    /// <returns>呼出し側がdisposeを所有する実接続。</returns>
    internal LR2SongDBExtended OpenSongDb(string databasePath)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteStatementObservation));
        }

        LR2SongDBExtended connection = new(databasePath);
        SQLitePCL.sqlite3 handle = connection.Handle;
        try
        {
            int result = sqlite3_trace_v2(
                handle.DangerousGetHandle(),
                SqliteTraceStatement | SqliteTraceProfile | SqliteTraceRow,
                traceCallback,
                IntPtr.Zero);
            if (result != 0)
            {
                throw new InvalidOperationException("sqlite3_trace_v2 の登録に失敗しました: " + result);
            }
            lock (syncRoot)
            {
                handles.Add(handle);
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// これまでに観測したSQL、返却行数、PROFILE計測値のsnapshotを返します。
    /// </summary>
    internal IReadOnlyList<SqliteObservedStatement> Statements
    {
        get
        {
            lock (syncRoot)
            {
                return [.. statements.Select(statement => new SqliteObservedStatement(
                    statement.Sql,
                    statement.RowCount,
                    statement.FullScanSteps,
                    statement.VmSteps,
                    statement.ProfileCount))];
            }
        }
    }

    /// <summary>
    /// 条件に一致するSQLが返した行数を合計します。
    /// </summary>
    /// <param name="statementFilter">集計対象SQLの判定。</param>
    /// <returns>一致するSQLの返却行数合計。</returns>
    internal int CountReturnedRows(Func<string, bool> statementFilter)
    {
        if (statementFilter == null)
        {
            throw new ArgumentNullException(nameof(statementFilter));
        }
        return Statements
            .Where(statement => statementFilter(statement.Sql))
            .Sum(statement => statement.RowCount);
    }

    /// <summary>
    /// PROFILE callbackで記録した全statementのFULLSCAN_STEPを合計します。
    /// </summary>
    /// <returns>観測した全statementのFULLSCAN_STEP合計。</returns>
    internal int CountFullScanSteps()
    {
        return Statements.Sum(statement => statement.FullScanSteps);
    }

    /// <summary>
    /// PROFILE callbackで記録した全statementのVM_STEPを合計します。
    /// </summary>
    /// <returns>観測した全statementのVM_STEP合計。</returns>
    internal int CountVmSteps()
    {
        return Statements.Sum(statement => statement.VmSteps);
    }

    /// <summary>
    /// 観測した対象表SQLを同じDBの実接続でquery planへ通し、背景表のscanを確認できるようにします。
    /// </summary>
    internal IReadOnlyList<string> ExplainCatalogQueryPlans(string databasePath)
    {
        ThrowIfCallbackFailed();
        IReadOnlyList<SqliteObservedStatement> observed = Statements;
        using var connection = new LR2SongDBExtended(databasePath);
        foreach (SqliteObservedStatement statement in observed.Where(statement => IsTempTableCreate(statement.Sql)))
        {
            connection.Execute(statement.Sql);
        }

        List<string> details = [];
        foreach (SqliteObservedStatement statement in observed.Where(statement => IsCatalogQuery(statement.Sql)))
        {
            string sql = TrimTerminator(statement.Sql);
            foreach (QueryPlanRow row in connection.Query<QueryPlanRow>("EXPLAIN QUERY PLAN " + sql))
            {
                if (!string.IsNullOrWhiteSpace(row.detail))
                {
                    details.Add(row.detail);
                }
            }
        }
        return details;
    }

    /// <summary>
    /// native callback内で捕捉した例外を検証側へ再通知します。
    /// </summary>
    internal void ThrowIfCallbackFailed()
    {
        lock (syncRoot)
        {
            if (callbackException != null)
            {
                throw new InvalidOperationException("SQLite trace callback が失敗しました。", callbackException);
            }
        }
    }

    /// <summary>
    /// trace_v2の登録を解除します。
    /// </summary>
    /// <remarks>
    /// 観測対象接続のdispose所有権は呼出し側に残し、closedまたはinvalidになったhandleへはアクセスしません。
    /// </remarks>
    public void Dispose()
    {
        List<SQLitePCL.sqlite3> openHandles;
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            openHandles = [.. handles];
        }

        foreach (SQLitePCL.sqlite3 handle in openHandles)
        {
            try
            {
                if (!handle.IsClosed && !handle.IsInvalid)
                {
                    sqlite3_trace_v2(
                        handle.DangerousGetHandle(),
                        0,
                        null,
                        IntPtr.Zero);
                }
            }
            catch (Exception exception)
            {
                lock (syncRoot)
                {
                    callbackException ??= exception;
                }
            }
        }
    }

    private int Trace(uint traceCode, IntPtr context, IntPtr statementHandle, IntPtr payload)
    {
        try
        {
            lock (syncRoot)
            {
                if (traceCode == SqliteTraceStatement)
                {
                    var statement = new MutableStatement(
                        payload == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(payload) ?? string.Empty);
                    activeStatements[statementHandle] = statement;
                    statements.Add(statement);
                }
                else if (traceCode == SqliteTraceRow
                    && activeStatements.TryGetValue(statementHandle, out MutableStatement? statement))
                {
                    statement.RowCount++;
                }
                else if (traceCode == SqliteTraceProfile
                    && activeStatements.TryGetValue(statementHandle, out MutableStatement? profiledStatement))
                {
                    profiledStatement.FullScanSteps += sqlite3_stmt_status(
                        statementHandle,
                        SqliteStmtstatusFullscanStep,
                        1);
                    profiledStatement.VmSteps += sqlite3_stmt_status(
                        statementHandle,
                        SqliteStmtstatusVmStep,
                        1);
                    profiledStatement.ProfileCount++;
                }
            }
        }
        catch (Exception exception)
        {
            lock (syncRoot)
            {
                callbackException ??= exception;
            }
        }
        return 0;
    }

    private static bool IsTempTableCreate(string sql)
    {
        return sql.StartsWith("CREATE TEMP TABLE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCatalogQuery(string sql)
    {
        if (!(sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return sql.Contains(" song ", StringComparison.OrdinalIgnoreCase)
            || sql.Contains(" bmson_song ", StringComparison.OrdinalIgnoreCase)
            || sql.Contains(" maintenance ", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTerminator(string sql)
    {
        return (sql ?? string.Empty).Trim().TrimEnd(';').Trim();
    }

    private sealed class MutableStatement(string sql)
    {
        internal string Sql { get; } = sql;

        internal int RowCount { get; set; }

        internal int FullScanSteps { get; set; }

        internal int VmSteps { get; set; }

        internal int ProfileCount { get; set; }
    }

    /// <summary>
    /// SQLite statementのSQL、返却行数、PROFILE計測値を表す観測snapshotです。
    /// </summary>
    internal sealed class SqliteObservedStatement(
        string sql,
        int rowCount,
        int fullScanSteps,
        int vmSteps,
        int profileCount)
    {
        /// <summary>観測したSQL文字列。</summary>
        internal string Sql { get; } = sql;

        /// <summary>trace row callbackで観測した返却行数。</summary>
        internal int RowCount { get; } = rowCount;

        /// <summary>PROFILE完了時に取得したFULLSCAN_STEP合計。</summary>
        internal int FullScanSteps { get; } = fullScanSteps;

        /// <summary>PROFILE完了時に取得したVM_STEP合計。</summary>
        internal int VmSteps { get; } = vmSteps;

        /// <summary>PROFILE callbackを受けた回数。</summary>
        internal int ProfileCount { get; } = profileCount;
    }

    private sealed class QueryPlanRow
    {
        public string detail { get; set; } = string.Empty;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TraceV2Callback(uint traceCode, IntPtr context, IntPtr statement, IntPtr payload);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_trace_v2")]
    private static extern int sqlite3_trace_v2(
        IntPtr database,
        uint mask,
        TraceV2Callback? callback,
        IntPtr context);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_stmt_status")]
    private static extern int sqlite3_stmt_status(IntPtr statement, int operation, int resetFlag);
}
