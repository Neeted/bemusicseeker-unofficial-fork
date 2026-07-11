using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    private Action<PlaylistSourceClearCommitResult> logPlaylistSourceClear;

    internal void ConfigureTerminalShellPublish(Action<PlaylistSourceClearCommitResult> logSourceClear)
    {
        logPlaylistSourceClear = logSourceClear
            ?? throw new ArgumentNullException(nameof(logSourceClear));
    }

    internal void PublishTerminalShellState(
        PlayHistoryTerminalCommitResult terminalCommit,
        PlaylistDetailBuildState playlistDetailBuildState)
    {
        if (terminalCommit == null)
        {
            throw new ArgumentNullException(nameof(terminalCommit));
        }
        if (playlistDetailBuildState == null)
        {
            throw new ArgumentNullException(nameof(playlistDetailBuildState));
        }
        if (terminalCommit.PlaylistSourceClear == null)
        {
            return;
        }
        if (logPlaylistSourceClear == null)
        {
            throw new PlayHistoryTerminalPublishException(
                new InvalidOperationException("Play-history terminal shell publishing must be configured before a source clear is published."),
                ownershipTransferred: true,
                terminalCommit);
        }

        List<Exception> publishExceptions = [];
        TryPublish(
            () => playlistDetailBuildState.PublishSourceClear(terminalCommit.PlaylistSourceClear),
            publishExceptions);
        TryPublish(
            () => logPlaylistSourceClear(terminalCommit.PlaylistSourceClear),
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
        PlaylistDetailBuildState playlistDetailBuildState)
    {
        if (tablePublishException?.TerminalCommitResult?.PlaylistSourceClear == null)
        {
            return;
        }

        try
        {
            PublishTerminalShellState(tablePublishException.TerminalCommitResult, playlistDetailBuildState);
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
