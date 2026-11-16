using System.Windows;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 所有者に注入する窓口の偽物です。操作の通知の依頼を、依頼の時点の状況とともに記録します。
/// 本番の窓口は UI の文脈から呼ばれると同期のモーダルとして振る舞うため、UI の文脈からの呼出しは <see cref="Request.OnUiContext"/> に記録し、
/// 表明が「UI の文脈の外から依頼した」ことを検出します（ハングを避けるため、呼出しは止めません）。
/// UI の文脈の外からの呼出しは、<see cref="HoldDisplay"/> が真なら表示の完了を保留した Task を返し、テストが <see cref="ReleaseAll"/> で解放します。
/// </summary>
internal sealed class OperationNoticeRequestProbe : IUiDialogService
{
    private readonly Func<bool> isUiContext;
    private readonly Func<bool> admissionsAreFree;
    private readonly object gate = new();
    private readonly List<Request> requests = [];
    private readonly List<TaskCompletionSource<UiDialogResult>> held = [];

    /// <param name="isUiContext">呼出しの文脈が UI かどうかを返す判定です。</param>
    /// <param name="admissionsAreFree">所有者の論理Lと、その操作が取る対象別Pが、依頼の時点で取得できるかを返す判定です。取得に成功したら直ちに解放します。</param>
    internal OperationNoticeRequestProbe(Func<bool> isUiContext, Func<bool> admissionsAreFree)
    {
        this.isUiContext = isUiContext;
        this.admissionsAreFree = admissionsAreFree;
    }

    /// <summary>真なら、UI の文脈の外からの依頼に表示の完了を保留した Task を返します。</summary>
    internal bool HoldDisplay { get; set; } = true;

    /// <summary>設定すると、依頼ごとにこの動作を先に実行します（同期の例外・失敗した Task の注入に使います）。</summary>
    internal Func<UiMessageRequest, Task<UiDialogResult>?>? Behavior { get; set; }

    /// <summary>依頼の記録です。</summary>
    internal IReadOnlyList<Request> Requests
    {
        get { lock (gate) { return [.. requests]; } }
    }

    /// <summary>依頼の本文のうち、指定の文字列を含むものの件数です。</summary>
    internal int CountContaining(string text) => Requests.Count(request => request.Message.MessageBoxText.Contains(text, StringComparison.Ordinal));

    /// <summary>保留した表示を、すべて完了させます。テストの後始末で必ず呼びます。</summary>
    internal void ReleaseAll()
    {
        TaskCompletionSource<UiDialogResult>[] pending;
        lock (gate)
        {
            pending = [.. held];
            held.Clear();
        }
        foreach (TaskCompletionSource<UiDialogResult> source in pending)
        {
            source.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }
    }

    public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
    {
        bool onUi = isUiContext();
        bool free = admissionsAreFree();
        lock (gate) { requests.Add(new Request(request, onUi, free)); }
        Task<UiDialogResult>? injected = Behavior?.Invoke(request);
        if (injected != null) { return injected; }
        if (onUi || !HoldDisplay)
        {
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }
        var source = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { held.Add(source); }
        return source.Task;
    }

    public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

    public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request,
        CancellationToken cancellationToken = default) where TWindow : Window => throw new NotSupportedException();

    public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>一回の依頼と、依頼の時点の状況です。</summary>
    /// <param name="Message">依頼の内容。</param>
    /// <param name="OnUiContext">UI の文脈から呼ばれたか。</param>
    /// <param name="AdmissionsWereFree">依頼の時点で、論理Lと対象別Pが取得できたか。</param>
    internal sealed record Request(UiMessageRequest Message, bool OnUiContext, bool AdmissionsWereFree);
}
