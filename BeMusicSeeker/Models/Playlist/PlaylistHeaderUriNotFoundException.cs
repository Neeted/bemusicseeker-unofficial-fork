using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistHeaderUriNotFoundException : InvalidOperationException
{
    internal PlaylistHeaderUriNotFoundException(Uri pageUri)
        : base("Playlist header URL was not found in page: " + (pageUri?.ToString() ?? string.Empty))
    {
        PageUri = pageUri;
    }

    public PlaylistHeaderUriNotFoundException() : base()
    {
    }

    public PlaylistHeaderUriNotFoundException(string message) : base(message)
    {
    }

    public PlaylistHeaderUriNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }

    internal Uri PageUri { get; }
}
