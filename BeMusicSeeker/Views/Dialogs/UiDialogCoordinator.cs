using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Parago.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 通常運用中の UI dialog 表示を集約します。legacy route のように表示失敗を標準 message box fallback へ丸めないための入口です。
/// </summary>
internal sealed class UiDialogCoordinator : IUiDialogService
{
    private readonly UiDialogOwnerResolver ownerResolver;

    /// <summary>
    /// 既定の owner resolver を使う coordinator を初期化します。
    /// </summary>
    internal UiDialogCoordinator()
        : this(new UiDialogOwnerResolver())
    {
    }

    /// <summary>
    /// owner resolver を指定して coordinator を初期化します。
    /// </summary>
    /// <param name="ownerResolver">dialog owner を解決する resolver。</param>
    /// <exception cref="ArgumentNullException">ownerResolver が null の場合。</exception>
    internal UiDialogCoordinator(UiDialogOwnerResolver ownerResolver)
    {
        this.ownerResolver = ownerResolver ?? throw new ArgumentNullException(nameof(ownerResolver));
    }

    /// <summary>
    /// 通知用 message dialog を表示します。
    /// </summary>
    /// <param name="request">表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>表示結果。</returns>
    /// <exception cref="ArgumentNullException">request が null の場合。</exception>
    public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return ShowMessageOnDispatcherAsync(request, cancellationToken);
    }

    /// <summary>
    /// ユーザー判断を必要とする確認 dialog を表示します。
    /// </summary>
    /// <param name="request">表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>確認結果。</returns>
    /// <exception cref="ArgumentNullException">request が null の場合。</exception>
    public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return ShowMessageOnDispatcherAsync(request, cancellationToken);
    }

    /// <summary>
    /// file picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromException<UiFilePickerResult>(new NotSupportedException("File picker route will be implemented in dialog consolidation Unit 5."));
    }

    /// <summary>
    /// folder picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromException<UiFolderPickerResult>(new NotSupportedException("Folder picker route will be implemented in dialog consolidation Unit 5."));
    }

    /// <summary>
    /// save file picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromException<UiSaveFilePickerResult>(new NotSupportedException("Save file picker route will be implemented in dialog consolidation Unit 5."));
    }

    /// <summary>
    /// progress dialog を表示しながら operation を実行します。
    /// </summary>
    /// <param name="request">progress 表示要求。</param>
    /// <param name="operation">progress context を受け取る operation。</param>
    /// <param name="cancellationToken">operation 開始前に呼び出し側が取り消すための token。</param>
    /// <returns>progress operation 結果。</returns>
    public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        return RunWithProgressOnDispatcherAsync(request, operation, cancellationToken);
    }

    private Task<UiProgressResult> RunWithProgressOnDispatcherAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<UiProgressResult>(cancellationToken);
        }

        Application application = Application.Current;
        if (application?.Dispatcher == null)
        {
            return Task.FromResult(new UiProgressResult(UiDialogStatus.DispatcherUnavailable));
        }

        Dispatcher dispatcher = application.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromResult(new UiProgressResult(UiDialogStatus.AppClosing));
        }

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(RunWithProgressCore(request, operation));
        }

        var completion = new TaskCompletionSource<UiProgressResult>();
        DispatcherOperation dispatcherOperation;
        try
        {
            dispatcherOperation = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(RunWithProgressCore(request, operation));
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(new UiProgressResult(UiDialogStatus.Failed, error: ex));
                }
            }));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(new UiProgressResult(UiDialogStatus.AppClosing));
        }

        dispatcherOperation.Aborted += delegate
        {
            completion.TrySetResult(new UiProgressResult(UiDialogStatus.AppClosing));
        };
        if (dispatcherOperation.Status == DispatcherOperationStatus.Aborted)
        {
            completion.TrySetResult(new UiProgressResult(UiDialogStatus.AppClosing));
        }

        return completion.Task;
    }

    private UiProgressResult RunWithProgressCore(UiProgressRequest request, Func<UiProgressContext, Task> operation)
    {
        Window owner = ownerResolver.ResolveOwner(request.Owner);
        if (owner == null)
        {
            return new UiProgressResult(UiDialogStatus.OwnerUnavailable);
        }

        try
        {
            ProgressDialogResult result = ProgressDialog.Execute(
                owner,
                request.Title,
                request.Label,
                context => operation(new UiProgressContext(context)).GetAwaiter().GetResult(),
                request.Settings);
            if (result == null)
            {
                return new UiProgressResult(UiDialogStatus.Failed, error: new InvalidOperationException("Progress dialog route returned no result."));
            }
            if (result.Cancelled)
            {
                return new UiProgressResult(UiDialogStatus.CancelledByUser, result.Result, result.Error);
            }
            return result.OperationFailed
                ? new UiProgressResult(UiDialogStatus.Failed, result.Result, result.Error)
                : new UiProgressResult(UiDialogStatus.Accepted, result.Result, null);
        }
        catch (InvalidOperationException) when (Application.Current?.Dispatcher?.HasShutdownStarted == true || Application.Current?.Dispatcher?.HasShutdownFinished == true)
        {
            return new UiProgressResult(UiDialogStatus.AppClosing);
        }
        catch (Exception ex)
        {
            return new UiProgressResult(UiDialogStatus.Failed, error: ex);
        }
    }

    private Task<UiDialogResult> ShowMessageOnDispatcherAsync(UiMessageRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<UiDialogResult>(cancellationToken);
        }

        Application application = Application.Current;
        if (application?.Dispatcher == null)
        {
            return Task.FromResult(UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable));
        }

        Dispatcher dispatcher = application.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromResult(UiDialogResult.NotShown(UiDialogStatus.AppClosing));
        }

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowMessageCore(request));
        }

        var completion = new TaskCompletionSource<UiDialogResult>();
        DispatcherOperation operation;
        try
        {
            operation = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(ShowMessageCore(request));
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(UiDialogResult.Failed(ex));
                }
            }));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(UiDialogResult.NotShown(UiDialogStatus.AppClosing));
        }

        operation.Aborted += delegate
        {
            completion.TrySetResult(UiDialogResult.NotShown(UiDialogStatus.AppClosing));
        };
        if (operation.Status == DispatcherOperationStatus.Aborted)
        {
            completion.TrySetResult(UiDialogResult.NotShown(UiDialogStatus.AppClosing));
        }

        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(delegate
            {
                completion.TrySetCanceled(cancellationToken);
                if (operation.Status == DispatcherOperationStatus.Pending)
                {
                    operation.Abort();
                }
            });
            completion.Task.ContinueWith(
                _ => cancellationRegistration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return completion.Task;
    }

    private UiDialogResult ShowMessageCore(UiMessageRequest request)
    {
        Window owner = ownerResolver.ResolveOwner(request.Owner);
        if (owner == null)
        {
            return UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable);
        }

        try
        {
            ThemedMessageBoxResponse response = ThemedMessageBox.ShowWithStatus(
                owner,
                request.MessageBoxText,
                request.Caption,
                request.Button,
                request.Icon,
                request.DefaultResult,
                request.Options);
            return response.ClosedWithoutSelection
                ? UiDialogResult.ClosedByUser(response.MessageBoxResult)
                : UiDialogResult.FromMessageBoxResult(response.MessageBoxResult);
        }
        catch (InvalidOperationException) when (Application.Current?.Dispatcher?.HasShutdownStarted == true || Application.Current?.Dispatcher?.HasShutdownFinished == true)
        {
            return UiDialogResult.NotShown(UiDialogStatus.AppClosing);
        }
        catch (Exception ex)
        {
            return UiDialogResult.Failed(ex);
        }
    }
}
