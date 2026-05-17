using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistHeaderParseException : ArgumentException
{
    internal PlaylistHeaderParseException(string message, Exception innerException)
        : base(message, "_header_json", innerException)
    {
    }

    public PlaylistHeaderParseException() : base()
    {
    }

    public PlaylistHeaderParseException(string message) : base(message)
    {
    }

    public PlaylistHeaderParseException(string message, string paramName, Exception innerException) : base(message, paramName, innerException)
    {
    }

    public PlaylistHeaderParseException(string message, string paramName) : base(message, paramName)
    {
    }
}
