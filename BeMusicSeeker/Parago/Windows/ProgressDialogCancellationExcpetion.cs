using System;
using System.Runtime.Serialization;

namespace Parago.Windows;

[Serializable]
internal class ProgressDialogCancellationExcpetion : Exception
{
    public ProgressDialogCancellationExcpetion()
    {
    }

    public ProgressDialogCancellationExcpetion(string message)
        : base(message)
    {
    }

    public ProgressDialogCancellationExcpetion(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProgressDialogCancellationExcpetion(string format, params string[] arg)
        : base(string.Format(format, arg))
    {
    }

    [System.Obsolete(DiagnosticId = "SYSLIB0051")]
    protected ProgressDialogCancellationExcpetion(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
