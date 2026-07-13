using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal event EventHandler<PlaylistTreeSelectionRequestedEventArgs> TreeSelectionRequested;

    internal void RequestSummarySelection()
    {
        TreeSelectionRequested?.Invoke(this, PlaylistTreeSelectionRequestedEventArgs.Summary());
    }

    internal void RequestDetailSelection(BMSTable table, PlaylistFolderNode folderNode = null)
    {
        string folderName = folderNode == null || folderNode.IsSpecial
            ? null
            : folderNode.FolderName;
        PlaylistDetailFilter filter = folderNode?.SpecialKind == PlaylistFolderNodeSpecialKind.NotOwned
            ? PlaylistDetailFilter.PlaylistNotOwnedFilterSelected
            : PlaylistDetailFilter.PlaylistFilter;
        TreeSelectionRequested?.Invoke(
            this,
            PlaylistTreeSelectionRequestedEventArgs.CreateDetail(
                new PlaylistDetailSelection(table, folderName, filter)));
    }
}

internal sealed class PlaylistDetailSelection
{
    internal PlaylistDetailSelection(
        BMSTable table,
        string folderName,
        PlaylistDetailFilter filter)
    {
        Table = table;
        FolderName = folderName;
        Filter = filter;
    }

    internal BMSTable Table { get; }

    internal string FolderName { get; }

    internal PlaylistDetailFilter Filter { get; }
}

internal sealed class PlaylistTreeSelectionRequestedEventArgs : EventArgs
{
    private PlaylistTreeSelectionRequestedEventArgs(
        bool isSummary,
        PlaylistDetailSelection detail)
    {
        IsSummary = isSummary;
        Detail = detail;
    }

    internal bool IsSummary { get; }

    internal PlaylistDetailSelection Detail { get; }

    internal static PlaylistTreeSelectionRequestedEventArgs Summary() => new(true, null);

    internal static PlaylistTreeSelectionRequestedEventArgs CreateDetail(PlaylistDetailSelection detail)
    {
        return new PlaylistTreeSelectionRequestedEventArgs(
            isSummary: false,
            detail ?? throw new ArgumentNullException(nameof(detail)));
    }
}
