using System;
using System.IO;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>選択した二つの保存先へheader、dataの順でJSONを書き、write失敗は元原因・段階・pathの局所診断を試み、一件のError receiptへ渡します。</summary>
    /// <param name="bmsTable">正本の入力値を変えずに出力する対象表。</param>
    /// <returns>選択と出力の実試行・通知session終端を待つTask。処理済みwrite失敗の正常終端は、両ファイルの出力成功を意味しません。</returns>
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
        try
        {
            string contents;
            string dataContents;
            using (bmsTable.ReaderWriterLock.GetReaderGuard())
            {
                contents = bmsTable.HeaderToJson(Path.GetFileName(fileNameData));
                dataContents = bmsTable.DataToJson();
            }
            string writeStage = "header";
            string writePath = fileNameHeader;
            try
            {
                File.WriteAllText(fileNameHeader, contents);
                writeStage = "data";
                writePath = fileNameData;
                File.WriteAllText(fileNameData, dataContents);
            }
            catch (Exception exception)
            {
                TryLogPlaylistDropSecondaryFailure(exception,
                    "playlist_json_export_write_failed stage=" + writeStage + " path=" + writePath);
                notificationOwner.QueueError(
                    BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist,
                    BeMusicSeeker.Properties.Resources.Error);
            }
        }
        finally
        {
            PublishPlaylistOperationNotificationReceipt(
                session,
                "playlist table JSON export notification");
        }
    }
}
