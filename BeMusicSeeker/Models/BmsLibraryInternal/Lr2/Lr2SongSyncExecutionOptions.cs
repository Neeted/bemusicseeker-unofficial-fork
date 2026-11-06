using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一回の楽曲同期で使う有限の処理幅です。保存形式と同期結果の意味を変えず、同じpipelineの実行資源だけを指定します。</summary>
internal sealed class Lr2SongSyncExecutionOptions
{
    /// <summary>実transactionでまとめて確定する楽曲行数です。</summary>
    internal int ChunkSize { get; }
    /// <summary>詳細解析を担当するworker数です。実対象数を上限に起動します。</summary>
    internal int WorkerCount { get; }
    /// <summary>読取り済み入力の有限queue容量です。</summary>
    internal int ReadQueueCapacity { get; }
    /// <summary>解析済み結果の有限queue容量です。</summary>
    internal int ComputedQueueCapacity { get; }
    /// <summary>順序回収待ちの入力数の上限です。slotはcommit前の順序回収で返します。</summary>
    internal int OrderingWindowCapacity { get; }

    /// <summary>すべて正の実行幅を指定します。未指定時の本番既定値は同期サービスが対象件数とCPU数から決定します。</summary>
    /// <remarks>
    /// 順序枠はチャンクの確定前の順序回収で返すため、順序枠の容量をチャンク幅以上にするなどの相互の大小制約は設けず、
    /// 各値が正であることだけを検査します。
    /// </remarks>
    internal Lr2SongSyncExecutionOptions(int chunkSize, int workerCount, int readQueueCapacity,
        int computedQueueCapacity, int orderingWindowCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(computedQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(orderingWindowCapacity);
        ChunkSize = chunkSize;
        WorkerCount = workerCount;
        ReadQueueCapacity = readQueueCapacity;
        ComputedQueueCapacity = computedQueueCapacity;
        OrderingWindowCapacity = orderingWindowCapacity;
    }
}
