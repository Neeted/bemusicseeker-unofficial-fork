using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistDataParseException : ArgumentException
{
    internal PlaylistDataParseException(string message, Exception innerException)
        : base(message, "_data_json", innerException)
    {
    }
}
