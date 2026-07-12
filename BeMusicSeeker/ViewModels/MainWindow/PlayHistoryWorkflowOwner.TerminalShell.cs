using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    internal void PublishTerminalShellState(
        PlayHistoryTerminalCommitResult terminalCommit,
        PlaylistWorkspaceViewModel playlistWorkspace)
    {
        if (terminalCommit == null)
        {
            throw new ArgumentNullException(nameof(terminalCommit));
        }
        if (playlistWorkspace == null)
        {
            throw new ArgumentNullException(nameof(playlistWorkspace));
        }
        if (terminalCommit.PlaylistSourceClear == null)
        {
            return;
        }
        List<Exception> publishExceptions = [];
        TryPublish(
            () => playlistWorkspace.PublishPlayHistorySourceClear(terminalCommit.PlaylistSourceClear),
            publishExceptions);
        TryPublish(
            () => playlistWorkspace.LogPlayHistorySourceClear(terminalCommit.PlaylistSourceClear),
            publishExceptions);
        if (publishExceptions.Count > 0)
        {
            throw new PlayHistoryTerminalPublishException(
                new AggregateException(publishExceptions),
                ownershipTransferred: true,
                terminalCommit);
        }
    }

    internal void PublishTerminalShellStateAfterTablePublishFailure(
        PlayHistoryTerminalPublishException tablePublishException,
        PlaylistWorkspaceViewModel playlistWorkspace)
    {
        if (tablePublishException?.TerminalCommitResult?.PlaylistSourceClear == null)
        {
            return;
        }

        try
        {
            PublishTerminalShellState(tablePublishException.TerminalCommitResult, playlistWorkspace);
        }
        catch (PlayHistoryTerminalPublishException shellPublishException)
        {
            throw new PlayHistoryTerminalPublishException(
                new AggregateException(tablePublishException, shellPublishException),
                ownershipTransferred: true,
                tablePublishException.TerminalCommitResult);
        }
    }
}
