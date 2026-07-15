using System;

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
            case PlayHistoryContextMenuActionKind.OpenBmsIr:
                string bmsIrUrl = GetBmsIrSongUrl(state.Md5);
                if (string.IsNullOrWhiteSpace(bmsIrUrl))
                {
                    return false;
                }
                action = PlayHistoryContextMenuAction.ForUrl(bmsIrUrl);
                return true;
            case PlayHistoryContextMenuActionKind.OpenMocha:
                if (!state.CanOpenRepository)
                {
                    return false;
                }
                action = PlayHistoryContextMenuAction.ForUrl(GetMochaSongUrl(state.Sha256));
                return true;
            case PlayHistoryContextMenuActionKind.OpenMinIr:
                if (!state.CanOpenRepository)
                {
                    return false;
                }
                action = PlayHistoryContextMenuAction.ForUrl(GetMinIrSongUrl(state.Sha256));
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

    private static string GetBmsIrSongUrl(string md5)
    {
        if (!PlayHistoryContextMenuState.IsValidBmsIrHash(md5))
        {
            return null;
        }
        return "https://bms-ir.org/new/song?songmd5=" + md5.Trim() + "&view=both";
    }

    private static string GetMochaSongUrl(string sha256)
    {
        return string.IsNullOrWhiteSpace(sha256) ? null : "https://mocha-repository.info/song.php?sha256=" + sha256;
    }

    private static string GetMinIrSongUrl(string sha256)
    {
        return string.IsNullOrWhiteSpace(sha256) ? null : "https://www.gaftalk.com/minir/#/viewer/song/" + sha256 + "/0";
    }
}
