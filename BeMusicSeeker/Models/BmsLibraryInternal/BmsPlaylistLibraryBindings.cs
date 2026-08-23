using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Carries the library capabilities used by a playlist constructed for startup.
/// </summary>
internal sealed class BmsPlaylistLibraryBindings
{
    /// <summary>
    /// Initializes bindings for one concrete library.
    /// </summary>
    /// <param name="sourceLibrary">The exact library used by the playlist.</param>
    internal BmsPlaylistLibraryBindings(BMSLibrary sourceLibrary)
    {
        SourceLibrary = sourceLibrary ?? throw new ArgumentNullException(nameof(sourceLibrary));
    }

    /// <summary>
    /// Gets the exact library that owns all forwarded capabilities.
    /// </summary>
    internal BMSLibrary SourceLibrary { get; }

    /// <summary>
    /// Gets scores from the exact source library.
    /// </summary>
    /// <returns>The source library's current score snapshot.</returns>
    internal List<BMSScore> GetBmsScores()
        => SourceLibrary.GetBMSScores();

    /// <summary>
    /// Creates a beatoraja hash resolver from the exact source library.
    /// </summary>
    /// <returns>The source library's resolver.</returns>
    internal Func<BmtSongHashResolveRequest, Tuple<string, string>> CreateBeatorajaBmtSongHashResolver()
        => SourceLibrary.CreateBeatorajaBmtSongHashResolver();

    /// <summary>
    /// Gets the LR2 playlist-folder synchronization port owned by the source library.
    /// </summary>
    internal ILr2PlaylistFolderSynchronizationPort Lr2PlaylistFolderSynchronization
        => SourceLibrary.Lr2PlaylistFolderSynchronization;
}
