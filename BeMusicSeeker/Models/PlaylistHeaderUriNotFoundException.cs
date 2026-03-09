using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistHeaderUriNotFoundException : InvalidOperationException
{
    internal PlaylistHeaderUriNotFoundException(Uri pageUri)
        : base("Playlist header URL was not found in page: " + (pageUri?.ToString() ?? string.Empty))
    {
        PageUri = pageUri;
    }

    internal Uri PageUri { get; }
}
