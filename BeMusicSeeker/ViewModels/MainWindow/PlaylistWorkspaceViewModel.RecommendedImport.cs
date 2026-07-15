using System;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal event EventHandler<PlaylistRecommendedTableImportConfirmationRequestedEventArgs> PlaylistRecommendedTableImportConfirmationRequested;

    /// <summary>
    /// Requests shell confirmation for a recommended-table import and enqueues it when approved.
    /// </summary>
    /// <param name="rawTag">The recommended-table URI stored in the menu item's tag.</param>
    /// <returns><see langword="true"/> when the import was enqueued.</returns>
    internal bool TryEnqueueRecommendedPlaylistImport(string rawTag)
    {
        if (IsWriteLockHeldBMSTablesInitializeMin)
        {
            return false;
        }

        Uri uri = new(rawTag);
        BMSLibrary library = getPlaylistLibrary();
        int lr2Id = library?.LR2ID ?? 0;
        var request = new PlaylistRecommendedTableImportConfirmationRequestedEventArgs(
            lr2Id,
            Regex.Match(rawTag, "mode=update").Success);
        EventHandler<PlaylistRecommendedTableImportConfirmationRequestedEventArgs> handler =
            PlaylistRecommendedTableImportConfirmationRequested
            ?? throw new InvalidOperationException("Recommended playlist table import confirmation is not configured.");
        handler(this, request);
        if (request.Lr2Id == 0 || !request.Confirmed)
        {
            return false;
        }

        EnqueueExternalPlaylistBMSTableImport(uri);
        return true;
    }
}

internal sealed class PlaylistRecommendedTableImportConfirmationRequestedEventArgs : EventArgs
{
    internal PlaylistRecommendedTableImportConfirmationRequestedEventArgs(int lr2Id, bool isUpdateMode)
    {
        Lr2Id = lr2Id;
        IsUpdateMode = isUpdateMode;
    }

    internal int Lr2Id { get; }

    internal bool IsUpdateMode { get; }

    internal bool Confirmed { get; set; }
}
