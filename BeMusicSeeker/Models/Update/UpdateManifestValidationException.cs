using System;
using System.Runtime.Serialization;

namespace BeMusicSeeker.Models.Update;

[Serializable]
internal sealed class UpdateManifestValidationException : Exception
{
    public UpdateManifestValidationException()
    {
    }

    public UpdateManifestValidationException(string message)
        : base(message)
    {
    }

    public UpdateManifestValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    [System.Obsolete(DiagnosticId = "SYSLIB0051")]
    private UpdateManifestValidationException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
