using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>表の編集・再読込みと、内蔵表のページを開かないメニュー可否を捕捉します。</summary>
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
        return TryResolvePlaylistTablePageUri(table, out _);
    }

    /// <summary>外部で開けるページURIを返します。内蔵表にはページを提供しません。</summary>
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

        return false;
    }

    /// <summary>サマリーの外部URIをブラウザーへ渡します。内蔵表URIは開きません。</summary>
    internal Task OpenPlaylistSummaryUriAsync(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || uri.Scheme == "bmseeker")
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

    /// <summary>開ける外部表のページをブラウザーへ渡し、内蔵表では false を返します。</summary>
    internal bool OpenPlaylistTablePage(BMSTable table)
    {
        if (!TryResolvePlaylistTablePageUri(table, out Uri uri))
        {
            return false;
        }

        playlistUrlBrowserOpenSink(uri);
        return true;
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
