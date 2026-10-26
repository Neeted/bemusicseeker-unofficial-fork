using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
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

        using var input = new PlaylistJsonExportDialogViewModel();
        UiWindowDialogResult<PlaylistJsonExportPaths> result = await playlistWorkspaceDialogService.ShowWindowAsync(
            new UiWindowDialogRequest<PlaylistJsonExportDialog, PlaylistJsonExportPaths>(
                () => new PlaylistJsonExportDialog(input, playlistWorkspaceDialogService,
                    !string.IsNullOrWhiteSpace(bmsTable.header_url) ? Path.GetFileName(bmsTable.Header_url.ToString()) : "header.json",
                    !string.IsNullOrWhiteSpace(bmsTable.data_url) ? Path.GetFileName(bmsTable.Data_url.ToString()) : "data.json"),
                _ => input.CapturePaths()));
        if (result?.Status is UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser) { return; }
        if (result?.Status != UiDialogStatus.Accepted || result.Value == null)
        {
            throw new InvalidOperationException("Playlist JSON export dialog failed: " + result?.Status, result?.Error);
        }

        // UIの有効状態を信用せず、正本の遅延読込み・通知session・ファイル生成より先に同じ判定を通します。
        string rejection = PlaylistJsonExportDialogViewModel.ValidatePaths(result.Value.HeaderPath, result.Value.DataPath);
        if (rejection.Length != 0)
        {
            await playlistWorkspaceDialogService.ShowMessageAsync(UiMessageRequest.CreateError(rejection,
                BeMusicSeeker.Properties.Resources.Error));
            return;
        }
        string headerPath = LongPathFileSystem.NormalizePathForStorage(result.Value.HeaderPath);
        string dataPath = LongPathFileSystem.NormalizePathForStorage(result.Value.DataPath);
        await Task.Run(() => ExportPlaylistTable(bmsTable, headerPath, dataPath));
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
                // 明示的なfile URIに揃えて、予約文字のescapeを保ったまま相対化します。
                var headerUri = new Uri(new Uri(fileNameHeader).AbsoluteUri);
                var dataUri = new Uri(new Uri(fileNameData).AbsoluteUri);
                // 異なるドライブ・共有ルートではMakeRelativeUriのドライブ付き相対値を使わず、file:を明示します。
                string outputDataReference = string.Equals(Path.GetPathRoot(fileNameHeader), Path.GetPathRoot(fileNameData), StringComparison.OrdinalIgnoreCase)
                    ? headerUri.MakeRelativeUri(dataUri).OriginalString
                    : dataUri.AbsoluteUri;
                contents = bmsTable.HeaderToJson(outputDataReference);
                dataContents = bmsTable.DataToJson();
            }
            string writeStage = "header";
            string writePath = fileNameHeader;
            try
            {
                WritePlaylistJson(fileNameHeader, contents);
                writeStage = "data";
                writePath = fileNameData;
                WritePlaylistJson(fileNameData, dataContents);
            }
            catch (Exception exception)
            {
                TryLogPlaylistDropSecondaryFailure(exception,
                    "playlist_json_export_write_failed stage=" + writeStage + " path=" + writePath);
                notificationOwner.QueueError(
                    writeStage == "data"
                        ? BeMusicSeeker.Properties.Resources.Playlist_json_export_partial_failure
                        : BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist,
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
    private static void WritePlaylistJson(string path, string contents)
    {
        AtomicFileWriter.Write(path, stream =>
        {
            // StreamWriterのバッファを排出して閉じた後、AtomicFileWriterがstreamを閉じて公開します。
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(contents);
        });
    }
}
