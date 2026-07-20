using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private const string PlaylistClearLampUri = "http://xyzzz.net/bms/clearlamp";

    internal bool CanReloadPlaylistTable(BMSTable table)
    {
        Uri uri = table?.Page_url ?? table?.Header_url;
        return uri != null && uri.IsAbsoluteUri;
    }

    internal bool CanOpenPlaylistTablePage(BMSTable table)
    {
        return table?.Page_url != null || table?.GetAbsoluteHeaderUrl() != null;
    }

    internal bool CanOpenPlaylistTableClearLamp(BMSTable table)
    {
        return table?.Page_url != null
            && table.Page_url.Scheme != "bmseeker"
            && table.is_external_sync
            && GetPlaylistLibraryLr2Id() != 0;
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

    internal bool TryResolvePlaylistTableClearLampUri(BMSTable table, out Uri uri)
    {
        uri = null;
        if (table?.Page_url == null
            || table.Page_url.Scheme == "bmseeker"
            || !table.is_external_sync)
        {
            return false;
        }

        int lr2Id = GetPlaylistLibraryLr2Id();
        if (lr2Id == 0)
        {
            return false;
        }

        uri = new Uri(
            PlaylistClearLampUri
            + "?lr2ID="
            + Uri.EscapeDataString(lr2Id.ToString())
            + "&table_url="
            + Uri.EscapeDataString(table.Page_url.ToString()));
        return true;
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

    internal bool OpenPlaylistTableClearLamp(BMSTable table)
    {
        if (!TryResolvePlaylistTableClearLampUri(table, out Uri uri))
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
