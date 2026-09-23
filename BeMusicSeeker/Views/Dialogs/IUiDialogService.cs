using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 通常運用中の UI dialog route を集約します。owner、dispatcher、表示失敗の扱いを call site へ散らさないための境界です。
/// </summary>
internal interface IUiDialogService
{
    /// <summary>
    /// 通知用 message dialog を表示します。
    /// </summary>
    /// <param name="request">表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>表示結果。</returns>
    Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// ユーザー判断を必要とする確認 dialog を表示します。
    /// </summary>
    /// <param name="request">表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>確認結果。</returns>
    Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// window modal dialog を表示します。
    /// </summary>
    /// <typeparam name="TWindow">表示する window 型。</typeparam>
    /// <typeparam name="TResult">dialog 固有の戻り値型。</typeparam>
    /// <param name="request">window 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>window modal dialog 結果。</returns>
    Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
        where TWindow : System.Windows.Window;

    /// <summary>
    /// file picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// folder picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// save file picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// progress dialog を表示しながら operation を実行します。
    /// </summary>
    /// <param name="request">progress 表示要求。</param>
    /// <param name="operation">progress context を受け取る operation。</param>
    /// <param name="cancellationToken">operation 開始前に呼び出し側が取り消すための token。</param>
    /// <returns>progress operation 結果。</returns>
    Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default);
}
