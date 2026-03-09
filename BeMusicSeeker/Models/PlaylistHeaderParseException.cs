using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistHeaderParseException : ArgumentException
{
    internal PlaylistHeaderParseException(string message, Exception innerException)
        : base(message, "_header_json", innerException)
    {
    }
}
