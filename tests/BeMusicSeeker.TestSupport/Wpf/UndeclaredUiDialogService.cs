using System.Windows;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>
/// 画面テストの構成の既定のダイアログ窓口です。テストが宣言（記録用の窓口の明示的な注入）していない通知・確認・選択・進捗の要求を、
/// 呼ばれた時点で共有ホストの観測記録の項目 <c>D</c> に残し、「表示に失敗した」結果を返します。
/// </summary>
/// <remarks>
/// 採用理由：既定の <c>UiDialogCoordinator</c> は所有者を共有ホストの状態（表示中のモーダル、アクティブなWindow、<c>MainWindow</c>）から解決するため、
/// 結果が <c>OwnerUnavailable</c> で黙って何も表示しないか、実ダイアログを表示するかが共有ホストに依存し、不安定性の原因になります。
/// 製品の通知経路には窓口の例外を捕捉してログだけにする所があるため、例外を投げるだけでは失敗が隠れます。そのため、呼ばれた時点で記録し、
/// 境界（ケースの開始・終了・moduleの終了）の判定（<see cref="HostObservationVerdict"/>）がケースの失敗にします。
/// 実画面は作りません。<c>ShowWindowAsync</c> のWindow生成・結果生成と <c>RunWithProgressAsync</c> の処理は呼びません。
/// 記録は <c>D</c> 一件を、呼出しが戻る前（返したTaskを待つ前）に、呼出し時点のケースへ帰属させます。状態は変えないため、復元の対象はありません。
/// 一行の形は <c>method=&lt;メソッド名&gt; | request=&lt;要求の型名&gt; | title=&lt;題名&gt; | text=&lt;本文&gt; | owner=&lt;要求のOwnerの型名、なければnull&gt;</c> で、
/// <c>ShowWindowAsync</c> だけ <c>result=&lt;結果の型引数の型名&gt;</c> を加えます。
/// 題名は通知・確認が <c>Caption</c>、ピッカーと進捗が <c>Title</c>、本文は通知・確認が <c>MessageBoxText</c>、進捗が <c>Label</c> で、持たない要求は <c>-</c> です。
/// 題名・本文はnullなら <c>-</c> で、CRLF・CR・LFをそれぞれ空白一つにし、先頭200文字で切ります（切ったときは末尾に <c>…</c> を付けます）。200文字目が高位サロゲートなら、ペアを分断せず199文字で切ります。
/// 通知を期待するテストは、記録用の窓口を明示的に注入して表明します。
/// </remarks>
internal sealed class UndeclaredUiDialogService : IUiDialogService
{
    private const int SummaryLength = 200;

    /// <summary>窓口を作ります。</summary>
    /// <exception cref="InvalidOperationException">
    /// 観測の境界が有効でないmodule（Coreなど）で作った場合。記録が消費されず、未宣言の要求が黙って通るため、作成時に失敗にします。
    /// </exception>
    internal UndeclaredUiDialogService()
    {
        if (!HostResidueBoundary.IsEnabled)
        {
            throw new InvalidOperationException(
                "UndeclaredUiDialogService records undeclared dialog requests to the shared host observation, which is consumed only by modules that enable the host residue boundary.");
        }
    }

    /// <inheritdoc />
    public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        InvalidOperationException failure = Record(nameof(ShowMessageAsync), request, request.Caption, request.MessageBoxText, request.Owner);
        return Task.FromResult(UiDialogResult.Failed(failure));
    }

    /// <inheritdoc />
    public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        InvalidOperationException failure = Record(nameof(ConfirmAsync), request, request.Caption, request.MessageBoxText, request.Owner);
        return Task.FromResult(UiDialogResult.Failed(failure));
    }

    /// <inheritdoc />
    public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
        where TWindow : Window
    {
        ArgumentNullException.ThrowIfNull(request);
        InvalidOperationException failure = Record(nameof(ShowWindowAsync), request, null, null, request.Owner, typeof(TResult));
        return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.Failed, error: failure));
    }

    /// <inheritdoc />
    public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        InvalidOperationException failure = Record(nameof(PickFileAsync), request, request.Title, null, request.Owner);
        return Task.FromResult(new UiFilePickerResult(UiDialogStatus.Failed, error: failure));
    }

    /// <inheritdoc />
    public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        InvalidOperationException failure = Record(nameof(PickFolderAsync), request, request.Title, null, request.Owner);
        return Task.FromResult(new UiFolderPickerResult(UiDialogStatus.Failed, error: failure));
    }

    /// <inheritdoc />
    public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        InvalidOperationException failure = Record(nameof(PickSaveFileAsync), request, request.Title, null, request.Owner);
        return Task.FromResult(new UiSaveFilePickerResult(UiDialogStatus.Failed, error: failure));
    }

    /// <inheritdoc />
    public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        InvalidOperationException failure = Record(nameof(RunWithProgressAsync), request, request.Title, request.Label, request.Owner);
        return Task.FromResult(new UiProgressResult(UiDialogStatus.Failed, error: failure));
    }

    private static InvalidOperationException Record(string method, object request, string? title, string? text, Window? owner, Type? resultType = null)
    {
        string line = $"method={method} | request={request.GetType().FullName} | title={Summarize(title)} | text={Summarize(text)} | owner={owner?.GetType().FullName ?? "null"}";
        if (resultType != null) { line += $" | result={resultType.FullName ?? resultType.Name}"; }
        HostObservationLog.Add(HostObservationVerdict.UndeclaredDialogCategory, line);
        return new InvalidOperationException("Undeclared UI dialog request: " + line);
    }

    /// <summary>nullは <c>-</c>、それ以外はCRLF・CR・LFを空白一つにして先頭200文字で切ります（切ったときだけ <c>…</c> を付けます）。</summary>
    private static string Summarize(string? value)
    {
        if (value == null) { return "-"; }
        string flat = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (flat.Length <= SummaryLength) { return flat; }
        // 切る位置がサロゲートペアの途中（添字199が高位サロゲート）なら、ペアを分断せず199文字で切ります。
        int length = char.IsHighSurrogate(flat[SummaryLength - 1]) ? SummaryLength - 1 : SummaryLength;
        return flat[..length] + "…";
    }
}
