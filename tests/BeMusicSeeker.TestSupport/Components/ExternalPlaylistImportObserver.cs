using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 受付で開始した取込みの実Taskを観測し、通知と集計の表示の依頼までを含む終端の結果（集計と通知の領収書）を返します。
/// 取込みは、受付（P）を解放した後に通知と集計の表示を依頼してから完了するため、受付の完了ではなく、この実Taskの終端を待ちます。
/// </summary>
internal sealed class ExternalPlaylistImportObserver : IDisposable
{
    private readonly PlaylistWorkspaceViewModel workspace;
    private readonly object gate = new();
    private readonly List<Task<ExternalPlaylistImportCompletion>> operations = [];

    /// <summary>作業領域の取込みの開始を購読します。</summary>
    internal ExternalPlaylistImportObserver(PlaylistWorkspaceViewModel workspace)
    {
        this.workspace = workspace;
        workspace.ExternalPlaylistImportStarted += OnStarted;
    }

    /// <summary>最後に開始した取込みの実Taskです。開始していなければnullです。</summary>
    internal Task<ExternalPlaylistImportCompletion>? LastOperation
    {
        get { lock (gate) { return operations.Count == 0 ? null : operations[^1]; } }
    }

    /// <summary>開始した取込みの数です。</summary>
    internal int StartedCount
    {
        get { lock (gate) { return operations.Count; } }
    }

    /// <summary>最後に開始した取込みの終端を待ち、集計を返します。取込みが開始していなければ失敗にします。</summary>
    internal async Task<ExternalPlaylistImportQueueSummary> LastSummaryAsync(CancellationToken cancellationToken = default)
    {
        Task<ExternalPlaylistImportCompletion> operation = LastOperation
            ?? throw new AssertFailedException("No external playlist import was started.");
        ExternalPlaylistImportCompletion completion = await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        return completion.Summary ?? throw new AssertFailedException("The import ended without a summary.");
    }

    /// <summary>これまでに観測した開始を忘れ、次の取込みだけを数え直します。</summary>
    internal void Reset()
    {
        lock (gate) { operations.Clear(); }
    }

    private void OnStarted(Task<ExternalPlaylistImportCompletion> operation)
    {
        lock (gate) { operations.Add(operation); }
    }

    public void Dispose() => workspace.ExternalPlaylistImportStarted -= OnStarted;
}
