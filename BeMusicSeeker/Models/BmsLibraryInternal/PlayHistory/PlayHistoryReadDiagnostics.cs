using System;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 履歴読取りの元例外を、結果へ変換する局所で記録します。
/// </summary>
internal static class PlayHistoryReadDiagnostics
{
    /// <summary>
    /// 取得元・段階・対象パスと元例外の記録を試み、通常のログ失敗を読取り結果へ混ぜません。
    /// </summary>
    /// <param name="exception">読取りまたは変換で発生した元例外。</param>
    /// <param name="provider">履歴の取得元。</param>
    /// <param name="phase">失敗した読取りまたは変換の段階。</param>
    /// <param name="sourcePath">失敗した処理の対象DBパス。</param>
    internal static void Report(Exception exception, string provider, string phase, string sourcePath)
    {
        try
        {
            NLogWrapper.FileLogger?.Warn(exception,
                "Play history read failed. Provider={0}, phase={1}, path={2}",
                provider, phase, sourcePath);
        }
        catch (Exception loggingException) when (loggingException is not OutOfMemoryException)
        {
            // 純粋な診断の失敗で読取り結果を置き換えず、再通知もしない。
        }
    }
}
