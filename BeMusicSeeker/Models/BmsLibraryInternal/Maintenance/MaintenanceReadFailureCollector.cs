using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>保守の読込みに失敗した対象の事実です。表示は文言を持たず、表示時の言語で整形します。</summary>
/// <param name="Path">読込みに失敗した譜面のパス。</param>
/// <param name="ExceptionType">失敗した例外の型名。</param>
/// <param name="Message">捕捉した時点の例外メッセージ。</param>
internal sealed record MaintenanceReadFailure(string Path, string ExceptionType, string Message);

/// <summary>
/// 並列の読取りスレッドが見つけた読込み失敗を、窓口へ触れずに集めます。件数は全件を数え、
/// 標本は対象の入力順で先頭から上限件だけ保持するため、読取りの完了順に依存せず、失敗が大量でもメモリは増えません。
/// 全件のパスと原因は、一定件数ごとに一つのログ項目へまとめて残します。
/// </summary>
/// <remarks>
/// 呼出元が操作の例外経路でも回収できるよう、結果の型とは別に明示の引数で受け取れます。
/// 書込みは並列のスレッドから行われるため、状態はすべて一つのロックで守ります。
/// </remarks>
internal sealed class MaintenanceReadFailureCollector
{
    /// <summary>表示へ渡す標本の最大件数です。</summary>
    internal const int MaximumSampleCount = 5;

    /// <summary>一つのログ項目へまとめる最大件数です。失敗が大量でも件数ごとの同期ログにしません。</summary>
    private const int LogBatchSize = 1000;

    private readonly object gate = new();
    private readonly List<(int TargetIndex, MaintenanceReadFailure Failure)> samples = [];
    private List<MaintenanceReadFailure> pendingLog = [];
    private int count;

    /// <summary>これまでに数えた失敗の全件数です。</summary>
    internal int Count => Volatile.Read(ref count);

    /// <summary>対象の入力順に並べた標本です。最大 <see cref="MaximumSampleCount"/> 件です。</summary>
    internal IReadOnlyList<MaintenanceReadFailure> Samples
    {
        get
        {
            lock (gate)
            {
                return [.. samples.Select(sample => sample.Failure)];
            }
        }
    }

    /// <summary>失敗を一件追加します。複数のスレッドから呼べます。</summary>
    /// <param name="targetIndex">保守の対象一覧での位置。標本の順序に使います。</param>
    internal void Add(int targetIndex, string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var failure = new MaintenanceReadFailure(path, exception.GetType().FullName, exception.Message);
        List<MaintenanceReadFailure> logBatch = null;
        lock (gate)
        {
            count++;
            int insertAt = samples.Count;
            while (insertAt > 0 && samples[insertAt - 1].TargetIndex > targetIndex)
            {
                insertAt--;
            }
            if (insertAt < MaximumSampleCount)
            {
                samples.Insert(insertAt, (targetIndex, failure));
                if (samples.Count > MaximumSampleCount)
                {
                    samples.RemoveAt(samples.Count - 1);
                }
            }
            pendingLog.Add(failure);
            if (pendingLog.Count >= LogBatchSize)
            {
                logBatch = pendingLog;
                pendingLog = [];
            }
        }
        WriteLog(logBatch);
    }

    /// <summary>未出力の失敗を一つのログ項目として出力します。読取りの終了後に呼びます。</summary>
    internal void FlushLog()
    {
        List<MaintenanceReadFailure> logBatch;
        lock (gate)
        {
            logBatch = pendingLog;
            pendingLog = [];
        }
        WriteLog(logBatch);
    }

    private void WriteLog(List<MaintenanceReadFailure> failures)
    {
        if (failures == null || failures.Count == 0)
        {
            return;
        }
        try
        {
            StringBuilder text = new StringBuilder("maintenance_read_failed count=").Append(failures.Count)
                .Append(" total=").Append(Count);
            foreach (MaintenanceReadFailure failure in failures)
            {
                text.AppendLine().Append("path=").Append(failure.Path)
                    .Append(" type=").Append(failure.ExceptionType)
                    .Append(" message=").Append((failure.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '));
            }
            NLogWrapper.FileLogger?.Warn(text.ToString());
        }
        catch
        {
            // 任意診断の失敗で、保守の読取りと結果の収集を止めません。
        }
    }
}
