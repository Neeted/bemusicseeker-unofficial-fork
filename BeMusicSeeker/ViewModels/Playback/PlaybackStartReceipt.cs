#nullable enable
using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>当該BeginStartで捕捉した不変の対象と実Ready・Completionです。</summary>
internal sealed record PlaybackStartReceipt(long Generation, string ChartPath, string PlaybackPath,
    Task Ready, Task Completion, IPlaybackNativeObservation? NativeObservation);

/// <summary>一回の入力だけが所有する受取先です。既存通知wrapperの成功を業務成功へ読み替えません。</summary>
internal sealed class PlaybackInputReceipt
{
    /// <summary>通知wrapperの内側で実際に開始した業務Taskです。受付拒否時はnullです。</summary>
    internal Task? ActionTask { get; private set; }
    /// <summary>当該入力の最初の開始です。入力不良後の別候補で上書きしません。</summary>
    internal PlaybackStartReceipt? Start { get; private set; }
    /// <summary>製品処理が通知・正常化した最初の開始故障または取消です。</summary>
    internal Exception? Failure { get; private set; }
    /// <summary>呼出し元の実Taskを受け取り、待機や通知は行いません。</summary>
    internal void CaptureAction(Task task) => ActionTask = task;
    /// <summary>Readyを待つ前に開始対象を受け取ります。</summary>
    internal void CaptureStart(PlaybackStartReceipt receipt) => Start ??= receipt;
    /// <summary>製品側の正常化に先立ち、元の不成功を保持します。</summary>
    internal void CaptureFailure(Exception failure) => Failure ??= failure;
}
