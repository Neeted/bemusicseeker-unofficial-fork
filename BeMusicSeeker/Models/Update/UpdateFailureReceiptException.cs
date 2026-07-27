using System;
using System.Runtime.Serialization;

namespace BeMusicSeeker.Models.Update;

[Serializable]
internal sealed class UpdateFailureReceiptException : InvalidOperationException
{
    [NonSerialized]
    private readonly Action acknowledge;

    private bool acknowledgeAfterPresentation = true;

    public UpdateFailureReceiptException()
    {
    }

    public UpdateFailureReceiptException(string message)
        : base(message)
    {
    }

    public UpdateFailureReceiptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal UpdateFailureReceiptException(string message, Action acknowledge)
        : base(message)
    {
        this.acknowledge = acknowledge ?? throw new ArgumentNullException(nameof(acknowledge));
    }

    private UpdateFailureReceiptException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }

    internal void Acknowledge()
    {
        if (acknowledge == null)
        {
            throw new InvalidOperationException("This failure receipt is not associated with an acknowledge operation.");
        }
        acknowledge();
    }

    internal bool ShouldAcknowledgeAfterPresentation => acknowledgeAfterPresentation;

    internal void DeferAcknowledge()
    {
        acknowledgeAfterPresentation = false;
    }
}
