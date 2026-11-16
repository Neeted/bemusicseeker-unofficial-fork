#nullable enable
using System.Collections.Immutable;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Runtime;

/// <summary>起動時の更新確認が終端した実receiptを要求します。未開始なら失敗し、終端種別を補いません。</summary>
internal sealed record ScenarioStartupUpdateCommand(long RequestId) : ScenarioCommand(RequestId);

/// <summary>このprocessの一回の更新確認の実終端と、その時点までの外部shell要求の記録です。</summary>
/// <param name="RequestId">対応する要求の通信番号です。</param>
/// <param name="Outcome">更新確認の実終端種別です。</param>
/// <param name="ShutdownPrepared">終了準備を完了したうえでの終端かどうかです。</param>
/// <param name="Failure">終端が失敗を伴う場合の実例外です。</param>
/// <param name="ExternalShell">応答時点までに記録した外部shell要求を受付順に並べた値です。</param>
internal sealed record ScenarioStartupUpdateCompleted(long RequestId, StartupUpdateWorkflowOutcome Outcome, bool ShutdownPrepared,
    ScenarioFailure? Failure, ImmutableArray<ScenarioExternalShellRecord> ExternalShell) : ScenarioMessage(RequestId);

/// <summary>記録窓口が受け付けた外部shell要求です。</summary>
/// <param name="Operation">窓口の操作名です。</param>
/// <param name="Kind">Open要求の種別です。他の操作ではnullです。</param>
/// <param name="Target">要求された値です。</param>
internal sealed record ScenarioExternalShellRecord(string Operation, string? Kind, string Target);
