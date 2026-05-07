using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistAlreadyExistsException : InvalidOperationException
{
    internal PlaylistAlreadyExistsException(string message, string playlistName)
        : base(message)
    {
        PlaylistName = playlistName ?? string.Empty;
    }

    internal string PlaylistName { get; }
}
