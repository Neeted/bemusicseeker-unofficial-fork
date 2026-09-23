using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    internal bool TryCreateContextMenuState(object row, out PlayHistoryContextMenuState state)
    {
        return PlayHistoryContextMenuState.TryCreate(row, out state);
    }

    internal bool TryCreateContextMenuAction(
        object row,
        PlayHistoryContextMenuActionKind actionKind,
        out PlayHistoryContextMenuAction action)
    {
        action = null;
        if (!TryCreateContextMenuState(row, out PlayHistoryContextMenuState state))
        {
            return false;
        }

        switch (actionKind)
        {
            case PlayHistoryContextMenuActionKind.OpenAssociated:
                if (!state.CanOpenAssociated)
                {
                    return false;
                }
                action = PlayHistoryContextMenuAction.ForPath(state.ChartPath);
                return true;
            case PlayHistoryContextMenuActionKind.OpenExplorer:
                if (!state.CanOpenExplorer)
                {
                    return false;
                }
                action = PlayHistoryContextMenuAction.ForPath(state.ChartPath);
                return true;
            case PlayHistoryContextMenuActionKind.RegisterScoreViewer:
                if (!state.CanOpenScoreViewer)
                {
                    return false;
                }
                action = PlayHistoryContextMenuAction.ForScoreViewer(
                    new ScoreViewerTarget(state.Md5, state.ChartPath, state.ChartTitle));
                return true;
            case PlayHistoryContextMenuActionKind.CopyMd5:
                return TryCreateCopyAction(state, PlayHistoryContextMenuState.CopyMd5Kind, out action);
            case PlayHistoryContextMenuActionKind.CopySha256:
                return TryCreateCopyAction(state, PlayHistoryContextMenuState.CopySha256Kind, out action);
            default:
                return false;
        }
    }

    private static bool TryCreateCopyAction(
        PlayHistoryContextMenuState state,
        string copyKind,
        out PlayHistoryContextMenuAction action)
    {
        action = null;
        string value = state.GetCopyValue(copyKind);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        action = PlayHistoryContextMenuAction.ForValue(value);
        return true;
    }

    internal bool TryCreateRightClickActionResolutionInput(
        object row,
        Func<string, bool> fileExists,
        out RightClickActionResolutionInput input)
    {
        input = null;
        if (!TryCreateContextMenuState(row, out PlayHistoryContextMenuState state))
        {
            return false;
        }
        input = state.CreateResolutionInput(fileExists);
        return true;
    }

    /// <summary>Creates an exact local chart target for play-history associated-open actions.</summary>
    internal bool TryCreateAssociatedChartOperationTarget(
        object row,
        out ChartOperationTarget target)
    {
        target = null;
        return TryCreateContextMenuState(row, out PlayHistoryContextMenuState state)
            && (target = state.CreateAssociatedOpenTarget()) != null;
    }

}
