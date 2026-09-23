using System;
using System.ComponentModel;

namespace Parago.Windows;

internal class ProgressDialogResult
{
    public object Result { get; private set; }

    public bool Cancelled { get; private set; }

    public Exception Error { get; private set; }

    public bool OperationFailed => Error != null;

    public ProgressDialogResult(RunWorkerCompletedEventArgs e)
    {
        if (e.Cancelled)
        {
            Cancelled = true;
        }
        else if (e.Error != null)
        {
            Error = e.Error;
        }
        else
        {
            Result = e.Result;
        }
    }
}
