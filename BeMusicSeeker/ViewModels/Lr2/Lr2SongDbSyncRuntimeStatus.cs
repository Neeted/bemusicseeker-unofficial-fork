using System;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// LR2同期状態の事実を保持し、表示文言は読取りのたびに現在の資源から作ります。
/// 言語変更後も同じインスタンスで現在語を返すため、翻訳済み文字列は保持しません。
/// </summary>
internal sealed class Lr2SongDbSyncRuntimeStatus
{
    /// <summary>同期状態の種別です。</summary>
    internal Lr2SongDbSyncStatusKind Kind { get; init; }

    /// <summary>保存済みの段階識別子です。表示語への変換は読取り時に行います。</summary>
    internal string Stage { get; init; }

    /// <summary>段階内の処理済み件数です。</summary>
    internal int? StageProcessedCount { get; init; }

    /// <summary>段階内の総件数です。</summary>
    internal int? StageTotalCount { get; init; }

    /// <summary>保存位置の処理済み件数です。</summary>
    internal int? SavedProcessedCount { get; init; }

    /// <summary>保存位置の総件数です。</summary>
    internal int? SavedTotalCount { get; init; }

    /// <summary>保存された失敗診断の原文です。</summary>
    internal string LastError { get; init; }

    /// <summary>現在語の状態名です。</summary>
    internal string StatusText => Lr2SongDbSyncStatusMapper.GetStatusText(Kind);

    /// <summary>現在語の詳細文です。</summary>
    internal string Detail => Lr2SongDbSyncStatusMapper.BuildDetail(this);

    /// <summary>現在語の段階進捗文です。</summary>
    internal string ProgressText => Lr2SongDbSyncStatusMapper.BuildProgressText(this);

    /// <summary>段階進捗の現在値です。</summary>
    internal double ProgressValue => Math.Max(0.0, StageProcessedCount.GetValueOrDefault());

    /// <summary>段階進捗の最大値です。</summary>
    internal double ProgressMaximum => Math.Max(1.0, StageTotalCount.GetValueOrDefault());

    /// <summary>段階進捗を表示できるかを取得します。</summary>
    internal bool HasProgress => Kind == Lr2SongDbSyncStatusKind.Running && StageTotalCount.GetValueOrDefault() > 0;

    /// <summary>警告として扱う状態かを取得します。</summary>
    internal bool HasWarningStatus => Kind is Lr2SongDbSyncStatusKind.Needed or Lr2SongDbSyncStatusKind.Running
        or Lr2SongDbSyncStatusKind.Failed or Lr2SongDbSyncStatusKind.Incomplete or Lr2SongDbSyncStatusKind.Cancelled;

    /// <summary>実行中LR2の発生元と親操作の表示識別です。</summary>
    internal OperationProgressRequest Request { get; init; }

    /// <summary>状態を検査した時刻です。</summary>
    internal DateTime CheckedAt { get; init; }
}
