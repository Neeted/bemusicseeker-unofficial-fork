using System;
using System.ComponentModel;

namespace Parago.Windows;

public class ProgressDialogContext(BackgroundWorker worker, DoWorkEventArgs arguments, Action disableCancellation = null)
{
    public BackgroundWorker Worker { get; private set; } = worker ?? throw new ArgumentNullException("worker");

    public DoWorkEventArgs Arguments { get; private set; } = arguments ?? throw new ArgumentNullException("arguments");

    /// <summary>
    /// 取消が要求されたかを、操作の結果を変えずに返します。<see cref="CheckCancellationPending"/> と違い、取消済みの結果として確定させません。
    /// </summary>
    public bool CancellationRequested => Worker.WorkerSupportsCancellation && Worker.CancellationPending;

    /// <summary>
    /// 取消ボタンを無効にします。操作が取消できない段階へ進んだときに呼びます。呼出し元のスレッドで画面の完了を待ちません。
    /// </summary>
    public void DisableCancellation() => disableCancellation?.Invoke();

    public bool CheckCancellationPending()
    {
        if (Worker.WorkerSupportsCancellation && Worker.CancellationPending)
        {
            Arguments.Cancel = true;
        }
        return Arguments.Cancel;
    }

    public void ThrowIfCancellationPending()
    {
        if (CheckCancellationPending())
        {
            throw new ProgressDialogCancellationExcpetion();
        }
    }

    public void Report(string message)
    {
        if (Worker.WorkerReportsProgress)
        {
            Worker.ReportProgress(0, message);
        }
    }

    public void Report(string format, params object[] arg)
    {
        if (Worker.WorkerReportsProgress)
        {
            Worker.ReportProgress(0, string.Format(format, arg));
        }
    }

    public void Report(int percentProgress, string message)
    {
        if (Worker.WorkerReportsProgress)
        {
            Worker.ReportProgress(percentProgress, message);
        }
    }

    public void Report(int percentProgress, string format, params object[] arg)
    {
        if (Worker.WorkerReportsProgress)
        {
            Worker.ReportProgress(percentProgress, string.Format(format, arg));
        }
    }

    public void ReportWithCancellationCheck(string message)
    {
        ThrowIfCancellationPending();
        if (Worker.WorkerReportsProgress)
        {
            Worker.ReportProgress(0, message);
        }
    }

    public void ReportWithCancellationCheck(string format, params object[] arg)
    {
        ThrowIfCancellationPending();
        if (Worker.WorkerReportsProgress)
        {
            Worker.ReportProgress(0, string.Format(format, arg));
        }
    }

    public void ReportWithCancellationCheck(int percentProgress, string message)
    {
        ThrowIfCancellationPending();
        if (Worker.WorkerReportsProgress)
        {
            Worker.ReportProgress(percentProgress, message);
        }
    }

    public void ReportWithCancellationCheck(int percentProgress, string format, params object[] arg)
    {
        ThrowIfCancellationPending();
        if (Worker.WorkerReportsProgress)
        {
            Worker.ReportProgress(percentProgress, string.Format(format, arg));
        }
    }
}
