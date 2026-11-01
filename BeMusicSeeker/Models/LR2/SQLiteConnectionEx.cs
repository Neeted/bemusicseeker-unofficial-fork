#nullable enable

using System;
using System.Collections.Generic;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

/// <summary>ownerに属するSQLite接続の終了と読取り設定を提供します。SQL・Commitの失敗は外側で再試行せず呼出し元へ返します。</summary>
/// <remarks>Commit失敗後はproviderが内部transaction状態を解放するため、再呼出しで失敗を成功へ変換しません。</remarks>
public class SQLiteConnectionEx : SQLiteConnection
{
    private readonly ApplicationDatabaseScope.ConnectionLease? connectionLease;

    /// <summary>ownerがnative open前に取得した必須leaseを接続へ引き継ぎます。通常の生成はownerのfactoryを使います。</summary>
    internal SQLiteConnectionEx(string databasePath, SQLiteOpenFlags openFlags,
        ApplicationDatabaseScope.ConnectionLease lease, bool storeDateTimeAsTicks = true)
        : base(databasePath, openFlags, storeDateTimeAsTicks)
    {
        connectionLease = lease;
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

    /// <summary>native closeの結果をownerへ返し、close後に接続leaseとwrite gateを解放します。</summary>
    protected override void Dispose(bool disposing)
    {
        // providerのbase constructorが失敗した部分生成オブジェクトにもfinalizerが走る。
        // leaseはfactoryが回収済みなので、ここではprovider自身の後始末だけを行う。
        if (connectionLease is null)
        {
            base.Dispose(disposing);
            return;
        }
        connectionLease.Close(() => base.Dispose(disposing));
    }
}
