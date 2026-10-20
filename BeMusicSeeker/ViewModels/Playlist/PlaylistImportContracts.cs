#nullable enable
using System;

namespace BeMusicSeeker.ViewModels;

/// <summary>一回のインポート要求を開始できたかと、未開始の理由を表します。</summary>
internal enum PlaylistImportStartStatus
{
    /// <summary>既存の取得・登録処理が要求を所有しました。</summary>
    Started,

    /// <summary>別の変更操作が実行中で、要求を予約せず拒否しました。</summary>
    Busy,

    /// <summary>必須のプレイリスト準備が完了していません。</summary>
    NotReady,

    /// <summary>終了要求または新規操作の受付閉鎖により開始できません。</summary>
    ShuttingDown,

    /// <summary>入力が空、または同URLの登録済みを除くと対象がありません。</summary>
    NoTargets
}

/// <summary>画面の閉鎖判断に使う受付結果です。実行後の成否は既存の完了集計が通知します。</summary>
/// <param name="Status">受付の成否と未開始の理由。</param>
/// <param name="StartedCount">今回、取得・登録の処理へ渡したURI数。</param>
/// <param name="SkippedDuplicateUrlCount">最新の既存表とURLが一致し、通信前に除いたURI数。</param>
internal sealed record PlaylistImportStartResult(
    PlaylistImportStartStatus Status,
    int StartedCount = 0,
    int SkippedDuplicateUrlCount = 0);

/// <summary>事前表示と受付時の再照合に使う既存表の値です。表の可変状態やロックを画面へ渡しません。</summary>
/// <param name="Name">保存済みの名前。</param>
/// <param name="PageUrl">保存済みのページURI。未設定ならnull。</param>
/// <param name="HeaderUrl">解決済みの絶対ヘッダーURI。未設定ならnull。</param>
internal sealed record PlaylistImportExistingTable(string Name, Uri? PageUrl, Uri? HeaderUrl);
