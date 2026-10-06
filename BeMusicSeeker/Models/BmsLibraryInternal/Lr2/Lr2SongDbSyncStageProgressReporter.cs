using System;
using System.Diagnostics;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 一回の逐次準備で段階と実対象件数を通知します。対象がある段階の開始と末尾を送出し、途中値は処理を遅らせないよう間引きます。
/// 通知先の例外は保存・入力検査の結果に影響させません。
/// </summary>
internal sealed class Lr2SongDbSyncStageProgressReporter(Action<string, int, int> reporter)
{
    private string stage = string.Empty;
    private long lastReportedAt;

    /// <summary>処理量が確定していない独立した作業を開始します。</summary>
    internal void Begin(string stage)
    {
        this.stage = stage;
        Report(0, 0);
    }

    /// <summary>実対象数が確定した段階を開始します。対象なしは通知せず、再開時は実処理済み数を引き継ぎます。</summary>
    internal void Begin(string stage, int total, int processed = 0)
    {
        this.stage = stage;
        if (total > 0)
        {
            Report(processed, total);
        }
    }

    /// <summary>実処理済み件数を通知します。分母はこの段階の確定した実対象数です。</summary>
    internal void Advance(int processed, int total)
    {
        if (reporter == null || total <= 0)
        {
            return;
        }
        if (processed != total && Stopwatch.GetElapsedTime(lastReportedAt).TotalMilliseconds < 150)
        {
            return;
        }
        Report(processed, total);
    }

    private void Report(int processed, int total)
    {
        if (reporter == null)
        {
            return;
        }
        lastReportedAt = Stopwatch.GetTimestamp();
        try
        {
            reporter?.Invoke(stage, processed, total);
        }
        catch
        {
            // 表示失敗で準備・保存・元の失敗を置き換えない。
        }
    }
}
