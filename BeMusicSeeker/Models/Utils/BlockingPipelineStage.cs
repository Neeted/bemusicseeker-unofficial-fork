using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// 操作の間ずっとキューの受取り・追加・同期待機でブロックする、パイプライン段を専用スレッドで開始します。
/// </summary>
/// <remarks>
/// 走査・同期・保守などのパイプラインは、一回の操作で段の数だけスレッドを同期的に占有します。
/// スレッドプールで開始すると、複数の操作や呼出元の待機が重なったときに、プールの増加速度でしか後続の段が開始されず停滞します。
/// 段は操作全体の寿命を持つ少数の長時間処理であり、<see cref="TaskCreationOptions.LongRunning"/> で専用スレッドに置きます。
/// 段の内部で短い並列ループを使う場合は、従来どおりスレッドプールを使います。
/// </remarks>
internal static class BlockingPipelineStage
{
    private const TaskCreationOptions Options = TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach;

    /// <summary>結果を返さない同期の段を開始します。例外は返したタスクへ保持します。</summary>
    /// <remarks><see cref="Task.Run(Action)"/> と違い非同期デリゲートを展開しないため、同期の段だけを渡します。</remarks>
    internal static Task Start(Action stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return Task.Factory.StartNew(stage, CancellationToken.None, Options, TaskScheduler.Default);
    }
}
