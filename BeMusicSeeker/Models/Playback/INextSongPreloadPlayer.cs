#nullable enable
using System;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models;

/// <summary>曲の準備・開始と、演奏・終了処理の終端を分けた開始結果です。</summary>
internal sealed record PlaybackStartOperation(Task Ready, Task Completion);

/// <summary>内蔵音声playerの次曲一件の準備と非同期停止の能力境界です。</summary>
internal interface INextSongPreloadPlayer
{
    /// <summary>通常の実対象を開始します。一時配置対象では準備を消費しません。</summary>
    /// <param name="onPlaybackFailure">Ready成功後、自身が停止・解放を所有した故障をgate解放後に一回通知します。通常取消は通知しません。</param>
    PlaybackStartOperation BeginStart(string path, Action<object, EventArgs>? onExit, bool allowPreload,
        Action<Exception>? onPlaybackFailure = null);
    /// <summary>開始した現在曲の次の一件を準備します。入力失敗は採用時に返し、未採用の基盤故障は停止後に通知します。</summary>
    Task PrepareNextAsync(NextSongPreloadInput input, Action<Exception> onFailure);
    /// <summary>先読み取消と再生停止を終端まで待ってruntimeを解放します。</summary>
    Task CloseAsync();
}
