using System.Windows;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// プレイリスト側の操作の通知が、表示窓口へ依頼される時点の状態を決定的に記録する窓口の補助です。
/// 本番の <c>UiDialogCoordinator</c> と同じく、UIの文脈から呼ばれたときだけ同期にブロックし、ほかのスレッドからは
/// 未完了のTaskを返します。ブロックした事実が「UIの文脈から依頼した誤実装」の検出になります。
/// </summary>
internal sealed class PlaylistNoticeProbe : IDisposable
{
    private readonly Func<bool> isUiContext;
    private readonly ManualResetEventSlim release = new(false);
    private readonly object gate = new();
    private readonly List<TaskCompletionSource<UiDialogResult>> held = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> waiters = [];
    private int requestCount;
    private bool blockedOnUiContext;

    /// <summary>窓口の呼出しがUIの文脈かを判定する関数で窓口を作ります。</summary>
    /// <param name="isUiContext">UIの文脈の判定。Windowsは Dispatcher.CheckAccess、Coreは単一スレッドの同期の文脈との一致を渡します。</param>
    internal PlaylistNoticeProbe(Func<bool> isUiContext)
    {
        this.isUiContext = isUiContext;
        Dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService { MessageTaskFactory = Answer };
    }

    /// <summary>記録する窓口です。ワークスペースの通知窓口として渡します。</summary>
    internal PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService Dialogs { get; }

    /// <summary>真の間、UIの文脈の外から届いた通知を未完了のTaskで返します。偽なら完了済みの結果で返します。</summary>
    internal bool HoldDisplay { get; set; }

    /// <summary>設定すると、通知ごとの窓口の応答を決めます。引数は何件目の依頼かと要求です。未完了・失敗・同期の例外の形を表せます。</summary>
    internal Func<int, UiMessageRequest, Task<UiDialogResult>>? Behavior { get; set; }

    /// <summary>依頼が届いた時点で、窓口の呼出し中に同期で呼ばれます。受付の解放後かどうかなど、依頼の時点の状態の観測に使います。</summary>
    internal Action<UiMessageRequest>? Requested { get; set; }

    /// <summary>UIの文脈から呼ばれて同期にブロックした事実があるかを返します。</summary>
    internal bool BlockedOnUiContext => Volatile.Read(ref blockedOnUiContext);

    /// <summary>届いた通知の要求を到達順に返します。</summary>
    internal IReadOnlyList<UiMessageRequest> Messages => Dialogs.MessageRequests;

    /// <summary>指定した件数以上の依頼が届くまで待ちます。到達は窓口の呼出しが通知し、固定の待ちは使いません。</summary>
    internal Task WaitForRequestsAsync(int count)
    {
        lock (gate)
        {
            if (requestCount >= count) { return Task.CompletedTask; }
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((count, signal));
            return signal.Task;
        }
    }

    /// <summary>保持している通知をすべて、指定した結果で完了させます。</summary>
    internal void CompleteHeld(UiDialogResult? result = null)
    {
        TaskCompletionSource<UiDialogResult>[] pending;
        lock (gate) { pending = [.. held]; held.Clear(); }
        foreach (TaskCompletionSource<UiDialogResult> item in pending)
        {
            item.TrySetResult(result ?? UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }
    }

    private Task<UiDialogResult> Answer(UiMessageRequest request)
    {
        int index;
        lock (gate) { index = requestCount; }
        try
        {
            Requested?.Invoke(request);
            if (isUiContext())
            {
                Volatile.Write(ref blockedOnUiContext, true);
                release.Wait();
                return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            }
            if (Behavior is { } behavior) { return behavior(index, request); }
            if (!HoldDisplay) { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
            var pending = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { held.Add(pending); }
            return pending.Task;
        }
        finally
        {
            Signal();
        }
    }

    private void Signal()
    {
        List<TaskCompletionSource> ready = [];
        lock (gate)
        {
            requestCount++;
            for (int i = waiters.Count - 1; i >= 0; i--)
            {
                if (requestCount >= waiters[i].Count)
                {
                    ready.Add(waiters[i].Signal);
                    waiters.RemoveAt(i);
                }
            }
        }
        foreach (TaskCompletionSource signal in ready) { signal.TrySetResult(); }
    }

    /// <summary>UIの文脈でブロックした呼出しを解放し、保持した通知を完了させます。試験の終端で必ず呼びます。</summary>
    public void Dispose()
    {
        release.Set();
        CompleteHeld();
        release.Dispose();
    }
}
