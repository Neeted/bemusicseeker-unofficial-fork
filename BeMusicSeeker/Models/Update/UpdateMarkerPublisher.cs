using System;
using System.IO;

namespace BeMusicSeeker.Models.Update;

/// <summary>更新の受付・決定マーカーを、書込みと永続化とクローズが完了してから同じディレクトリへ公開します。</summary>
internal static class UpdateMarkerPublisher
{
    /// <summary>一意な一時ファイルへ内容を出力し、完成名へ移動します。失敗時は自身が作成した一時ファイルだけ回収を試みます。</summary>
    /// <param name="finalPath">呼出側で境界検査と必要な長パス変換を済ませた完成ファイルのパス。</param>
    /// <param name="overwrite">既存の完成ファイルを置換する場合はtrue。受付マーカーではfalseを指定します。</param>
    /// <param name="writeContents">同期的に内容だけを出力する処理。ストリームの破棄・保持や別管理主体の完了待ちは行いません。</param>
    internal static void Publish(string finalPath, bool overwrite, Action<Stream> writeContents)
    {
        string temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool ownsTemporaryFile = false;
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                ownsTemporaryFile = true;
                writeContents(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, finalPath, overwrite);
            ownsTemporaryFile = false;
        }
        catch (Exception exception)
        {
            if (ownsTemporaryFile)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception cleanupFailure)
                {
                    // 回収失敗でも公開の元例外とスタックを保ち、残った所有パスを補助診断へ添えます。
                    try
                    {
                        exception.Data["UpdateMarkerTemporaryPath"] = temporaryPath;
                        exception.Data["UpdateMarkerCleanupFailure"] = cleanupFailure;
                    }
                    catch
                    {
                    }
                }
            }
            throw;
        }
    }
}
