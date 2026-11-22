using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using BeMusicSeeker.Views;

namespace Parago.Windows;

public partial class ProgressDialog : ThemedWindow, IComponentConnector
{
    private volatile bool _isBusy;

    private bool _operationOwnsCancellation;

    private BackgroundWorker _worker;

    public string Label
    {
        get
        {
            return TextLabel.Text;
        }
        set
        {
            TextLabel.Text = value;
        }
    }

    public string SubLabel
    {
        get
        {
            return SubTextLabel.Text;
        }
        set
        {
            SubTextLabel.Text = value;
        }
    }

    internal ProgressDialogResult Result { get; private set; }

    public ProgressDialog(ProgressDialogSettings settings)
    {
        InitializeComponent();
        settings ??= ProgressDialogSettings.WithLabelOnly;
        if (settings.ShowSubLabel)
        {
            SubTextLabel.Visibility = Visibility.Visible;
        }
        else
        {
            SubTextLabel.Visibility = Visibility.Collapsed;
        }
        CancelButton.Visibility = ((!settings.ShowCancelButton) ? Visibility.Collapsed : Visibility.Visible);
        _operationOwnsCancellation = settings.OperationOwnsCancellation;
        ProgressBar.IsIndeterminate = settings.ShowProgressBarIndeterminate;
    }

    internal ProgressDialogResult Execute(object operation)
    {
        if (operation == null)
        {
            throw new ArgumentNullException("operation");
        }
        ProgressDialogResult result = null;
        _isBusy = true;
        _worker = new BackgroundWorker
        {
            WorkerReportsProgress = true,
            WorkerSupportsCancellation = true
        };
        _worker.DoWork += delegate (object s, DoWorkEventArgs e)
        {
            ProgressDialogContext current = null;
            try
            {
                current = new ProgressDialogContext(s as BackgroundWorker, e, DisableCancellation);
                if (operation is Action)
                {
                    ((Action)operation)();
                }
                else if (operation is Action<ProgressDialogContext>)
                {
                    ((Action<ProgressDialogContext>)operation)(current);
                }
                else
                {
                    if (operation is not Func<object>)
                    {
                        throw new InvalidOperationException("Operation type is not supoorted");
                    }
                    e.Result = ((Func<object>)operation)();
                }
                if (!_operationOwnsCancellation)
                {
                    current.CheckCancellationPending();
                }
            }
            catch (ProgressDialogCancellationExcpetion)
            {
            }
            catch (Exception)
            {
                // 取消を操作に任せる場合は、取消の要求中でも操作の例外を握りつぶさず、結果として返します。
                if (_operationOwnsCancellation || current == null || !current.CheckCancellationPending())
                {
                    throw;
                }
            }
        };
        _worker.RunWorkerCompleted += delegate (object s, RunWorkerCompletedEventArgs e)
        {
            result = new ProgressDialogResult(e);
            base.Dispatcher.BeginInvoke(DispatcherPriority.Send, (SendOrPostCallback)delegate
            {
                _isBusy = false;
                Close();
            }, null);
        };
        _worker.ProgressChanged += delegate (object s, ProgressChangedEventArgs e)
        {
            if (_operationOwnsCancellation || !_worker.CancellationPending)
            {
                SubLabel = (e.UserState as string) ?? string.Empty;
                ProgressBar.Value = e.ProgressPercentage;
            }
        };
        _worker.RunWorkerAsync();
        ShowDialog();
        return result;
    }

    private void OnCancelButtonClick(object sender, RoutedEventArgs e)
    {
        if (_worker != null && _worker.WorkerSupportsCancellation)
        {
            if (!_operationOwnsCancellation)
            {
                SubLabel = "aborting ...";
            }
            CancelButton.IsEnabled = false;
            _worker.CancelAsync();
        }
    }

    /// <summary>
    /// 取消ボタンを無効にします。操作が取消できない段階へ進んだことを利用者へ示します。どのスレッドからでも呼べ、UIスレッドの完了を待ちません。
    /// </summary>
    private void DisableCancellation()
    {
        void Disable() => CancelButton.IsEnabled = false;
        if (Dispatcher.CheckAccess())
        {
            Disable();
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)Disable);
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        e.Cancel = _isBusy;
    }

    internal static ProgressDialogResult Execute(Window owner, string title, string label, Action operation)
    {
        return ExecuteInternal(owner, title, label, operation, null);
    }

    internal static ProgressDialogResult Execute(Window owner, string title, string label, Action operation, ProgressDialogSettings settings)
    {
        return ExecuteInternal(owner, title, label, operation, settings);
    }

    internal static ProgressDialogResult Execute(Window owner, string title, string label, Action<ProgressDialogContext> operation, ProgressDialogSettings settings)
    {
        return ExecuteInternal(owner, title, label, operation, settings);
    }

    internal static ProgressDialogResult Execute(Window owner, string title, string label, Action<ProgressDialogContext> operation, ProgressDialogSettings settings, Func<Window, IDisposable> modalScopeFactory)
    {
        return ExecuteInternal(owner, title, label, operation, settings, modalScopeFactory);
    }

    internal static ProgressDialogResult Execute(Window owner, string title, string label, Func<object> operationWithResult)
    {
        return ExecuteInternal(owner, title, label, operationWithResult, null);
    }

    internal static ProgressDialogResult Execute(Window owner, string title, string label, Func<object> operationWithResult, ProgressDialogSettings settings)
    {
        return ExecuteInternal(owner, title, label, operationWithResult, settings);
    }

    internal static void Execute(Window owner, string title, string label, Action operation, Action<ProgressDialogResult> successOperation, Action<ProgressDialogResult> failureOperation = null, Action<ProgressDialogResult> cancelledOperation = null)
    {
        ProgressDialogResult progressDialogResult = ExecuteInternal(owner, title, label, operation, null);
        if (progressDialogResult.Cancelled && cancelledOperation != null)
        {
            cancelledOperation(progressDialogResult);
        }
        else if (progressDialogResult.OperationFailed && failureOperation != null)
        {
            failureOperation(progressDialogResult);
        }
        else
        {
            successOperation?.Invoke(progressDialogResult);
        }
    }

    internal static ProgressDialogResult ExecuteInternal(Window owner, string title, string label, object operation, ProgressDialogSettings settings, Func<Window, IDisposable> modalScopeFactory = null)
    {
        var progressDialog = new ProgressDialog(settings)
        {
            Owner = owner
        };
        if (!string.IsNullOrEmpty(title))
        {
            progressDialog.Title = title;
        }
        if (!string.IsNullOrEmpty(label))
        {
            progressDialog.Label = label;
        }
        using IDisposable modalScope = modalScopeFactory?.Invoke(progressDialog);
        return progressDialog.Execute(operation);
    }



}
