using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistDataParseException : ArgumentException
{
    internal PlaylistDataParseException(string message, Exception innerException)
        : base(message, "_data_json", innerException)
    {
    }

    public PlaylistDataParseException() : base()
    {
    }

    public PlaylistDataParseException(string message) : base(message)
    {
    }

    public PlaylistDataParseException(string message, string paramName, Exception innerException) : base(message, paramName, innerException)
    {
    }

    public PlaylistDataParseException(string message, string paramName) : base(message, paramName)
    {
    }
}
