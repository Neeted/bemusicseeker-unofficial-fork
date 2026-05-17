using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistAlreadyExistsException : InvalidOperationException
{
    internal PlaylistAlreadyExistsException(string message, string playlistName)
        : base(message)
    {
        PlaylistName = playlistName ?? string.Empty;
    }

    public PlaylistAlreadyExistsException() : base()
    {
    }

    public PlaylistAlreadyExistsException(string message) : base(message)
    {
    }

    public PlaylistAlreadyExistsException(string message, Exception innerException) : base(message, innerException)
    {
    }

    internal string PlaylistName { get; }
}
