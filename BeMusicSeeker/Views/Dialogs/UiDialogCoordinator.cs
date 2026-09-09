using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Parago.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 通常運用中の UI dialog 表示を集約します。legacy route のように表示失敗を標準 message box fallback へ丸めないための入口です。
/// </summary>
internal sealed class UiDialogCoordinator : IUiDialogService
{
    private readonly UiDialogOwnerResolver ownerResolver;

    private readonly Func<Window, IDisposable> modalScopeFactory;

    private readonly Func<Window, UiMessageRequest, ThemedMessageBoxResponse> messagePresenter;

    /// <summary>
    /// 既定の owner resolver を使う coordinator を初期化します。
    /// </summary>
    internal UiDialogCoordinator()
        : this(new UiDialogOwnerResolver(), UiDialogOwnerResolver.PushActiveModal)
    {
    }

    /// <summary>
    /// owner resolver を指定して coordinator を初期化します。
    /// </summary>
    /// <param name="ownerResolver">dialog owner を解決する resolver。</param>
    /// <exception cref="ArgumentNullException">ownerResolver が null の場合。</exception>
    internal UiDialogCoordinator(UiDialogOwnerResolver ownerResolver)
        : this(ownerResolver, UiDialogOwnerResolver.PushActiveModal)
    {
    }

    /// <summary>
    /// 指定した owner resolver と modal scope 境界を使う coordinator を初期化します。
    /// </summary>
    /// <param name="ownerResolver">dialog owner を解決する resolver。</param>
    /// <param name="modalScopeFactory">表示中の modal window を active owner として登録する境界。</param>
    /// <exception cref="ArgumentNullException">いずれかの引数が null の場合。</exception>
    internal UiDialogCoordinator(
        UiDialogOwnerResolver ownerResolver,
        Func<Window, IDisposable> modalScopeFactory)
    {
        this.ownerResolver = ownerResolver ?? throw new ArgumentNullException(nameof(ownerResolver));
        this.modalScopeFactory = modalScopeFactory ?? throw new ArgumentNullException(nameof(modalScopeFactory));
        messagePresenter = PresentThemedMessageBox;
    }

    /// <summary>
    /// owner resolver、modal scope、message presenter を指定して coordinator を初期化します。
    /// presenter は通常の message / confirmation 表示だけを差し替え、owner 解決と dispatcher 境界は coordinator に残します。
    /// </summary>
    /// <param name="ownerResolver">dialog owner を解決する resolver。</param>
    /// <param name="modalScopeFactory">表示中の modal window を active owner として登録する境界。</param>
    /// <param name="messagePresenter">解決済み owner と元の表示 request を受けて message box 結果を返す presenter。既定経路は <see cref="ThemedMessageBox"/> を使います。</param>
    /// <exception cref="ArgumentNullException">いずれかの引数が null の場合。</exception>
    internal UiDialogCoordinator(
        UiDialogOwnerResolver ownerResolver,
        Func<Window, IDisposable> modalScopeFactory,
        Func<Window, UiMessageRequest, ThemedMessageBoxResponse> messagePresenter)
        : this(ownerResolver, modalScopeFactory)
    {
        this.messagePresenter = messagePresenter ?? throw new ArgumentNullException(nameof(messagePresenter));
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
    /// window modal dialog を表示します。
    /// </summary>
    /// <typeparam name="TWindow">表示する window 型。</typeparam>
    /// <typeparam name="TResult">dialog 固有の戻り値型。</typeparam>
    /// <param name="request">window 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>window modal dialog 結果。</returns>
    public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
        UiWindowDialogRequest<TWindow, TResult> request,
        CancellationToken cancellationToken = default)
        where TWindow : Window
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return ShowWindowOnDispatcherAsync(request, cancellationToken);
    }

    /// <summary>
    /// file picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return RunPickerOnDispatcherAsync(() => PickFileCore(request), cancellationToken);
    }

    /// <summary>
    /// folder picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return RunPickerOnDispatcherAsync(() => PickFolderCore(request), cancellationToken);
    }

    /// <summary>
    /// save file picker を表示します。
    /// </summary>
    /// <param name="request">picker 表示要求。</param>
    /// <param name="cancellationToken">表示前に呼び出し側が取り消すための token。</param>
    /// <returns>picker 結果。</returns>
    public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return RunPickerOnDispatcherAsync(() => PickSaveFileCore(request), cancellationToken);
    }

    private Task<T> RunPickerOnDispatcherAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        Application application = Application.Current;
        if (application?.Dispatcher == null)
        {
            return Task.FromResult(CreatePickerNotShownResult<T>(UiDialogStatus.DispatcherUnavailable));
        }

        Dispatcher dispatcher = application.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromResult(CreatePickerNotShownResult<T>(UiDialogStatus.AppClosing));
        }

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(operation());
        }

        var completion = new TaskCompletionSource<T>();
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
                    completion.TrySetResult(operation());
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(CreatePickerFailedResult<T>(ex));
                }
            }));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(CreatePickerNotShownResult<T>(UiDialogStatus.AppClosing));
        }

        dispatcherOperation.Aborted += delegate
        {
            completion.TrySetResult(CreatePickerNotShownResult<T>(UiDialogStatus.AppClosing));
        };
        if (dispatcherOperation.Status == DispatcherOperationStatus.Aborted)
        {
            completion.TrySetResult(CreatePickerNotShownResult<T>(UiDialogStatus.AppClosing));
        }

        return completion.Task;
    }

    private Task<UiWindowDialogResult<TResult>> ShowWindowOnDispatcherAsync<TWindow, TResult>(
        UiWindowDialogRequest<TWindow, TResult> request,
        CancellationToken cancellationToken)
        where TWindow : Window
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<UiWindowDialogResult<TResult>>(cancellationToken);
        }

        Application application = Application.Current;
        if (application?.Dispatcher == null)
        {
            return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.DispatcherUnavailable));
        }

        Dispatcher dispatcher = application.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.AppClosing));
        }

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowWindowCore(request));
        }

        var completion = new TaskCompletionSource<UiWindowDialogResult<TResult>>();
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

                completion.TrySetResult(ShowWindowCore(request));
            }));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.AppClosing));
        }

        dispatcherOperation.Aborted += delegate
        {
            completion.TrySetResult(new UiWindowDialogResult<TResult>(UiDialogStatus.AppClosing));
        };
        if (dispatcherOperation.Status == DispatcherOperationStatus.Aborted)
        {
            completion.TrySetResult(new UiWindowDialogResult<TResult>(UiDialogStatus.AppClosing));
        }

        return completion.Task;
    }

    private UiWindowDialogResult<TResult> ShowWindowCore<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request)
        where TWindow : Window
    {
        Window owner = ownerResolver.ResolveOwner(request.Owner);
        if (owner == null)
        {
            return new UiWindowDialogResult<TResult>(UiDialogStatus.OwnerUnavailable);
        }

        try
        {
            TWindow window = request.CreateWindow();
            if (window == null)
            {
                return new UiWindowDialogResult<TResult>(
                    UiDialogStatus.Failed,
                    error: new InvalidOperationException("Window dialog factory returned null."));
            }
            if (window.Owner != null && !ReferenceEquals(window.Owner, owner))
            {
                return new UiWindowDialogResult<TResult>(
                    UiDialogStatus.Failed,
                    error: new InvalidOperationException("Window dialog factory assigned a different owner."));
            }
            if (window.Owner == null)
            {
                window.Owner = owner;
            }

            using (modalScopeFactory(window))
            {
                bool? dialogResult = window.ShowDialog();
                UiDialogStatus status = dialogResult == true
                    ? UiDialogStatus.Accepted
                    : dialogResult == false
                        ? UiDialogStatus.CancelledByUser
                        : UiDialogStatus.ClosedByUser;
                return new UiWindowDialogResult<TResult>(status, request.CreateResult(window), dialogResult);
            }
        }
        catch (InvalidOperationException) when (Application.Current?.Dispatcher?.HasShutdownStarted == true)
        {
            return new UiWindowDialogResult<TResult>(UiDialogStatus.AppClosing);
        }
        catch (Exception ex)
        {
            return new UiWindowDialogResult<TResult>(UiDialogStatus.Failed, error: ex);
        }
    }

    private static T CreatePickerNotShownResult<T>(UiDialogStatus status)
    {
        if (typeof(T) == typeof(UiFilePickerResult))
        {
            return (T)(object)new UiFilePickerResult(status);
        }
        if (typeof(T) == typeof(UiFolderPickerResult))
        {
            return (T)(object)new UiFolderPickerResult(status);
        }
        if (typeof(T) == typeof(UiSaveFilePickerResult))
        {
            return (T)(object)new UiSaveFilePickerResult(status);
        }

        throw new InvalidOperationException("Unsupported picker result type: " + typeof(T).FullName);
    }

    private static T CreatePickerFailedResult<T>(Exception exception)
    {
        if (typeof(T) == typeof(UiFilePickerResult))
        {
            return (T)(object)new UiFilePickerResult(UiDialogStatus.Failed, error: exception);
        }
        if (typeof(T) == typeof(UiFolderPickerResult))
        {
            return (T)(object)new UiFolderPickerResult(UiDialogStatus.Failed, error: exception);
        }
        if (typeof(T) == typeof(UiSaveFilePickerResult))
        {
            return (T)(object)new UiSaveFilePickerResult(UiDialogStatus.Failed, error: exception);
        }

        throw new InvalidOperationException("Unsupported picker result type: " + typeof(T).FullName);
    }

    private UiFilePickerResult PickFileCore(UiFilePickerRequest request)
    {
        Window owner = ownerResolver.ResolveOwner(request.Owner);
        if (owner == null)
        {
            return new UiFilePickerResult(UiDialogStatus.OwnerUnavailable);
        }

        try
        {
            OpenFileDialog dialog = CreateOpenFileDialog(request);

            return dialog.ShowDialog(owner) == true
                ? new UiFilePickerResult(UiDialogStatus.Accepted, dialog.FileNames)
                : new UiFilePickerResult(UiDialogStatus.CancelledByUser);
        }
        catch (Exception ex)
        {
            return new UiFilePickerResult(UiDialogStatus.Failed, error: ex);
        }
    }

    private UiFolderPickerResult PickFolderCore(UiFolderPickerRequest request)
    {
        Window owner = ownerResolver.ResolveOwner(request.Owner);
        if (owner == null)
        {
            return new UiFolderPickerResult(UiDialogStatus.OwnerUnavailable);
        }

        try
        {
            OpenFolderDialog dialog = CreateOpenFolderDialog(request);
            return dialog.ShowDialog(owner) == true
                ? new UiFolderPickerResult(UiDialogStatus.Accepted, dialog.FolderNames)
                : new UiFolderPickerResult(UiDialogStatus.CancelledByUser);
        }
        catch (Exception ex)
        {
            return new UiFolderPickerResult(UiDialogStatus.Failed, error: ex);
        }
    }

    /// <summary>
    /// file picker request を OS の open-file dialog options へ変換します。
    /// </summary>
    /// <param name="request">file picker 表示要求。</param>
    /// <returns>表示前の open-file dialog。</returns>
    /// <exception cref="ArgumentNullException">request が null の場合。</exception>
    internal static OpenFileDialog CreateOpenFileDialog(UiFilePickerRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var dialog = new OpenFileDialog
        {
            Title = request.Title,
            FileName = request.FileName,
            InitialDirectory = UiFilePickerUtilities.ResolveInitialDirectory(request.InitialDirectory),
            Filter = UiFilePickerUtilities.BuildFilter(request.Filter),
            CheckFileExists = request.EnsureFileExists,
            CheckPathExists = request.EnsurePathExists,
            Multiselect = request.Multiselect
        };
        string defaultExtension = request.DefaultExtension;
        if (string.IsNullOrWhiteSpace(defaultExtension))
        {
            defaultExtension = UiFilePickerUtilities.InferDefaultExtension(request.FileName, request.Filter);
        }
        if (!string.IsNullOrWhiteSpace(defaultExtension))
        {
            dialog.DefaultExt = "." + defaultExtension.TrimStart('.');
            dialog.AddExtension = true;
        }
        return dialog;
    }

    /// <summary>
    /// folder picker request を OS の open-folder dialog options へ変換します。
    /// </summary>
    /// <param name="request">folder picker 表示要求。</param>
    /// <returns>表示前の open-folder dialog。</returns>
    /// <exception cref="ArgumentNullException">request が null の場合。</exception>
    internal static OpenFolderDialog CreateOpenFolderDialog(UiFolderPickerRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return new OpenFolderDialog
        {
            Title = request.Title,
            InitialDirectory = UiFilePickerUtilities.ResolveInitialDirectory(request.SelectedPath),
            Multiselect = request.Multiselect
        };
    }

    private UiSaveFilePickerResult PickSaveFileCore(UiSaveFilePickerRequest request)
    {
        Window owner = ownerResolver.ResolveOwner(request.Owner);
        if (owner == null)
        {
            return new UiSaveFilePickerResult(UiDialogStatus.OwnerUnavailable);
        }

        try
        {
            SaveFileDialog dialog = CreateSaveFileDialog(request);
            return dialog.ShowDialog(owner) == true
                ? new UiSaveFilePickerResult(UiDialogStatus.Accepted, dialog.FileName)
                : new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser);
        }
        catch (Exception ex)
        {
            return new UiSaveFilePickerResult(UiDialogStatus.Failed, error: ex);
        }
    }

    /// <summary>
    /// save-file picker request を OS の save-file dialog options へ変換します。
    /// </summary>
    /// <param name="request">save-file picker 表示要求。</param>
    /// <returns>表示前の save-file dialog。</returns>
    /// <exception cref="ArgumentNullException">request が null の場合。</exception>
    internal static SaveFileDialog CreateSaveFileDialog(UiSaveFilePickerRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return new SaveFileDialog
        {
            Title = request.Title,
            FileName = request.FileName,
            DefaultExt = request.DefaultExtension,
            AddExtension = request.AddExtension,
            Filter = request.Filter
        };
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
                request.Settings,
                modalScopeFactory);
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
            ThemedMessageBoxResponse response = messagePresenter(owner, request);
            return response.ClosedWithoutSelection
                ? UiDialogResult.ClosedByUser(response.MessageBoxResult)
                : UiDialogResult.FromMessageBoxResult(response.MessageBoxResult);
        }
        catch (InvalidOperationException) when (messagePresenter == PresentThemedMessageBox
            && (Application.Current?.Dispatcher?.HasShutdownStarted == true || Application.Current?.Dispatcher?.HasShutdownFinished == true))
        {
            return UiDialogResult.NotShown(UiDialogStatus.AppClosing);
        }
        catch (Exception ex)
        {
            return UiDialogResult.Failed(ex);
        }
    }

    private ThemedMessageBoxResponse PresentThemedMessageBox(
        Window owner,
        UiMessageRequest request)
    {
        return ThemedMessageBox.ShowWithStatus(
            owner,
            request.MessageBoxText,
            request.Caption,
            request.Button,
            request.Icon,
            request.DefaultResult,
            request.Options,
            request.WarningMessageBoxText,
            modalScopeFactory);
    }
}
