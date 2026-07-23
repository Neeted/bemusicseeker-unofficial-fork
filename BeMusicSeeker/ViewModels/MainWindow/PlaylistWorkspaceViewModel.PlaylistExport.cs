using System;
using System.IO;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal async Task ExportPlaylistTableAsync(BMSTable bmsTable)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException(nameof(bmsTable));
        }

        UiSaveFilePickerResult headerResult = await playlistWorkspaceDialogService.PickSaveFileAsync(
            new UiSaveFilePickerRequest(
                BeMusicSeeker.Properties.Resources.Save_header_file,
                !string.IsNullOrWhiteSpace(bmsTable.header_url)
                    ? Path.GetFileName(bmsTable.Header_url.ToString())
                    : "header.json",
                ".json",
                BeMusicSeeker.Properties.Resources.Json_file_exts,
                addExtension: true));
        ThrowIfPlaylistExportPickerFailed(headerResult, "Header export save picker");
        if (headerResult.Status != UiDialogStatus.Accepted)
        {
            return;
        }

        UiSaveFilePickerResult dataResult = await playlistWorkspaceDialogService.PickSaveFileAsync(
            new UiSaveFilePickerRequest(
                BeMusicSeeker.Properties.Resources.Save_data_file,
                !string.IsNullOrWhiteSpace(bmsTable.data_url)
                    ? Path.GetFileName(bmsTable.Data_url.ToString())
                    : "data.json",
                ".json",
                BeMusicSeeker.Properties.Resources.Json_file_exts,
                addExtension: true));
        ThrowIfPlaylistExportPickerFailed(dataResult, "Data export save picker");
        if (dataResult.Status != UiDialogStatus.Accepted)
        {
            return;
        }

        await ExportPlaylistTableCoreAsync(bmsTable, headerResult.FileName, dataResult.FileName);
    }

    private static void ThrowIfPlaylistExportPickerFailed(UiSaveFilePickerResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no picker result.");
        }
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Error);
    }

    private Task ExportPlaylistTableCoreAsync(BMSTable bmsTable, string fileNameHeader, string fileNameData)
    {
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
