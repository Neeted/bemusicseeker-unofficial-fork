using System;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    /// <summary>確定済みsource退役を公開し、任意診断の失敗とは分離して所有権移譲を伝えます。</summary>
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
        try
        {
            playlistWorkspace.PublishPlayHistorySourceClear(terminalCommit.PlaylistSourceClear);
        }
        catch (Exception failure)
        {
            throw new PlayHistoryTerminalPublishException(failure, ownershipTransferred: true, terminalCommit);
        }
        finally
        {
            playlistWorkspace.LogPlayHistorySourceClear(terminalCommit.PlaylistSourceClear);
        }
    }

    /// <summary>行の公開失敗後もsource退役を公開し、実公開失敗だけを所有権移譲済みの結果へ残します。</summary>
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
                new AggregateException(tablePublishException.InnerException ?? tablePublishException,
                    shellPublishException.InnerException ?? shellPublishException),
                ownershipTransferred: true,
                tablePublishException.TerminalCommitResult);
        }
    }
}
