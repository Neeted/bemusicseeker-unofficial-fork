#nullable enable
using System;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioControl
{
    /// <summary>明示test sessionだけで、外部shell要求を記録する窓口です。<c>ApplicationComposition</c>へ注入し、processを起動させません。</summary>
    internal ApplicationScenarioExternalShell ExternalShell { get; } = new();

    /// <summary>更新で終了へ向かう途中でも、受領済みの応答を失わず配送する要求かどうかを返します。</summary>
    private static bool KeepsResponseThroughShutdown(ScenarioCommand request)
        => request is ScenarioPlaylistSqlCommand or ScenarioSettingsSectionCommand or ScenarioStartupUpdateCommand;

    /// <summary>
    /// 更新確認の保持済みの実終端を返します。未開始なら失敗し、実行中なら終端まで待ちます。
    /// </summary>
    /// <remarks>
    /// 購読ではなく所有者が保持する状態と受領を読むため、要求が開始・終端より先でも後でも同じ受領を返します。
    /// 終端種別を既定値や<c>NoUpdate</c>で補いません。
    /// </remarks>
    private async Task<ScenarioStartupUpdateCompleted> ObserveStartupUpdateAsync(MainWindowViewModel model, long requestId)
    {
        StartupUpdateWorkflowOwner owner = model.StartupUpdateWorkflow;
        StartupUpdateWorkflowState state = owner.CaptureState(out StartupUpdateWorkflowCompletionReceipt? receipt);
        if (state == StartupUpdateWorkflowState.NotStarted)
        { throw new InvalidOperationException("The startup update workflow has not started."); }
        if (state == StartupUpdateWorkflowState.Running)
        {
            try { await owner.WaitForTerminalAsync().WaitAsync(stopping.Token); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // 終端後に通信終了が重なっても、確定済みの受領は失いません。受領がなければ取消として伝えます。
                if (owner.CaptureState(out _) != StartupUpdateWorkflowState.Terminal) { throw; }
            }
            state = owner.CaptureState(out receipt);
        }
        if (state != StartupUpdateWorkflowState.Terminal || receipt is null)
        { throw new InvalidOperationException("The startup update workflow terminal receipt is unavailable."); }
        return new(requestId, receipt.Outcome, receipt.ShutdownPrepared,
            receipt.Exception is null ? null : ScenarioFailure.FromException(receipt.Exception), ExternalShell.Capture());
    }
}
