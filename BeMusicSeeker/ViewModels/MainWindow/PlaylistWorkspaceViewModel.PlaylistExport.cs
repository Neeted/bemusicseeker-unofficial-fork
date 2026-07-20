using System;
using System.IO;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal Task ExportPlaylistTableAsync(BMSTable bmsTable, string fileNameHeader, string fileNameData)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException(nameof(bmsTable));
        }
        if (fileNameHeader == null)
        {
            throw new ArgumentNullException(nameof(fileNameHeader));
        }
        if (fileNameData == null)
        {
            throw new ArgumentNullException(nameof(fileNameData));
        }

        return Task.Run(() => ExportPlaylistTable(bmsTable, fileNameHeader, fileNameData));
    }

    private void ExportPlaylistTable(BMSTable bmsTable, string fileNameHeader, string fileNameData)
    {
        BMSPlaylist tables = getPlaylistStore();
        tables?.EnsurePlaylistEntriesLoaded(bmsTable, "ExportBMSTable");
        PlaylistOperationNotificationOwner notificationOwner = tables?.OperationNotificationOwner
            ?? new PlaylistOperationNotificationOwner();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = notificationOwner.BeginSession();
        bool temporaryDataUrl = false;
        Uri originalDataUrl = bmsTable.Data_url;
        try
        {
            if (string.IsNullOrWhiteSpace(bmsTable.Data_url?.ToString()))
            {
                bmsTable.Data_url = new Uri(Path.GetFileName(fileNameData), UriKind.Relative);
                temporaryDataUrl = true;
            }

            string contents = bmsTable.HeaderToJson();
            dynamic val = bmsTable.DataToJson();
            try
            {
                File.WriteAllText(fileNameHeader, contents);
                File.WriteAllText(fileNameData, val);
            }
            catch (Exception)
            {
                notificationOwner.QueueError(
                    BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist,
                    BeMusicSeeker.Properties.Resources.Error);
            }
        }
        finally
        {
            if (temporaryDataUrl)
            {
                bmsTable.Data_url = originalDataUrl;
            }
            PublishPlaylistOperationNotificationReceipt(
                session,
                "playlist table JSON export notification");
        }
    }
}
