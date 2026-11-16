using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// プレイリスト同期状態の事実を保持する不変値です。
/// 状態名・詳細文・整列順は読取りのたびに現在の資源から作り、言語変更後も同じインスタンスで現在語を返します。
/// </summary>
internal sealed class PlaylistSyncRuntimeStatus
{
    /// <summary>同期結果の種別です。</summary>
    internal PlaylistSyncStatusKind Kind { get; init; }

    /// <summary>同期結果を検査した時刻です。</summary>
    internal DateTime CheckedAt { get; init; }

    /// <summary>同期結果の表ページ位置の文字列です。なければ空です。</summary>
    internal string PageUri { get; init; } = string.Empty;

    /// <summary>失敗時の技術診断の原文です。成功時と未実施では空です。</summary>
    internal string Diagnostic { get; init; } = string.Empty;

    /// <summary>実際の同期結果から作られた状態かを示します。未実施の状態は詳細文を持ちません。</summary>
    internal bool HasResult { get; init; }

    /// <summary>現在語の状態名です。</summary>
    internal string StatusText => PlaylistSyncStatusMapper.GetStatusText(Kind);

    /// <summary>現在語の詳細文です。</summary>
    internal string Detail => PlaylistSyncStatusMapper.BuildDetail(this);

    /// <summary>状態の整列順です。言語に依存しません。</summary>
    internal int StatusSortOrder => PlaylistSyncStatusMapper.GetStatusSortOrder(Kind);

    /// <summary>失敗として扱う状態かを取得します。</summary>
    internal bool HasFailureStatus => Kind != PlaylistSyncStatusKind.None && Kind != PlaylistSyncStatusKind.Ok && Kind != PlaylistSyncStatusKind.Updated;
}
