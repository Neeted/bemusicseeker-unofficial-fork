using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Parago.Windows;

public partial class ProgressDialog : Window, IComponentConnector
{
    private volatile bool _isBusy;

    private BackgroundWorker _worker;

    public static ProgressDialogContext Current { get; set; }

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
            base.Height = 140.0;
            base.MinHeight = 140.0;
            SubTextLabel.Visibility = Visibility.Visible;
        }
        else
        {
            base.Height = 110.0;
            base.MinHeight = 110.0;
            SubTextLabel.Visibility = Visibility.Collapsed;
        }
        CancelButton.Visibility = ((!settings.ShowCancelButton) ? Visibility.Collapsed : Visibility.Visible);
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
            try
            {
                Current = new ProgressDialogContext(s as BackgroundWorker, e);
                if (operation is Action)
                {
                    ((Action)operation)();
                }
                else
                {
                    if (operation is not Func<object>)
                    {
                        throw new InvalidOperationException("Operation type is not supoorted");
                    }
                    e.Result = ((Func<object>)operation)();
                }
                Current.CheckCancellationPending();
            }
            catch (ProgressDialogCancellationExcpetion)
            {
            }
            catch (Exception)
            {
                if (!Current.CheckCancellationPending())
                {
                    throw;
                }
            }
            finally
            {
                Current = null;
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
            if (!_worker.CancellationPending)
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
            SubLabel = "aborting ...";
            CancelButton.IsEnabled = false;
            _worker.CancelAsync();
        }
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

    internal static ProgressDialogResult ExecuteInternal(Window owner, string title, string label, object operation, ProgressDialogSettings settings)
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
        return progressDialog.Execute(operation);
    }



}
