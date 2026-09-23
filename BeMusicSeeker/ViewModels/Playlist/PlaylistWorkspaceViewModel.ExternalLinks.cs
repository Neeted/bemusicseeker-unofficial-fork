using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal PlaylistTableContextMenuAvailability CapturePlaylistTableContextMenuAvailability(BMSTable table)
    {
        return new PlaylistTableContextMenuAvailability(
            canReload: CanReloadPlaylistTable(table),
            canOpenPage: CanOpenPlaylistTablePage(table),
            canCreateFolder: table != null && !table.is_external_sync,
            canOverwriteLevel: true,
            canRemoveTable: true,
            canOpenProperty: CanOpenPlaylistEditDialog);
    }

    private bool CanReloadPlaylistTable(BMSTable table)
    {
        Uri uri = table?.Page_url ?? table?.Header_url;
        return uri != null && uri.IsAbsoluteUri;
    }

    private bool CanOpenPlaylistTablePage(BMSTable table)
    {
        return table?.Page_url != null || table?.GetAbsoluteHeaderUrl() != null;
    }

    internal bool TryResolvePlaylistTablePageUri(BMSTable table, out Uri uri)
    {
        uri = null;
        Uri candidate = table?.Page_url ?? table?.GetAbsoluteHeaderUrl();
        if (candidate == null)
        {
            return false;
        }
        if (candidate.Scheme != "bmseeker")
        {
            uri = candidate;
            return true;
        }

        string value = candidate.ToString();
        if (value.StartsWith("bmseeker:table.estimation"))
        {
            uri = new Uri("http://walkure.net/hakkyou/bms.html");
            return true;
        }
        if (value.StartsWith("bmseeker:table.recommended"))
        {
            int lr2Id = GetPlaylistLibraryLr2Id();
            if (lr2Id == 0)
            {
                return false;
            }
            uri = new Uri(
                "http://walkure.net/hakkyou/recommended_mypage.html?playerid="
                + lr2Id);
            return true;
        }
        return false;
    }

    internal Task OpenPlaylistSummaryUriAsync(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                playlistUrlBrowserOpenSink(uri);
            }
            catch
            {
                // Summary links historically ignore browser-launch failures.
            }
        });
    }

    internal bool OpenPlaylistTablePage(BMSTable table)
    {
        if (!TryResolvePlaylistTablePageUri(table, out Uri uri))
        {
            return false;
        }

        playlistUrlBrowserOpenSink(uri);
        return true;
    }

    private int GetPlaylistLibraryLr2Id()
    {
        return getPlaylistLibrary()?.LR2ID ?? 0;
    }
}

internal sealed class PlaylistTableContextMenuAvailability
{
    internal PlaylistTableContextMenuAvailability(
        bool canReload,
        bool canOpenPage,
        bool canCreateFolder,
        bool canOverwriteLevel,
        bool canRemoveTable,
        bool canOpenProperty)
    {
        CanReload = canReload;
        CanOpenPage = canOpenPage;
        CanCreateFolder = canCreateFolder;
        CanOverwriteLevel = canOverwriteLevel;
        CanRemoveTable = canRemoveTable;
        CanOpenProperty = canOpenProperty;
    }

    internal bool CanReload { get; }

    internal bool CanOpenPage { get; }

    internal bool CanCreateFolder { get; }

    internal bool CanOverwriteLevel { get; }

    internal bool CanRemoveTable { get; }

    internal bool CanOpenProperty { get; }
}
