using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryFixInstallationResult
{
    /// <summary>
    /// 修復対象ごとの filesystem/catalog receipt を保持します。個別 receipt は
    /// 後段処理が失敗しても失われません。
    /// </summary>
    public FileDbMutationBatchReceipt MutationReceipt { get; set; }

    /// <summary>承認済み重複削除の filesystem/catalog facts です。</summary>
    public LibraryChartRemovalOutcome RemovalOutcome { get; set; }

    /// <summary>receipt 取得後に停止した操作固有の failure です。</summary>
    public Exception Failure { get; set; }

    public List<LibraryChartRef> ChartsToRemove { get; } = [];

    public List<ChartFile> MaintenanceCharts { get; } = [];

    public List<LibraryDeleteFailure> Failures { get; } = [];

    public int RequestedCount { get; set; }

    public int MovedCount { get; set; }

    public int DuplicateSkippedCount { get; set; }

    public long TotalMs { get; set; }

    internal bool HasDurableFinalizationFailure => MutationReceipt?.HasDurableFinalizationFailure == true;
}
