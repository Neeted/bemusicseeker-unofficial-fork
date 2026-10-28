using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    /// <summary>履歴行の入力とローカル能力を捕捉します。表示項目の有無で未解決行を拒否しません。</summary>
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
            default:
                return false;
        }
    }

    /// <summary>取得済みの履歴hashを設定Web操作へ渡し、ローカルpathは既存の実在検査を通します。</summary>
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
